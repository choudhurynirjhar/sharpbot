using System.Text;

namespace Sharpbot.Agent.Tools;

/// <summary>
/// Tool for managing background process sessions.
/// Actions: list, poll, log, write, kill, clear, remove.
/// </summary>
public sealed class ProcessTool : ToolBase
{
    private readonly ProcessSessionManager _manager;

    public ProcessTool(ProcessSessionManager manager) => _manager = manager;

    public override string Name => "process";

    public override string Description =>
        "Manage background processes started by the exec tool. " +
        "Actions: list (show all sessions), poll (get new output), log (get full output), " +
        "write (send stdin), kill (terminate), clear (remove finished), remove (kill+clear).";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["action"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Action to perform: list, poll, log, write, kill, clear, remove",
                ["enum"] = new[] { "list", "poll", "log", "write", "kill", "clear", "remove" },
            },
            ["session_id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Session ID (required for all actions except 'list')",
            },
            ["data"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Data to send to stdin (for 'write' action)",
            },
            ["eof"] = new Dictionary<string, object?>
            {
                ["type"] = "boolean",
                ["description"] = "Close stdin after writing (for 'write' action)",
            },
            ["offset"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Line offset for 'log' action (negative = from end)",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Max lines to return for 'log' action",
            },
        },
        ["required"] = new[] { "action" },
    };

    public override Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var action = GetString(args, "action").ToLowerInvariant();

        return Task.FromResult(action switch
        {
            "list" => HandleList(),
            "poll" => HandlePoll(args),
            "log" => HandleLog(args),
            "write" => HandleWrite(args),
            "kill" => HandleKill(args),
            "clear" => HandleClear(args),
            "remove" => HandleRemove(args),
            _ => $"Error: Unknown action '{action}'. Valid actions: list, poll, log, write, kill, clear, remove",
        });
    }

    private string HandleList()
    {
        var sessions = _manager.ListSessions();
        if (sessions.Count == 0) return "No background sessions.";

        var sb = new StringBuilder();
        sb.AppendLine($"Background sessions ({sessions.Count}):");
        sb.AppendLine();

        foreach (var s in sessions.OrderByDescending(s => s.StartedAt))
        {
            var status = s.IsRunning ? "running" : $"exited ({s.ExitCode})";
            var elapsed = DateTime.UtcNow - s.StartedAt;
            var elapsedStr = elapsed.TotalSeconds < 60 ? $"{elapsed.TotalSeconds:F0}s" :
                             elapsed.TotalMinutes < 60 ? $"{elapsed.TotalMinutes:F1}m" :
                             $"{elapsed.TotalHours:F1}h";

            sb.AppendLine($"  [{s.SessionId}] {s.DerivedName}");
            sb.AppendLine($"    Status: {status} | PID: {s.Pid} | Elapsed: {elapsedStr} | Output: {s.OutputLength} chars");
        }

        return sb.ToString().TrimEnd();
    }

    private string HandlePoll(Dictionary<string, object?> args)
    {
        var session = ResolveSession(args);
        if (session == null) return SessionNotFoundError(args);

        var newOutput = session.PollNewOutput();
        var sb = new StringBuilder();

        if (!session.IsRunning)
        {
            sb.AppendLine($"Process exited with code {session.ExitCode}.");
            if (!string.IsNullOrEmpty(newOutput))
            {
                sb.AppendLine("New output:");
                sb.Append(newOutput);
            }
            else
            {
                sb.AppendLine("No new output since last poll.");
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(newOutput))
            {
                sb.AppendLine("New output:");
                sb.Append(newOutput);
            }
            else
            {
                sb.AppendLine("(still running, no new output)");
            }
        }

        return TruncateResult(sb.ToString().TrimEnd());
    }

    private string HandleLog(Dictionary<string, object?> args)
    {
        var session = ResolveSession(args);
        if (session == null) return SessionNotFoundError(args);

        var offset = GetInt(args, "offset");
        var limit = GetInt(args, "limit");

        var log = session.GetLog(offset, limit);
        if (string.IsNullOrEmpty(log)) return "(no output)";
        return TruncateResult(log);
    }

    private string HandleWrite(Dictionary<string, object?> args)
    {
        var session = ResolveSession(args);
        if (session == null) return SessionNotFoundError(args);

        var data = GetString(args, "data");
        if (string.IsNullOrEmpty(data)) return "Error: 'data' parameter is required for write action.";

        var eof = GetBool(args, "eof");

        try
        {
            session.WriteStdin(data, eof);
            return eof
                ? $"Wrote {data.Length} chars to stdin and closed it."
                : $"Wrote {data.Length} chars to stdin.";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private string HandleKill(Dictionary<string, object?> args)
    {
        var session = ResolveSession(args);
        if (session == null) return SessionNotFoundError(args);

        if (!session.IsRunning) return $"Session {session.SessionId} has already exited (code {session.ExitCode}).";

        session.Kill();
        return $"Killed session {session.SessionId} (PID {session.Pid}).";
    }

    private string HandleClear(Dictionary<string, object?> args)
    {
        var sessionId = GetString(args, "session_id");
        if (string.IsNullOrEmpty(sessionId)) return "Error: 'session_id' is required.";

        var session = _manager.GetSession(sessionId);
        if (session == null) return SessionNotFoundError(args);
        if (session.IsRunning) return $"Error: Session {sessionId} is still running. Use 'kill' first or 'remove' to kill+clear.";

        _manager.ClearSession(sessionId);
        return $"Cleared finished session {sessionId}.";
    }

    private string HandleRemove(Dictionary<string, object?> args)
    {
        var sessionId = GetString(args, "session_id");
        if (string.IsNullOrEmpty(sessionId)) return "Error: 'session_id' is required.";

        var removed = _manager.RemoveSession(sessionId);
        return removed
            ? $"Removed session {sessionId} (killed if running)."
            : SessionNotFoundError(args);
    }

    private ProcessSession? ResolveSession(Dictionary<string, object?> args)
    {
        var sessionId = GetString(args, "session_id");
        if (string.IsNullOrEmpty(sessionId)) return null;
        return _manager.GetSession(sessionId);
    }

    private static string SessionNotFoundError(Dictionary<string, object?> args)
    {
        var sessionId = args.TryGetValue("session_id", out var v) ? v?.ToString() : null;
        return string.IsNullOrEmpty(sessionId)
            ? "Error: 'session_id' is required for this action."
            : $"Error: Session '{sessionId}' not found. Use action 'list' to see active sessions.";
    }

    private static string TruncateResult(string result)
    {
        const int maxLen = 10000;
        if (result.Length > maxLen)
            result = result[..maxLen] + $"\n... (truncated, {result.Length - maxLen} more chars)";
        return result;
    }
}
