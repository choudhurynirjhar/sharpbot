using System.Text;

namespace Sharpbot.Agent.Tools;

/// <summary>
/// Tool that allows the agent to search semantic memory for relevant past information.
/// Returns top-K results with similarity scores.
/// </summary>
public sealed class MemorySearchTool : ToolBase
{
    private readonly SemanticMemoryStore _store;

    public MemorySearchTool(SemanticMemoryStore store) => _store = store;

    public override string Name => "memory_search";

    public override string Description =>
        "Search your semantic memory for relevant past information. " +
        "Use this when you need to recall something from past conversations, notes, or indexed content. " +
        "Returns the most semantically similar stored memories.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["query"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "What to search for in semantic memory"
            },
            ["top_k"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Number of results to return (default: 5, max: 20)",
                ["minimum"] = 1,
                ["maximum"] = 20,
            },
            ["min_score"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = "Minimum similarity score threshold (0-1, default: 0.5)",
            },
        },
        ["required"] = new[] { "query" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query is required";

        var topK = Math.Clamp(GetInt(args, "top_k") ?? 5, 1, 20);
        var minScoreStr = GetString(args, "min_score");
        var minScore = 0.5f;
        if (!string.IsNullOrEmpty(minScoreStr) && float.TryParse(minScoreStr, out var parsed))
            minScore = Math.Clamp(parsed, 0f, 1f);

        var results = await _store.SearchAsync(query, topK, minScore);

        if (results.Count == 0)
            return "No relevant memories found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} relevant memory/memories:");
        sb.AppendLine();

        foreach (var r in results)
        {
            sb.AppendLine($"--- [Score: {r.Score:F3}] (Source: {r.Source}) ---");
            sb.AppendLine(r.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
