using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Jira;

/// <summary>
/// List Jira Software boards (Scrum/Kanban), optionally filtered by project.
/// </summary>
internal sealed class JiraBoardsTool(JiraClient? client, ILogger? logger) : JiraToolBase(client, logger)
{
    public override string Name => "jira_boards";

    public override string Description =>
        "List Jira Software boards (Scrum, Kanban). Optionally filter by project key. " +
        "Shows board name, type, and associated project.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["project"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Filter boards by project key"
            },
        },
        ["required"] = Array.Empty<string>()
    };

    protected override async Task<string> ExecuteCoreAsync(Dictionary<string, object?> args)
    {
        var project = GetString(args, "project");
        var result = await Client!.GetBoardsAsync(
            projectKey: string.IsNullOrEmpty(project) ? null : project);

        var sb = new StringBuilder();
        sb.AppendLine("## Jira Boards\n");

        if (!result.TryGetProperty("values", out var boards) || boards.GetArrayLength() == 0)
            return "No boards found.";

        foreach (var board in boards.EnumerateArray())
        {
            var id = board.TryGetProperty("id", out var bid) ? bid.GetInt32() : 0;
            var name = board.TryGetProperty("name", out var bn) ? bn.GetString() : "?";
            var type = board.TryGetProperty("type", out var bt) ? bt.GetString() : "?";

            var projInfo = "";
            if (board.TryGetProperty("location", out var loc))
            {
                var projName = loc.TryGetProperty("projectName", out var pn) ? pn.GetString() : "";
                var projKey = loc.TryGetProperty("projectKey", out var pk) ? pk.GetString() : "";
                if (!string.IsNullOrEmpty(projKey))
                    projInfo = $" | Project: {projKey} ({projName})";
            }

            sb.AppendLine($"- **{name}** (ID: {id}, {type}){projInfo}");
        }

        var total = result.TryGetProperty("total", out var t) ? t.GetInt32() : boards.GetArrayLength();
        sb.AppendLine($"\nShowing {boards.GetArrayLength()} of {total} boards");
        return sb.ToString();
    }
}
