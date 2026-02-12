using System.Diagnostics;
using System.Text;

namespace Sharpbot.Agent;

/// <summary>
/// Telemetry for a single tool call execution.
/// </summary>
public sealed record ToolCallTelemetry
{
    public required string Name { get; init; }
    public required string CallId { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Success { get; init; } = true;
    public string? Error { get; init; }
    public int ResultLength { get; init; }
    public int Iteration { get; init; }
}

/// <summary>
/// Telemetry for a single LLM provider call.
/// </summary>
public sealed record LlmCallTelemetry
{
    public required string Model { get; init; }
    public int Iteration { get; init; }
    public TimeSpan Duration { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
    public string FinishReason { get; init; } = "stop";
    public bool HasToolCalls { get; init; }
    public int ToolCallCount { get; init; }
}

/// <summary>
/// Aggregated telemetry for a complete agent message processing cycle.
/// Captures LLM calls, tool calls, timing, and token usage.
/// </summary>
public sealed class AgentTelemetry
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly List<LlmCallTelemetry> _llmCalls = [];
    private readonly List<ToolCallTelemetry> _toolCalls = [];

    /// <summary>Channel the message came from.</summary>
    public string Channel { get; set; } = "";

    /// <summary>Sender ID.</summary>
    public string SenderId { get; set; } = "";

    /// <summary>Session key.</summary>
    public string SessionKey { get; set; } = "";

    /// <summary>Model used.</summary>
    public string Model { get; set; } = "";

    /// <summary>Whether the processing completed successfully.</summary>
    public bool Success { get; set; } = true;

    /// <summary>Error message if processing failed.</summary>
    public string? Error { get; set; }

    /// <summary>Number of times context compaction was triggered.</summary>
    public int CompactionCount { get; private set; }

    /// <summary>All LLM calls made during this cycle.</summary>
    public IReadOnlyList<LlmCallTelemetry> LlmCalls => _llmCalls;

    /// <summary>All tool calls made during this cycle.</summary>
    public IReadOnlyList<ToolCallTelemetry> ToolCalls => _toolCalls;

    /// <summary>Record an LLM call.</summary>
    public void AddLlmCall(LlmCallTelemetry call) => _llmCalls.Add(call);

    /// <summary>Record a tool call.</summary>
    public void AddToolCall(ToolCallTelemetry call) => _toolCalls.Add(call);

    /// <summary>Record that context compaction occurred.</summary>
    public void RecordCompaction() => CompactionCount++;

    /// <summary>Stop the timer and mark complete.</summary>
    public void Complete()
    {
        _stopwatch.Stop();
    }

    /// <summary>Mark as failed.</summary>
    public void Fail(string error)
    {
        Success = false;
        Error = error;
        _stopwatch.Stop();
    }

    // ─── Computed properties ──────────────────────────────────────────

    /// <summary>Total wall-clock time for the entire processing cycle.</summary>
    public TimeSpan TotalDuration => _stopwatch.Elapsed;

    /// <summary>Number of LLM iterations.</summary>
    public int Iterations => _llmCalls.Count;

    /// <summary>Total prompt tokens across all LLM calls.</summary>
    public int TotalPromptTokens => _llmCalls.Sum(c => c.PromptTokens);

    /// <summary>Total completion tokens across all LLM calls.</summary>
    public int TotalCompletionTokens => _llmCalls.Sum(c => c.CompletionTokens);

    /// <summary>Total tokens across all LLM calls.</summary>
    public int TotalTokens => _llmCalls.Sum(c => c.TotalTokens);

    /// <summary>Total time spent in LLM calls.</summary>
    public TimeSpan TotalLlmDuration => TimeSpan.FromTicks(_llmCalls.Sum(c => c.Duration.Ticks));

    /// <summary>Total time spent in tool calls.</summary>
    public TimeSpan TotalToolDuration => TimeSpan.FromTicks(_toolCalls.Sum(c => c.Duration.Ticks));

    /// <summary>Number of tool calls.</summary>
    public int TotalToolCalls => _toolCalls.Count;

    /// <summary>Number of failed tool calls.</summary>
    public int FailedToolCalls => _toolCalls.Count(c => !c.Success);

    /// <summary>Distinct tools used.</summary>
    public IReadOnlyList<string> ToolsUsed =>
        _toolCalls.Select(c => c.Name).Distinct().ToList();

    // ─── Formatting ───────────────────────────────────────────────────

    /// <summary>Build a structured log string for the telemetry summary.</summary>
    public string ToLogString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("┌─ Agent Telemetry ─────────────────────────────────────");
        sb.AppendLine($"│ Channel:    {Channel} | Sender: {SenderId}");
        sb.AppendLine($"│ Session:    {SessionKey}");
        sb.AppendLine($"│ Model:      {Model}");
        sb.AppendLine($"│ Status:     {(Success ? "✓ Success" : $"✗ Failed: {Error}")}");
        sb.AppendLine($"│ Total Time: {FormatDuration(TotalDuration)}");
        if (CompactionCount > 0)
            sb.AppendLine($"│ Compactions: {CompactionCount}");
        sb.AppendLine("├─ LLM Calls ──────────────────────────────────────────");
        sb.AppendLine($"│ Iterations:       {Iterations}");
        sb.AppendLine($"│ LLM Time:         {FormatDuration(TotalLlmDuration)}");
        sb.AppendLine($"│ Prompt Tokens:    {TotalPromptTokens:N0}");
        sb.AppendLine($"│ Completion Tokens:{TotalCompletionTokens:N0}");
        sb.AppendLine($"│ Total Tokens:     {TotalTokens:N0}");

        if (_llmCalls.Count > 1)
        {
            for (int i = 0; i < _llmCalls.Count; i++)
            {
                var c = _llmCalls[i];
                sb.AppendLine($"│   [{i + 1}] {FormatDuration(c.Duration)} | " +
                    $"tokens: {c.TotalTokens:N0} | " +
                    $"finish: {c.FinishReason}" +
                    (c.HasToolCalls ? $" | tool_calls: {c.ToolCallCount}" : ""));
            }
        }

        sb.AppendLine("├─ Tool Calls ─────────────────────────────────────────");
        sb.AppendLine($"│ Total Calls:  {TotalToolCalls}");
        sb.AppendLine($"│ Tool Time:    {FormatDuration(TotalToolDuration)}");
        if (FailedToolCalls > 0)
            sb.AppendLine($"│ Failed:       {FailedToolCalls}");
        if (ToolsUsed.Count > 0)
            sb.AppendLine($"│ Tools Used:   {string.Join(", ", ToolsUsed)}");

        foreach (var tc in _toolCalls)
        {
            var status = tc.Success ? "✓" : "✗";
            sb.AppendLine($"│   {status} {tc.Name} ({FormatDuration(tc.Duration)}) " +
                $"→ {tc.ResultLength:N0} chars" +
                (tc.Error != null ? $" | error: {tc.Error}" : ""));
        }

        sb.Append("└──────────────────────────────────────────────────────");
        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalMilliseconds < 1000)
            return $"{ts.TotalMilliseconds:F0}ms";
        if (ts.TotalSeconds < 60)
            return $"{ts.TotalSeconds:F1}s";
        return $"{ts.TotalMinutes:F1}m";
    }
}
