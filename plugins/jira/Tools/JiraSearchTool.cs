using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Jira;

/// <summary>
/// Search Jira tickets using JQL with optional filters for project, date range,
/// issue type, status, assignee, and free-text query.
/// </summary>
internal sealed class JiraSearchTool(JiraClient? client, ILogger? logger) : JiraToolBase(client, logger)
{
    public override string Name => "jira_search";

    public override string Description =>
        "Search Jira tickets. Supports filtering by project, sprint (by ID or name), " +
        "date range (created/updated), issue type (Story, Bug, Epic, Task, Sub-task), " +
        "status, assignee, labels, and free text. Returns a summary list of matching issues. " +
        "You can search by sprint alone — a project key is NOT required.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["project"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Jira project key (e.g. PROJ, MYAPP)"
            },
            ["sprint_id"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Sprint ID to get all issues for (use jira_sprints to find the ID)"
            },
            ["sprint"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Sprint name to filter by (partial match supported)"
            },
            ["query"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Free-text search across summary and description"
            },
            ["type"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Issue type filter: Story, Bug, Epic, Task, Sub-task, etc."
            },
            ["status"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Status filter: To Do, In Progress, Done, etc."
            },
            ["assignee"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Assignee display name or 'currentUser()' or 'unassigned'"
            },
            ["labels"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Comma-separated labels to filter by"
            },
            ["created_after"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Only tickets created on or after this date (YYYY-MM-DD)"
            },
            ["created_before"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Only tickets created on or before this date (YYYY-MM-DD)"
            },
            ["updated_after"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Only tickets updated on or after this date (YYYY-MM-DD)"
            },
            ["updated_before"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Only tickets updated on or before this date (YYYY-MM-DD)"
            },
            ["order_by"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Sort field: created, updated, priority, status (default: updated DESC)"
            },
            ["max_results"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Maximum results to return (default: 20, max: 50)"
            },
            ["jql"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Raw JQL query. If provided, all other filters are ignored."
            },
        },
        ["required"] = Array.Empty<string>()
    };

    protected override async Task<string> ExecuteCoreAsync(Dictionary<string, object?> args)
    {
        var maxResults = Math.Clamp(GetInt(args, "max_results", 20), 1, 50);

        // If sprint_id is provided and no other complex filters, use the Agile API directly
        // (more reliable than JQL sprint filter, especially for board-scoped sprints)
        var sprintId = GetInt(args, "sprint_id");
        var rawJql = GetString(args, "jql");

        if (sprintId > 0 && string.IsNullOrEmpty(rawJql))
        {
            var result = await Client!.GetSprintIssuesAsync(
                sprintId,
                maxResults: maxResults,
                fields: "summary,status,issuetype,priority,assignee,created,updated,labels");

            return FormatSearchResults(result, $"sprint = {sprintId} (via Agile API)");
        }

        var jql = !string.IsNullOrEmpty(rawJql) ? rawJql : BuildJql(args);

        if (string.IsNullOrEmpty(jql))
            return "Error: Provide at least one filter (project, sprint_id, sprint, jql, query, etc.).";

        var searchResult = await Client!.SearchAsync(
            jql,
            maxResults: maxResults,
            fields: "summary,status,issuetype,priority,assignee,created,updated,labels");

        return FormatSearchResults(searchResult, jql);
    }

    private static string BuildJql(Dictionary<string, object?> args)
    {
        var clauses = new List<string>();

        var project = GetString(args, "project");
        if (!string.IsNullOrEmpty(project))
            clauses.Add($"project = \"{project}\"");

        var sprintName = GetString(args, "sprint");
        if (!string.IsNullOrEmpty(sprintName))
            clauses.Add($"sprint = \"{sprintName}\"");

        var sprintId = GetInt(args, "sprint_id");
        if (sprintId > 0)
            clauses.Add($"sprint = {sprintId}");

        var type = GetString(args, "type");
        if (!string.IsNullOrEmpty(type))
            clauses.Add($"issuetype = \"{type}\"");

        var status = GetString(args, "status");
        if (!string.IsNullOrEmpty(status))
            clauses.Add($"status = \"{status}\"");

        var assignee = GetString(args, "assignee");
        if (!string.IsNullOrEmpty(assignee))
        {
            if (assignee.Equals("unassigned", StringComparison.OrdinalIgnoreCase))
                clauses.Add("assignee is EMPTY");
            else if (assignee.Equals("currentUser()", StringComparison.OrdinalIgnoreCase))
                clauses.Add("assignee = currentUser()");
            else
                clauses.Add($"assignee = \"{assignee}\"");
        }

        var labels = GetString(args, "labels");
        if (!string.IsNullOrEmpty(labels))
        {
            foreach (var label in labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                clauses.Add($"labels = \"{label}\"");
        }

        var createdAfter = GetString(args, "created_after");
        if (!string.IsNullOrEmpty(createdAfter))
            clauses.Add($"created >= \"{createdAfter}\"");

        var createdBefore = GetString(args, "created_before");
        if (!string.IsNullOrEmpty(createdBefore))
            clauses.Add($"created <= \"{createdBefore}\"");

        var updatedAfter = GetString(args, "updated_after");
        if (!string.IsNullOrEmpty(updatedAfter))
            clauses.Add($"updated >= \"{updatedAfter}\"");

        var updatedBefore = GetString(args, "updated_before");
        if (!string.IsNullOrEmpty(updatedBefore))
            clauses.Add($"updated <= \"{updatedBefore}\"");

        var query = GetString(args, "query");
        if (!string.IsNullOrEmpty(query))
            clauses.Add($"text ~ \"{query}\"");

        if (clauses.Count == 0) return "";

        var orderBy = GetString(args, "order_by", "updated DESC");
        return string.Join(" AND ", clauses) + $" ORDER BY {orderBy}";
    }

    private static string FormatSearchResults(JsonElement result, string jql)
    {
        var sb = new StringBuilder();

        var total = result.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
        sb.AppendLine($"**JQL:** `{jql}`");
        sb.AppendLine($"**Results:** {total} total\n");

        if (!result.TryGetProperty("issues", out var issues)) return sb.ToString();

        foreach (var issue in issues.EnumerateArray())
        {
            var key = issue.GetProperty("key").GetString();
            var fields = issue.GetProperty("fields");

            var summary = fields.TryGetProperty("summary", out var s) ? s.GetString() : "(no summary)";
            var issueType = SafeGetNested(fields, "issuetype", "name") ?? "?";
            var status = SafeGetNested(fields, "status", "name") ?? "?";
            var priority = SafeGetNested(fields, "priority", "name") ?? "None";
            var assignee = SafeGetNested(fields, "assignee", "displayName") ?? "Unassigned";
            var created = fields.TryGetProperty("created", out var c) ? FormatDate(c.GetString()) : "?";
            var updated = fields.TryGetProperty("updated", out var u) ? FormatDate(u.GetString()) : "?";

            var labels = "";
            if (fields.TryGetProperty("labels", out var lbl) && lbl.GetArrayLength() > 0)
                labels = $" | Labels: {string.Join(", ", lbl.EnumerateArray().Select(l => l.GetString()))}";

            sb.AppendLine($"- **{key}** [{issueType}] {summary}");
            sb.AppendLine($"  Status: {status} | Priority: {priority} | Assignee: {assignee}");
            sb.AppendLine($"  Created: {created} | Updated: {updated}{labels}");
        }

        return sb.ToString();
    }

    private static string? SafeGetNested(JsonElement parent, string prop, string nested)
    {
        if (!parent.TryGetProperty(prop, out var child) || child.ValueKind == JsonValueKind.Null) return null;
        return child.TryGetProperty(nested, out var val) ? val.GetString() : null;
    }

    private static string FormatDate(string? iso)
    {
        if (iso == null) return "?";
        return DateTime.TryParse(iso, out var dt) ? dt.ToString("yyyy-MM-dd") : iso[..Math.Min(10, iso.Length)];
    }
}
