using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Jira;

/// <summary>
/// Get comments for a specific Jira ticket.
/// </summary>
internal sealed class JiraCommentsTool(JiraClient? client, ILogger? logger) : JiraToolBase(client, logger)
{
    public override string Name => "jira_comments";

    public override string Description =>
        "Get all comments on a Jira ticket. Shows author, date, and comment body.";

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

        var result = await Client!.GetCommentsAsync(key);

        var sb = new StringBuilder();
        sb.AppendLine($"## Comments on {key}\n");

        if (!result.TryGetProperty("comments", out var comments) || comments.GetArrayLength() == 0)
            return $"No comments on {key}.";

        var total = result.TryGetProperty("total", out var t) ? t.GetInt32() : comments.GetArrayLength();
        sb.AppendLine($"Total: {total} comment(s)\n");

        foreach (var comment in comments.EnumerateArray())
        {
            var author = "";
            if (comment.TryGetProperty("author", out var a) && a.TryGetProperty("displayName", out var dn))
                author = dn.GetString() ?? "Unknown";

            var created = "";
            if (comment.TryGetProperty("created", out var c))
                created = FormatDate(c.GetString());

            var body = "";
            if (comment.TryGetProperty("body", out var b))
                body = ExtractText(b);

            sb.AppendLine($"**{author}** — {created}");
            sb.AppendLine(body);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Extract text from ADF or plain string body.</summary>
    private static string ExtractText(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.String)
            return body.GetString() ?? "";

        if (body.ValueKind != JsonValueKind.Object) return body.ToString();

        var sb = new StringBuilder();
        WalkAdf(body, sb);
        return sb.ToString().Trim();
    }

    private static void WalkAdf(JsonElement node, StringBuilder sb)
    {
        if (node.TryGetProperty("type", out var type))
        {
            var t = type.GetString();
            if (t == "text" && node.TryGetProperty("text", out var text))
            {
                sb.Append(text.GetString());
                return;
            }
            if (t == "hardBreak")
            {
                sb.AppendLine();
                return;
            }
            if (t is "paragraph" or "heading" && sb.Length > 0 && sb[^1] != '\n')
                sb.AppendLine();
        }

        if (node.TryGetProperty("content", out var content))
        {
            foreach (var child in content.EnumerateArray())
                WalkAdf(child, sb);
        }
    }

    private static string FormatDate(string? iso)
    {
        if (iso == null) return "?";
        return DateTime.TryParse(iso, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm") : iso[..Math.Min(16, iso.Length)];
    }
}
