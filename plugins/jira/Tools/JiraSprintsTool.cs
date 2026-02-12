using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Jira;

/// <summary>
/// List sprints for a Jira board, optionally filtered by state (active, closed, future).
/// </summary>
internal sealed class JiraSprintsTool(JiraClient? client, ILogger? logger) : JiraToolBase(client, logger)
{
    public override string Name => "jira_sprints";

    public override string Description =>
        "List sprints for a Jira board. Requires the board ID (use jira_boards to find it). " +
        "Optionally filter by state: active, closed, or future. " +
        "Shows sprint name, state, start/end dates, and goal.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["board_id"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Board ID (use jira_boards to find it)"
            },
            ["state"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Filter by sprint state: active, closed, future (default: all)"
            },
        },
        ["required"] = new[] { "board_id" }
    };

    protected override async Task<string> ExecuteCoreAsync(Dictionary<string, object?> args)
    {
        var boardId = GetInt(args, "board_id");
        if (boardId <= 0)
            return "Error: 'board_id' is required (use jira_boards to find the ID).";

        var state = GetString(args, "state");
        var result = await Client!.GetSprintsAsync(
            boardId,
            state: string.IsNullOrEmpty(state) ? null : state);

        var sb = new StringBuilder();
        sb.AppendLine($"## Sprints for Board {boardId}\n");

        if (!result.TryGetProperty("values", out var sprints) || sprints.GetArrayLength() == 0)
            return $"No sprints found for board {boardId}.";

        foreach (var sprint in sprints.EnumerateArray())
        {
            var id = sprint.TryGetProperty("id", out var sid) ? sid.GetInt32() : 0;
            var name = sprint.TryGetProperty("name", out var sn) ? sn.GetString() : "?";
            var sprintState = sprint.TryGetProperty("state", out var ss) ? ss.GetString() : "?";
            var startDate = sprint.TryGetProperty("startDate", out var sd) ? FormatDate(sd.GetString()) : "—";
            var endDate = sprint.TryGetProperty("endDate", out var ed) ? FormatDate(ed.GetString()) : "—";
            var completeDate = sprint.TryGetProperty("completeDate", out var cd) ? FormatDate(cd.GetString()) : "";
            var goal = sprint.TryGetProperty("goal", out var g) ? g.GetString() : "";

            var stateIcon = sprintState switch
            {
                "active" => "[Active]",
                "closed" => "[Closed]",
                "future" => "[Future]",
                _ => $"[{sprintState}]"
            };

            sb.AppendLine($"- **{name}** (ID: {id}) {stateIcon}");
            sb.Append($"  {startDate} → {endDate}");
            if (!string.IsNullOrEmpty(completeDate))
                sb.Append($" (completed: {completeDate})");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(goal))
                sb.AppendLine($"  Goal: {goal}");
        }

        return sb.ToString();
    }

    private static string FormatDate(string? iso)
    {
        if (iso == null) return "—";
        return DateTime.TryParse(iso, out var dt) ? dt.ToString("yyyy-MM-dd") : iso[..Math.Min(10, iso.Length)];
    }
}
