using Microsoft.Extensions.Logging;

namespace Sharpbot.Heartbeat;

/// <summary>
/// Periodic heartbeat service that wakes the agent to check for tasks.
/// The agent reads HEARTBEAT.md from the workspace and executes any
/// tasks listed there.
/// </summary>
public sealed class HeartbeatService : IDisposable
{
    private const int DefaultIntervalSeconds = 30 * 60; // 30 minutes
    private const string HeartbeatPrompt = """
        Read HEARTBEAT.md in your workspace (if it exists).
        Follow any instructions or tasks listed there.
        If nothing needs attention, reply with just: HEARTBEAT_OK
        """;
    private const string HeartbeatOkToken = "HEARTBEAT_OK";

    private readonly string _workspace;
    private readonly Func<string, Task<string>>? _onHeartbeat;
    private readonly int _intervalSeconds;
    private readonly bool _enabled;
    private readonly ILogger? _logger;
    private volatile bool _running;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public HeartbeatService(
        string workspace,
        Func<string, Task<string>>? onHeartbeat = null,
        int intervalSeconds = DefaultIntervalSeconds,
        bool enabled = true,
        ILogger? logger = null)
    {
        _workspace = workspace;
        _onHeartbeat = onHeartbeat;
        _intervalSeconds = intervalSeconds;
        _enabled = enabled;
        _logger = logger;
    }

    private string HeartbeatFile => Path.Combine(_workspace, "HEARTBEAT.md");

    private string? ReadHeartbeatFile()
    {
        if (!File.Exists(HeartbeatFile)) return null;
        try { return File.ReadAllText(HeartbeatFile); }
        catch { return null; }
    }

    private static bool IsHeartbeatEmpty(string? content)
    {
        if (string.IsNullOrEmpty(content)) return true;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith("<!--"))
                continue;
            if (line is "- [ ]" or "* [ ]" or "- [x]" or "* [x]")
                continue;
            return false; // Found actionable content
        }

        return true;
    }

    /// <summary>Start the heartbeat service.</summary>
    public Task StartAsync()
    {
        if (!_enabled)
        {
            _logger?.LogInformation("Heartbeat disabled");
            return Task.CompletedTask;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        _task = RunLoopAsync(_cts.Token);
        _logger?.LogInformation("Heartbeat started (every {Interval}s)", _intervalSeconds);
        return Task.CompletedTask;
    }

    /// <summary>Stop the heartbeat service.</summary>
    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct);
                if (_running) await TickAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _logger?.LogError(e, "Heartbeat error");
            }
        }
    }

    private async Task TickAsync()
    {
        var content = ReadHeartbeatFile();
        if (IsHeartbeatEmpty(content))
        {
            _logger?.LogDebug("Heartbeat: no tasks (HEARTBEAT.md empty)");
            return;
        }

        _logger?.LogInformation("Heartbeat: checking for tasks...");

        if (_onHeartbeat is null) return;

        try
        {
            var response = await _onHeartbeat(HeartbeatPrompt);
            if (response.Contains("HEARTBEATOK", StringComparison.OrdinalIgnoreCase))
                _logger?.LogInformation("Heartbeat: OK (no action needed)");
            else
                _logger?.LogInformation("Heartbeat: completed task");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Heartbeat execution failed");
        }
    }

    /// <summary>Manually trigger a heartbeat.</summary>
    public async Task<string?> TriggerNowAsync()
    {
        if (_onHeartbeat is not null)
            return await _onHeartbeat(HeartbeatPrompt);
        return null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
