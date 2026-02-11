using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Sharpbot.Telemetry;

/// <summary>
/// Centralised OpenTelemetry instrumentation for Sharpbot.
/// Defines the activity source (tracing) and meter (metrics) used throughout the application.
/// </summary>
public static class SharpbotInstrumentation
{
    public const string ServiceName = "sharpbot";
    public const string ServiceVersion = "0.2.0";

    // ── Tracing ─────────────────────────────────────────────────────────────
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    // ── Metrics ─────────────────────────────────────────────────────────────
    private static readonly Meter Meter = new(ServiceName, ServiceVersion);

    /// <summary>Total agent requests processed.</summary>
    public static readonly Counter<long> RequestsTotal =
        Meter.CreateCounter<long>("sharpbot.requests.total", "requests", "Total agent requests processed");

    /// <summary>Successful agent requests.</summary>
    public static readonly Counter<long> RequestsSuccess =
        Meter.CreateCounter<long>("sharpbot.requests.success", "requests", "Successful agent requests");

    /// <summary>Failed agent requests.</summary>
    public static readonly Counter<long> RequestsFailed =
        Meter.CreateCounter<long>("sharpbot.requests.failed", "requests", "Failed agent requests");

    /// <summary>Total prompt tokens consumed.</summary>
    public static readonly Counter<long> PromptTokens =
        Meter.CreateCounter<long>("sharpbot.tokens.prompt", "tokens", "Total prompt tokens consumed");

    /// <summary>Total completion tokens consumed.</summary>
    public static readonly Counter<long> CompletionTokens =
        Meter.CreateCounter<long>("sharpbot.tokens.completion", "tokens", "Total completion tokens consumed");

    /// <summary>Total tool calls executed.</summary>
    public static readonly Counter<long> ToolCallsTotal =
        Meter.CreateCounter<long>("sharpbot.tool_calls.total", "calls", "Total tool calls executed");

    /// <summary>Failed tool calls.</summary>
    public static readonly Counter<long> ToolCallsFailed =
        Meter.CreateCounter<long>("sharpbot.tool_calls.failed", "calls", "Failed tool calls");

    /// <summary>LLM call duration histogram.</summary>
    public static readonly Histogram<double> LlmDuration =
        Meter.CreateHistogram<double>("sharpbot.llm.duration", "ms", "LLM call duration in milliseconds");

    /// <summary>Agent request total duration histogram.</summary>
    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("sharpbot.request.duration", "ms", "Agent request total duration in milliseconds");
}
