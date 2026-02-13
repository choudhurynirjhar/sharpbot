using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Sharpbot.Agent;

namespace Sharpbot.Agent.Tools;

/// <summary>Tool to execute shell commands with optional background process support.</summary>
public sealed class ExecTool : ToolBase
{
    private readonly int _timeout;
    private readonly int _defaultYieldMs;
    private readonly string? _workingDir;
    private readonly IReadOnlyList<Regex> _denyPatterns;
    private readonly bool _restrictToWorkspace;
    private readonly ProcessSessionManager? _processManager;
    private readonly string _security;
    private readonly string _ask;
    private readonly string _askFallback;
    private readonly int _approvalTimeoutSec;
    private readonly HashSet<string> _safeBins;
    private readonly ExecApprovalManager? _approvalManager;

    /// <summary>Pre-compiled deny patterns — avoids recompiling on every <see cref="ExecuteAsync"/> call.</summary>
    private static readonly Regex[] DefaultDenyPatterns =
    [
        new(@"\brm\s+-[rf]{1,2}\b", RegexOptions.Compiled),
        new(@"\bdel\s+/[fq]\b", RegexOptions.Compiled),
        new(@"\brmdir\s+/s\b", RegexOptions.Compiled),
        new(@"\b(format|mkfs|diskpart)\b", RegexOptions.Compiled),
        new(@"\bdd\s+if=", RegexOptions.Compiled),
        new(@">\s*/dev/sd", RegexOptions.Compiled),
        new(@"\b(shutdown|reboot|poweroff)\b", RegexOptions.Compiled),
        new(@":\(\)\s*\{.*\};\s*:", RegexOptions.Compiled),
    ];

    public ExecTool(
        int timeout = 60,
        int defaultYieldMs = 10_000,
        string? workingDir = null,
        IReadOnlyList<Regex>? denyPatterns = null,
        bool restrictToWorkspace = false,
        ProcessSessionManager? processManager = null,
        string security = "allowlist",
        string ask = "on-miss",
        string askFallback = "deny",
        int approvalTimeoutSec = 120,
        IEnumerable<string>? safeBins = null,
        ExecApprovalManager? approvalManager = null)
    {
        _timeout = timeout;
        _defaultYieldMs = defaultYieldMs;
        _workingDir = workingDir;
        _restrictToWorkspace = restrictToWorkspace;
        _denyPatterns = denyPatterns ?? DefaultDenyPatterns;
        _processManager = processManager;
        _security = NormalizeSecurity(security);
        _ask = NormalizeAsk(ask);
        _askFallback = NormalizeSecurity(askFallback);
        _approvalTimeoutSec = Math.Max(5, approvalTimeoutSec);
        _safeBins = new HashSet<string>((safeBins ?? []).Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)), StringComparer.OrdinalIgnoreCase);
        _approvalManager = approvalManager;
    }

    public override string Name => "exec";
    public override string Description =>
        "Execute a shell command. Set background=true or yield_ms to run long tasks in the background. " +
        "Use the 'process' tool to check on background tasks.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["command"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The shell command to execute" },
            ["working_dir"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Optional working directory for the command" },
            ["background"] = new Dictionary<string, object?> { ["type"] = "boolean", ["description"] = "If true, run the command in the background immediately and return a session ID. Use the 'process' tool to poll output." },
            ["yield_ms"] = new Dictionary<string, object?> { ["type"] = "integer", ["description"] = "Auto-background: wait this many milliseconds for the command to finish. If it hasn't finished, background it and return a session ID. Default: 10000." },
        },
        ["required"] = new[] { "command" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var command = GetString(args, "command");
        var workingDir = GetString(args, "working_dir", _workingDir ?? Directory.GetCurrentDirectory());
        var background = GetBool(args, "background");
        var yieldMs = GetInt(args, "yield_ms");

        var guardError = GuardCommand(command, workingDir);
        if (guardError != null) return guardError;

        // Policy gate: security/allowlist/approval.
        var policyResult = await EnforceApprovalPolicyAsync(command, workingDir);
        if (policyResult is not null)
            return policyResult;

        // ── Explicit background ────────────────────────────────────
        if (background && _processManager != null)
            return StartBackground(command, workingDir);

        // ── Auto-yield (yield_ms) ──────────────────────────────────
        if (_processManager != null && (yieldMs.HasValue || background))
        {
            var yieldTimeout = yieldMs ?? _defaultYieldMs;
            return await RunWithYieldAsync(command, workingDir, yieldTimeout);
        }

        // ── Foreground (original behavior) ─────────────────────────
        return await RunForegroundAsync(command, workingDir);
    }

    /// <summary>Start a command immediately in the background.</summary>
    private string StartBackground(string command, string workingDir)
    {
        var session = _processManager!.StartSession(command, workingDir);
        return $"Process started in background.\n" +
               $"Session ID: {session.SessionId}\n" +
               $"PID: {session.Pid}\n" +
               $"Use the 'process' tool with action 'poll' or 'log' to check output.";
    }

    /// <summary>
    /// Run foreground with auto-yield: wait up to yieldMs for the command to finish.
    /// If it hasn't finished, background it.
    /// </summary>
    private async Task<string> RunWithYieldAsync(string command, string workingDir, int yieldMs)
    {
        var session = _processManager!.StartSession(command, workingDir);

        var exited = await session.WaitForExitAsync(yieldMs);
        if (exited)
        {
            // Finished within yield window — return output like foreground
            var output = session.PollNewOutput();
            var exitCode = session.ExitCode ?? 0;
            _processManager.RemoveSession(session.SessionId);

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(output)) parts.Add(output.TrimEnd());
            if (exitCode != 0) parts.Add($"\nExit code: {exitCode}");

            var result = parts.Count > 0 ? string.Join("\n", parts) : "(no output)";
            return TruncateOutput(result);
        }

        // Still running — background it
        var tail = session.GetTail(500);
        return $"Command still running — backgrounded.\n" +
               $"Session ID: {session.SessionId}\n" +
               $"PID: {session.Pid}\n" +
               $"Output so far:\n{tail}\n" +
               $"Use the 'process' tool with action 'poll' to check for new output.";
    }

    /// <summary>Original synchronous foreground execution.</summary>
    private async Task<string> RunForegroundAsync(string command, string workingDir)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/sh",
                Arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return "Error: Failed to start process";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeout));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                return $"Error: Command timed out after {_timeout} seconds";
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(stdout)) parts.Add(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) parts.Add($"STDERR:\n{stderr}");
            if (process.ExitCode != 0) parts.Add($"\nExit code: {process.ExitCode}");

            var result = parts.Count > 0 ? string.Join("\n", parts) : "(no output)";
            return TruncateOutput(result);
        }
        catch (Exception e)
        {
            return $"Error executing command: {e.Message}";
        }
    }

    private static string TruncateOutput(string result)
    {
        const int maxLen = 10000;
        if (result.Length > maxLen)
            result = result[..maxLen] + $"\n... (truncated, {result.Length - maxLen} more chars)";
        return result;
    }

    private async Task<string?> EnforceApprovalPolicyAsync(string command, string workingDir)
    {
        if (_security == "full")
            return null;

        if (_security == "deny")
            return "Error: Command blocked by exec policy (security=deny).";

        var hasShellChaining = ContainsShellControlOperators(command);
        var executable = ResolveExecutable(command, workingDir);
        var allowlistSatisfied = executable.ResolvedPath != null &&
                                 _approvalManager?.IsAllowlisted(executable.ResolvedPath) == true;
        var safeBinSatisfied = IsSafeBinUsage(command, executable.Name);
        var policySatisfied = allowlistSatisfied || safeBinSatisfied;

        var needsApproval = _ask == "always" || (_ask == "on-miss" && !policySatisfied);
        if (!needsApproval)
            return policySatisfied ? null : "Error: Command blocked by exec policy (allowlist miss).";

        if (_approvalManager == null)
            return ApplyFallbackPolicy(policySatisfied, "approval system unavailable");

        if (hasShellChaining && _security == "allowlist")
        {
            // Keep allowlist mode strict against shell composition bypasses.
            return "Error: Command blocked by exec policy (unsupported shell control operators in allowlist mode).";
        }

        var timeout = TimeSpan.FromSeconds(_approvalTimeoutSec);
        var id = _approvalManager.CreateRequest(
            command: command,
            workingDirectory: workingDir,
            security: _security,
            ask: _ask,
            resolvedExecutablePath: executable.ResolvedPath,
            timeout: timeout);

        var decision = await _approvalManager.WaitForDecisionAsync(id);
        if (decision is null)
            return ApplyFallbackPolicy(policySatisfied, $"approval timeout ({_approvalTimeoutSec}s)");

        if (decision == ExecApprovalDecision.Deny)
            return $"Error: Command denied by operator approval ({id[..8]}).";

        if (decision == ExecApprovalDecision.AllowAlways && !string.IsNullOrEmpty(executable.ResolvedPath))
            _approvalManager.AddAllowlist(executable.ResolvedPath);

        return null;
    }

    private string? ApplyFallbackPolicy(bool allowlistSatisfied, string reason)
    {
        if (_askFallback == "full")
            return null;

        if (_askFallback == "allowlist" && allowlistSatisfied)
            return null;

        return $"Error: Command blocked by exec approval fallback ({reason}, ask_fallback={_askFallback}).";
    }

    private static string NormalizeSecurity(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v is "deny" or "allowlist" or "full" ? v : "allowlist";
    }

    private static string NormalizeAsk(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v is "off" or "on-miss" or "always" ? v : "on-miss";
    }

    private static bool ContainsShellControlOperators(string command)
    {
        // Conservative gate for allowlist mode.
        var controls = new[] { "&&", "||", ";", "|", ">", "<", "$(", "`" };
        return controls.Any(command.Contains);
    }

    private bool IsSafeBinUsage(string command, string? executableName)
    {
        if (string.IsNullOrEmpty(executableName))
            return false;
        if (!_safeBins.Contains(executableName))
            return false;

        var tokens = Tokenize(command);
        if (tokens.Count <= 1)
            return true;

        foreach (var token in tokens.Skip(1))
        {
            if (token == "-")
                continue;
            if (token.StartsWith('-'))
                continue;
            if (LooksLikePath(token))
                return false;
        }

        return true;
    }

    private static bool LooksLikePath(string token)
    {
        if (token.StartsWith("./") || token.StartsWith("../") || token.StartsWith("~/") || token.StartsWith('/'))
            return true;
        return Regex.IsMatch(token, @"^[A-Za-z]:[\\/]");
    }

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = "";
        var inSingle = false;
        var inDouble = false;

        foreach (var ch in command)
        {
            if (ch == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }

            if (ch == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inSingle && !inDouble)
            {
                if (!string.IsNullOrEmpty(current))
                {
                    tokens.Add(current);
                    current = "";
                }
                continue;
            }

            current += ch;
        }

        if (!string.IsNullOrEmpty(current))
            tokens.Add(current);

        return tokens;
    }

    private static (string? Name, string? ResolvedPath) ResolveExecutable(string command, string workingDir)
    {
        var tokens = Tokenize(command);
        if (tokens.Count == 0)
            return (null, null);

        var raw = tokens[0];
        if (string.IsNullOrWhiteSpace(raw))
            return (null, null);

        if (Path.IsPathRooted(raw) || raw.Contains('/') || raw.Contains('\\'))
        {
            var full = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(workingDir, raw));
            var name = Path.GetFileNameWithoutExtension(full);
            return (name, File.Exists(full) ? full : full);
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, raw + (ext.StartsWith('.') || string.IsNullOrEmpty(ext) ? ext : "." + ext));
                if (File.Exists(candidate))
                    return (Path.GetFileNameWithoutExtension(candidate), candidate);
            }
        }

        return (raw, null);
    }

    private string? GuardCommand(string command, string cwd)
    {
        var cmd = command.Trim();
        var lower = cmd.ToLowerInvariant();

        foreach (var pattern in _denyPatterns)
        {
            if (pattern.IsMatch(lower))
                return "Error: Command blocked by safety guard (dangerous pattern detected)";
        }

        if (_restrictToWorkspace)
        {
            if (cmd.Contains("..\\") || cmd.Contains("../"))
                return "Error: Command blocked by safety guard (path traversal detected)";

            var cwdPath = Path.GetFullPath(cwd);

            // Check for absolute paths in the command
            var winPaths = Regex.Matches(cmd, @"[A-Za-z]:\\[^\\\""']+");
            var posixPaths = Regex.Matches(cmd, @"/[^\s\""']+");

            foreach (Match m in winPaths.Concat(posixPaths))
            {
                try
                {
                    var p = Path.GetFullPath(m.Value);
                    if (!p.StartsWith(cwdPath, StringComparison.OrdinalIgnoreCase) && p != cwdPath)
                        return "Error: Command blocked by safety guard (path outside working dir)";
                }
                catch { /* ignore parse errors */ }
            }
        }

        return null;
    }
}
