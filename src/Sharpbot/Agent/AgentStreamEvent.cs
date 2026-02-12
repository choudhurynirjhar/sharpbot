using Sharpbot.Api;

namespace Sharpbot.Agent;

/// <summary>
/// Events emitted during a streaming agent turn.
/// Serialized as SSE events to the frontend.
/// </summary>
public sealed record AgentStreamEvent
{
    /// <summary>
    /// "text_delta" — incremental LLM text output.
    /// "tool_start" — a tool is about to execute.
    /// "tool_end" — a tool has finished executing.
    /// "status" — informational status update (e.g., "Running iteration 2").
    /// "done" — final event with full response, stats, and tool calls.
    /// "error" — an error occurred.
    /// </summary>
    public required string Type { get; init; }

    // ── text_delta fields ──
    public string? Delta { get; init; }

    // ── tool_start / tool_end fields ──
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public bool? ToolSuccess { get; init; }
    public int? ToolDurationMs { get; init; }
    public string? ToolError { get; init; }
    public int? ToolResultLength { get; init; }

    // ── status fields ──
    public string? StatusMessage { get; init; }
    public int? Iteration { get; init; }

    // ── done fields ──
    public string? Message { get; init; }
    public string? SessionId { get; init; }
    public List<ToolCallDto>? ToolCalls { get; init; }
    public ChatStatsDto? Stats { get; init; }

    // ── error fields ──
    public string? Error { get; init; }

    // ── Factory methods ──
    public static AgentStreamEvent TextDelta(string delta) =>
        new() { Type = "text_delta", Delta = delta };

    public static AgentStreamEvent ToolStart(string name, string callId) =>
        new() { Type = "tool_start", ToolName = name, ToolCallId = callId };

    public static AgentStreamEvent ToolEnd(string name, string callId, bool success, int durationMs, string? error = null, int resultLength = 0) =>
        new() { Type = "tool_end", ToolName = name, ToolCallId = callId, ToolSuccess = success, ToolDurationMs = durationMs, ToolError = error, ToolResultLength = resultLength };

    public static AgentStreamEvent Status(string message, int iteration) =>
        new() { Type = "status", StatusMessage = message, Iteration = iteration };

    public static AgentStreamEvent Completed(string message, string sessionId, List<ToolCallDto> toolCalls, ChatStatsDto stats) =>
        new() { Type = "done", Message = message, SessionId = sessionId, ToolCalls = toolCalls, Stats = stats };

    public static AgentStreamEvent Failed(string error) =>
        new() { Type = "error", Error = error };
}
