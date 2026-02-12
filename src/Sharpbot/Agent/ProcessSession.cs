using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Sharpbot.Agent;

/// <summary>
/// Wraps a <see cref="Process"/> with async buffered stdout/stderr reading,
/// stdin writing, polling, and kill support.
/// Sessions are created by <see cref="ProcessSessionManager"/> and tracked in memory.
/// </summary>
public sealed class ProcessSession : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _outputBuffer = new();
    private readonly object _lock = new();
    private readonly int _maxOutputChars;
    private int _pollOffset;
    private bool _disposed;

    /// <summary>Unique session identifier.</summary>
    public string SessionId { get; }

    /// <summary>The shell command that was executed.</summary>
    public string Command { get; }

    /// <summary>Working directory the command runs in.</summary>
    public string WorkingDir { get; }

    /// <summary>When the session was created.</summary>
    public DateTime StartedAt { get; }

    /// <summary>Process exit code, or null if still running.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>Whether the process is still running.</summary>
    public bool IsRunning => !_process.HasExited;

    /// <summary>Process ID.</summary>
    public int Pid { get; }

    /// <summary>Short human-readable name derived from the command (e.g. "npm build", "python server").</summary>
    public string DerivedName { get; }

    internal ProcessSession(
        Process process,
        string command,
        string workingDir,
        int maxOutputChars = 500_000)
    {
        _process = process;
        _maxOutputChars = maxOutputChars;

        SessionId = Guid.NewGuid().ToString("N")[..12];
        Command = command;
        WorkingDir = workingDir;
        StartedAt = DateTime.UtcNow;
        Pid = process.Id;
        DerivedName = DeriveName(command);

        // Start reading stdout/stderr asynchronously
        _process.OutputDataReceived += OnOutputData;
        _process.ErrorDataReceived += OnErrorData;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnExited;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void OnOutputData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        AppendOutput(e.Data);
    }

    private void OnErrorData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        AppendOutput($"[stderr] {e.Data}");
    }

    private void OnExited(object? sender, EventArgs e)
    {
        try { ExitCode = _process.ExitCode; }
        catch { ExitCode = -1; }
    }

    private void AppendOutput(string line)
    {
        lock (_lock)
        {
            // Enforce max output cap — drop oldest content when exceeded
            if (_outputBuffer.Length + line.Length + 1 > _maxOutputChars)
            {
                var excess = _outputBuffer.Length + line.Length + 1 - _maxOutputChars;
                _outputBuffer.Remove(0, Math.Min(excess + _maxOutputChars / 10, _outputBuffer.Length));
                // Adjust poll offset if it was pointing into removed content
                _pollOffset = Math.Min(_pollOffset, _outputBuffer.Length);
            }
            _outputBuffer.AppendLine(line);
        }
    }

    /// <summary>
    /// Drain new output since the last poll.
    /// Returns only content accumulated since the previous <see cref="PollNewOutput"/> call.
    /// </summary>
    public string PollNewOutput()
    {
        lock (_lock)
        {
            if (_pollOffset >= _outputBuffer.Length) return "";
            var newContent = _outputBuffer.ToString(_pollOffset, _outputBuffer.Length - _pollOffset);
            _pollOffset = _outputBuffer.Length;
            return newContent;
        }
    }

    /// <summary>
    /// Read the full buffered output with optional line-based offset and limit.
    /// </summary>
    public string GetLog(int? offset = null, int? limit = null)
    {
        string fullOutput;
        lock (_lock)
        {
            fullOutput = _outputBuffer.ToString();
        }

        if (offset == null && limit == null) return fullOutput;

        var lines = fullOutput.Split('\n');
        var startLine = offset ?? 0;
        if (startLine < 0) startLine = Math.Max(0, lines.Length + startLine); // negative = from end

        var count = limit ?? (lines.Length - startLine);
        count = Math.Min(count, lines.Length - startLine);
        if (startLine >= lines.Length || count <= 0) return "";

        return string.Join('\n', lines.AsSpan(startLine, count).ToArray());
    }

    /// <summary>Write data to the process stdin. Optionally close stdin afterward.</summary>
    public void WriteStdin(string data, bool eof = false)
    {
        if (_process.HasExited)
            throw new InvalidOperationException("Process has already exited.");

        _process.StandardInput.Write(data);
        _process.StandardInput.Flush();

        if (eof)
            _process.StandardInput.Close();
    }

    /// <summary>Kill the process tree.</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited */ }
    }

    /// <summary>Get the current output length in characters.</summary>
    public int OutputLength
    {
        get { lock (_lock) { return _outputBuffer.Length; } }
    }

    /// <summary>Get a tail of the output (last N characters).</summary>
    public string GetTail(int chars = 500)
    {
        lock (_lock)
        {
            if (_outputBuffer.Length <= chars) return _outputBuffer.ToString();
            return "..." + _outputBuffer.ToString(_outputBuffer.Length - chars, chars);
        }
    }

    /// <summary>Wait for the process to exit (with optional timeout).</summary>
    public async Task<bool> WaitForExitAsync(int timeoutMs, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Derive a short name from a command string.</summary>
    private static string DeriveName(string command)
    {
        var cmd = command.Trim();
        // Strip shell prefix (cmd /c, sh -c, etc.)
        cmd = Regex.Replace(cmd, @"^(cmd\s+/c\s+|/bin/sh\s+-c\s+)", "", RegexOptions.IgnoreCase).Trim();
        // Strip quotes
        cmd = cmd.Trim('"', '\'');
        // Take first two "words" (e.g. "npm run build" -> "npm run")
        var words = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = string.Join(' ', words.Take(Math.Min(3, words.Length)));
        return name.Length > 40 ? name[..37] + "..." : name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        _process.OutputDataReceived -= OnOutputData;
        _process.ErrorDataReceived -= OnErrorData;
        _process.Exited -= OnExited;
        _process.Dispose();
    }
}
