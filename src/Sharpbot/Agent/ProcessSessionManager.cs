using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Sharpbot.Agent;

/// <summary>
/// Manages background <see cref="ProcessSession"/> instances.
/// Provides start, list, get, and remove operations with automatic cleanup of finished sessions.
/// In-memory only — sessions are lost on process restart.
/// </summary>
public sealed class ProcessSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ProcessSession> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly int _maxOutputChars;
    private readonly int _backgroundTimeoutSec;
    private readonly int _sessionCleanupMs;
    private readonly IReadOnlyList<Regex> _denyPatterns;
    private readonly bool _restrictToWorkspace;
    private readonly string? _defaultWorkDir;
    private readonly ILogger? _logger;
    private bool _disposed;

    public ProcessSessionManager(
        int maxOutputChars = 500_000,
        int backgroundTimeoutSec = 1800,
        int sessionCleanupMs = 1_800_000,
        IReadOnlyList<Regex>? denyPatterns = null,
        bool restrictToWorkspace = false,
        string? defaultWorkDir = null,
        ILogger? logger = null)
    {
        _maxOutputChars = maxOutputChars;
        _backgroundTimeoutSec = backgroundTimeoutSec;
        _sessionCleanupMs = sessionCleanupMs;
        _denyPatterns = denyPatterns ?? [];
        _restrictToWorkspace = restrictToWorkspace;
        _defaultWorkDir = defaultWorkDir;
        _logger = logger;

        // Periodic cleanup of finished sessions past their TTL
        _cleanupTimer = new Timer(CleanupFinished, null, sessionCleanupMs, sessionCleanupMs);
    }

    /// <summary>
    /// Start a new background process session.
    /// Returns the created <see cref="ProcessSession"/>.
    /// </summary>
    public ProcessSession StartSession(string command, string? workingDir = null)
    {
        var cwd = workingDir ?? _defaultWorkDir ?? Directory.GetCurrentDirectory();

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            Arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Failed to start process");

        var session = new ProcessSession(process, command, cwd, _maxOutputChars);
        _sessions[session.SessionId] = session;

        _logger?.LogInformation(
            "Background process started: sessionId={SessionId}, pid={Pid}, command={Command}",
            session.SessionId, session.Pid, session.DerivedName);

        // Start a background timeout watchdog
        _ = WatchTimeoutAsync(session);

        return session;
    }

    /// <summary>Get a session by ID, or null if not found.</summary>
    public ProcessSession? GetSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : null;

    /// <summary>List all tracked sessions (running + finished).</summary>
    public List<ProcessSession> ListSessions() => [.. _sessions.Values];

    /// <summary>Remove a session from tracking. Kills it if still running.</summary>
    public bool RemoveSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return false;
        session.Dispose();
        return true;
    }

    /// <summary>Remove a finished session from tracking (does not kill).</summary>
    public bool ClearSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return false;
        if (session.IsRunning) return false; // can't clear a running session
        _sessions.TryRemove(sessionId, out _);
        session.Dispose();
        return true;
    }

    /// <summary>Number of tracked sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>Background timeout watchdog — kills process after configured timeout.</summary>
    private async Task WatchTimeoutAsync(ProcessSession session)
    {
        try
        {
            var exited = await session.WaitForExitAsync(_backgroundTimeoutSec * 1000);
            if (!exited)
            {
                _logger?.LogWarning(
                    "Background process timed out after {Timeout}s: sessionId={SessionId}",
                    _backgroundTimeoutSec, session.SessionId);
                session.Kill();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error in timeout watchdog for session {SessionId}", session.SessionId);
        }
    }

    /// <summary>Timer callback — remove finished sessions past their TTL.</summary>
    private void CleanupFinished(object? state)
    {
        var now = DateTime.UtcNow;
        var ttl = TimeSpan.FromMilliseconds(_sessionCleanupMs);

        foreach (var session in _sessions.Values)
        {
            if (!session.IsRunning && now - session.StartedAt > ttl)
            {
                if (_sessions.TryRemove(session.SessionId, out _))
                {
                    _logger?.LogInformation(
                        "Cleaned up expired session: sessionId={SessionId}", session.SessionId);
                    session.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();

        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }
}
