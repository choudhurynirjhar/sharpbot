namespace Sharpbot.Providers;

/// <summary>A tool call request from the LLM.</summary>
public sealed record ToolCallRequest(string Id, string Name, Dictionary<string, object?> Arguments);

/// <summary>
/// Response from an LLM provider.
/// Immutable after construction.
/// </summary>
public sealed record LlmResponse
{
    public string? Content { get; init; }
    public IReadOnlyList<ToolCallRequest> ToolCalls { get; init; } = [];
    public string FinishReason { get; init; } = "stop";
    public IReadOnlyDictionary<string, int> Usage { get; init; } = new Dictionary<string, int>();

    public bool HasToolCalls => ToolCalls.Count > 0;
}

/// <summary>
/// Abstract interface for LLM providers.
/// Implementations handle specifics of each provider's API
/// while maintaining a consistent interface.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Send a chat completion request.</summary>
    Task<LlmResponse> ChatAsync(
        List<Dictionary<string, object?>> messages,
        List<Dictionary<string, object?>>? tools = null,
        string? model = null,
        int maxTokens = 4096,
        double temperature = 0.7,
        CancellationToken ct = default);

    /// <summary>Send a streaming chat completion request. Yields text deltas followed by a final "done" chunk.</summary>
    IAsyncEnumerable<StreamChunk> ChatStreamAsync(
        List<Dictionary<string, object?>> messages,
        List<Dictionary<string, object?>>? tools = null,
        string? model = null,
        int maxTokens = 4096,
        double temperature = 0.7,
        CancellationToken ct = default);

    /// <summary>Generate an embedding vector for the given text.</summary>
    Task<float[]> EmbedAsync(
        string text,
        string? model = null,
        CancellationToken ct = default);

    /// <summary>Get the default model for this provider.</summary>
    string GetDefaultModel();
}
