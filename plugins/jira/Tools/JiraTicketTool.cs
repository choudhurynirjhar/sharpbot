using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Jira;

/// <summary>
/// Get full details of a specific Jira ticket including description,
/// status, assignee, reporter, linked issues, subtasks, and more.
/// </summary>
internal sealed class JiraTicketTool(JiraClient? client, ILogger? logger) : JiraToolBase(client, logger)
{
    public override string Name => "jira_ticket";

    public override string Description =>
        "Get detailed information about a specific Jira ticket by its key (e.g. PROJ-123). " +
        "Returns type, status, priority, assignee, reporter, description, labels, " +
        "linked issues, subtasks, story points, sprint, and dates.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["key"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Issue key (e.g. PROJ-123)"
            },
        },
        ["required"] = new[] { "key" }
    };

    protected override async Task<string> ExecuteCoreAsync(Dictionary<string, object?> args)
    {
        var key = GetString(args, "key");
        if (string.IsNullOrEmpty(key))
            return "Error: 'key' parameter is required (e.g. PROJ-123).";

        var issue = await Client!.GetIssueAsync(key);
        return FormatIssueDetail(issue);
    }

    private static string FormatIssueDetail(JsonElement issue)
    {
        var sb = new StringBuilder();
        var key = issue.GetProperty("key").GetString();
        var fields = issue.GetProperty("fields");

        var summary = fields.TryGetProperty("summary", out var s) ? s.GetString() : "(no summary)";
        var issueType = SafeGet(fields, "issuetype", "name") ?? "Unknown";
        var status = SafeGet(fields, "status", "name") ?? "Unknown";
        var statusCat = SafeGet(fields, "status", "statusCategory", "name") ?? "";
        var priority = SafeGet(fields, "priority", "name") ?? "None";
        var assignee = SafeGet(fields, "assignee", "displayName") ?? "Unassigned";
        var reporter = SafeGet(fields, "reporter", "displayName") ?? "Unknown";
        var created = FormatDate(fields, "created");
        var updated = FormatDate(fields, "updated");
        var resolved = FormatDate(fields, "resolutiondate");

        sb.AppendLine($"# {key}: {summary}");
        sb.AppendLine();
        sb.AppendLine($"**Type:** {issueType}");
        sb.AppendLine($"**Status:** {status} ({statusCat})");
        sb.AppendLine($"**Priority:** {priority}");
        sb.AppendLine($"**Assignee:** {assignee}");
        sb.AppendLine($"**Reporter:** {reporter}");
        sb.AppendLine($"**Created:** {created}");
        sb.AppendLine($"**Updated:** {updated}");
        if (resolved != "—")
            sb.AppendLine($"**Resolved:** {resolved}");

        // Labels
        if (fields.TryGetProperty("labels", out var labels) && labels.GetArrayLength() > 0)
        {
            sb.AppendLine($"**Labels:** {string.Join(", ", labels.EnumerateArray().Select(l => l.GetString()))}");
        }

        // Components
        if (fields.TryGetProperty("components", out var components) && components.GetArrayLength() > 0)
        {
            var names = components.EnumerateArray()
                .Select(c => c.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => n != null);
            sb.AppendLine($"**Components:** {string.Join(", ", names)}");
        }

        // Fix versions
        if (fields.TryGetProperty("fixVersions", out var versions) && versions.GetArrayLength() > 0)
        {
            var names = versions.EnumerateArray()
                .Select(v => v.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => n != null);
            sb.AppendLine($"**Fix Versions:** {string.Join(", ", names)}");
        }

        // Story points (customfield_10016 is the common default)
        if (fields.TryGetProperty("customfield_10016", out var sp) && sp.ValueKind == JsonValueKind.Number)
            sb.AppendLine($"**Story Points:** {sp.GetDouble()}");

        // Sprint (customfield_10020)
        if (fields.TryGetProperty("customfield_10020", out var sprints) &&
            sprints.ValueKind == JsonValueKind.Array && sprints.GetArrayLength() > 0)
        {
            var sprintNames = sprints.EnumerateArray()
                .Select(spr => spr.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => n != null);
            sb.AppendLine($"**Sprint:** {string.Join(", ", sprintNames)}");
        }

        // Epic link (customfield_10014)
        if (fields.TryGetProperty("customfield_10014", out var epic) && epic.ValueKind == JsonValueKind.String)
            sb.AppendLine($"**Epic:** {epic.GetString()}");

        // Parent (for subtasks or child issues in next-gen)
        if (fields.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object)
        {
            var parentKey = parent.TryGetProperty("key", out var pk) ? pk.GetString() : "?";
            var parentSummary = "";
            if (parent.TryGetProperty("fields", out var pf) && pf.TryGetProperty("summary", out var ps))
                parentSummary = $" — {ps.GetString()}";
            sb.AppendLine($"**Parent:** {parentKey}{parentSummary}");
        }

        // Description
        sb.AppendLine();
        sb.AppendLine("## Description");
        sb.AppendLine();
        if (fields.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null)
        {
            sb.AppendLine(ExtractDescription(desc));
        }
        else
        {
            sb.AppendLine("(no description)");
        }

        // Subtasks
        if (fields.TryGetProperty("subtasks", out var subtasks) && subtasks.GetArrayLength() > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Subtasks");
            foreach (var sub in subtasks.EnumerateArray())
            {
                var subKey = sub.TryGetProperty("key", out var sk) ? sk.GetString() : "?";
                var subSummary = sub.TryGetProperty("fields", out var sf) &&
                    sf.TryGetProperty("summary", out var ss) ? ss.GetString() : "";
                var subStatus = sf.TryGetProperty("status", out var st) &&
                    st.TryGetProperty("name", out var sn) ? sn.GetString() : "?";
                sb.AppendLine($"- **{subKey}** [{subStatus}] {subSummary}");
            }
        }

        // Issue links
        if (fields.TryGetProperty("issuelinks", out var links) && links.GetArrayLength() > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Linked Issues");
            foreach (var link in links.EnumerateArray())
            {
                var linkType = link.TryGetProperty("type", out var lt) &&
                    lt.TryGetProperty("outward", out var lto) ? lto.GetString() : "related to";

                JsonElement linkedIssue;
                if (link.TryGetProperty("outwardIssue", out var outward))
                    linkedIssue = outward;
                else if (link.TryGetProperty("inwardIssue", out var inward))
                {
                    linkType = lt.TryGetProperty("inward", out var lti) ? lti.GetString() : "related to";
                    linkedIssue = inward;
                }
                else continue;

                var linkedKey = linkedIssue.TryGetProperty("key", out var lk) ? lk.GetString() : "?";
                var linkedSummary = "";
                if (linkedIssue.TryGetProperty("fields", out var lf) && lf.TryGetProperty("summary", out var ls))
                    linkedSummary = ls.GetString();
                sb.AppendLine($"- {linkType} **{linkedKey}**: {linkedSummary}");
            }
        }

        return sb.ToString();
    }

    /// <summary>Extract plain text from Jira's Atlassian Document Format (ADF) description.</summary>
    private static string ExtractDescription(JsonElement desc)
    {
        if (desc.ValueKind == JsonValueKind.String)
            return desc.GetString() ?? "";

        // ADF format — walk the content tree and extract text nodes
        if (desc.ValueKind != JsonValueKind.Object) return desc.ToString();

        var sb = new StringBuilder();
        ExtractTextFromAdf(desc, sb);
        return sb.ToString().Trim();
    }

    private static void ExtractTextFromAdf(JsonElement node, StringBuilder sb)
    {
        if (node.TryGetProperty("type", out var type))
        {
            var typeName = type.GetString();
            if (typeName == "text" && node.TryGetProperty("text", out var text))
            {
                sb.Append(text.GetString());
                return;
            }
            if (typeName == "hardBreak")
            {
                sb.AppendLine();
                return;
            }
            // Add spacing for block elements
            if (typeName is "paragraph" or "heading" or "bulletList" or "orderedList" or "codeBlock")
            {
                if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
            }
            if (typeName == "listItem")
                sb.Append("- ");
        }

        if (node.TryGetProperty("content", out var content))
        {
            foreach (var child in content.EnumerateArray())
                ExtractTextFromAdf(child, sb);
        }
    }

    private static string? SafeGet(JsonElement parent, params string[] path)
    {
        var current = parent;
        foreach (var prop in path)
        {
            if (!current.TryGetProperty(prop, out var next) || next.ValueKind == JsonValueKind.Null)
                return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    private static string FormatDate(JsonElement fields, string prop)
    {
        if (!fields.TryGetProperty(prop, out var val) || val.ValueKind == JsonValueKind.Null)
            return "—";
        var s = val.GetString();
        return DateTime.TryParse(s, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm") : s ?? "—";
    }
}
