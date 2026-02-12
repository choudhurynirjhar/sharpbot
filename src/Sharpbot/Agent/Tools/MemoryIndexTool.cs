namespace Sharpbot.Agent.Tools;

/// <summary>
/// Tool that allows the agent to explicitly index content into semantic memory.
/// Useful for remembering important information from conversations.
/// </summary>
public sealed class MemoryIndexTool : ToolBase
{
    private readonly SemanticMemoryStore _store;

    public MemoryIndexTool(SemanticMemoryStore store) => _store = store;

    public override string Name => "memory_index";

    public override string Description =>
        "Store information in your semantic memory for future retrieval. " +
        "Use this to remember important facts, user preferences, or key information from conversations. " +
        "Stored content can later be found with the memory_search tool.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["content"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The text content to store in semantic memory"
            },
            ["source"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Category tag: conversation, note, file, or manual (default: manual)",
                ["enum"] = new[] { "conversation", "note", "file", "manual" },
            },
            ["source_id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Optional reference ID for the source (e.g., session key, file path)"
            },
        },
        ["required"] = new[] { "content" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var content = GetString(args, "content");
        if (string.IsNullOrWhiteSpace(content))
            return "Error: content is required";

        var source = GetString(args, "source", "manual");
        var sourceId = GetString(args, "source_id");
        if (string.IsNullOrEmpty(sourceId)) sourceId = null;

        await _store.IndexAsync(content, source, sourceId);

        var stats = _store.GetStats();
        return $"Successfully indexed content into semantic memory. Total stored chunks: {stats.TotalChunks}";
    }
}
