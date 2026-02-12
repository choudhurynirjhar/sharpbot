namespace Sharpbot.Providers;

/// <summary>
/// A chunk emitted during streaming LLM completion.
/// </summary>
public sealed record StreamChunk
{
    /// <summary>"text_delta" for incremental text, "done" for the final aggregated response.</summary>
    public required string Type { get; init; }

    /// <summary>Incremental text content (only for "text_delta" chunks).</summary>
    public string? Delta { get; init; }

    /// <summary>Full aggregated response (only for "done" chunk).</summary>
    public LlmResponse? Response { get; init; }

    public static StreamChunk TextDelta(string delta) => new() { Type = "text_delta", Delta = delta };
    public static StreamChunk Done(LlmResponse response) => new() { Type = "done", Response = response };
}
