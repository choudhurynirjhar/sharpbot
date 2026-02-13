using System.Collections.Concurrent;
using System.Text.Json;
using Sharpbot.Config;

namespace Sharpbot.Agent;

public enum ExecApprovalDecision
{
    AllowOnce,
    AllowAlways,
    Deny,
}

public sealed record ExecApprovalRequest
{
    public required string Id { get; init; }
    public required string Command { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? ResolvedExecutablePath { get; init; }
    public required string Security { get; init; }
    public required string Ask { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
}

internal sealed record ExecApprovalPending
{
    public required ExecApprovalRequest Request { get; init; }
    public required TaskCompletionSource<ExecApprovalDecision> Tcs { get; init; }
}

internal sealed record ExecApprovalsFile
{
    public int Version { get; init; } = 1;
    public List<string> Allowlist { get; init; } = [];
}

/// <summary>
/// Holds pending exec approvals and a persistent executable allowlist.
/// </summary>
public sealed class ExecApprovalManager
{
    private readonly ConcurrentDictionary<string, ExecApprovalPending> _pending = new();
    private readonly HashSet<string> _allowlist;
    private readonly object _allowlistLock = new();
    private readonly string _filePath;

    public ExecApprovalManager(ExecToolConfig execConfig)
    {
        _filePath = Path.Combine(Utils.Helpers.GetDataPath(), "exec-approvals.json");

        // Merge config allowlist + persisted allowlist into one effective set.
        _allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in execConfig.Allowlist)
            AddAllowlistInternal(entry);
        foreach (var entry in LoadPersistedAllowlist())
            AddAllowlistInternal(entry);
    }

    public string CreateRequest(
        string command,
        string workingDirectory,
        string security,
        string ask,
        string? resolvedExecutablePath,
        TimeSpan timeout)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var pending = new ExecApprovalPending
        {
            Request = new ExecApprovalRequest
            {
                Id = id,
                Command = command,
                WorkingDirectory = workingDirectory,
                ResolvedExecutablePath = resolvedExecutablePath,
                Security = security,
                Ask = ask,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.Add(timeout),
            },
            Tcs = new TaskCompletionSource<ExecApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        _pending[id] = pending;
        return id;
    }

    public async Task<ExecApprovalDecision?> WaitForDecisionAsync(string approvalId, CancellationToken ct = default)
    {
        if (!_pending.TryGetValue(approvalId, out var pending))
            return null;

        var timeout = pending.Request.ExpiresAtUtc - DateTime.UtcNow;
        if (timeout <= TimeSpan.Zero)
        {
            _pending.TryRemove(approvalId, out _);
            return null;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            return await pending.Tcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(approvalId, out _);
        }
    }

    public bool Resolve(string approvalId, ExecApprovalDecision decision)
    {
        if (!_pending.TryGetValue(approvalId, out var pending))
            return false;

        return pending.Tcs.TrySetResult(decision);
    }

    public List<ExecApprovalRequest> GetPending() =>
        _pending.Values
            .Select(p => p.Request)
            .OrderBy(p => p.CreatedAtUtc)
            .ToList();

    public bool IsAllowlisted(string executablePath)
    {
        lock (_allowlistLock)
        {
            return _allowlist.Any(pattern => MatchesPattern(pattern, executablePath));
        }
    }

    public List<string> GetAllowlist()
    {
        lock (_allowlistLock)
        {
            return [.. _allowlist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public void AddAllowlist(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return;

        lock (_allowlistLock)
        {
            AddAllowlistInternal(executablePath);
            PersistAllowlistUnsafe();
        }
    }

    private void AddAllowlistInternal(string value)
    {
        var normalized = value.Trim();
        if (string.IsNullOrEmpty(normalized))
            return;

        _allowlist.Add(normalized);
    }

    private List<string> LoadPersistedAllowlist()
    {
        try
        {
            if (!File.Exists(_filePath))
                return [];

            var json = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize<ExecApprovalsFile>(json);
            return parsed?.Allowlist ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void PersistAllowlistUnsafe()
    {
        var payload = new ExecApprovalsFile
        {
            Version = 1,
            Allowlist = [.. _allowlist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)],
        };

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_filePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    private static bool MatchesPattern(string pattern, string input)
    {
        // Treat plain entries as exact paths; support glob-like '*' and '?' patterns.
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(Path.GetFullPath(pattern), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase);

        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            input,
            regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
