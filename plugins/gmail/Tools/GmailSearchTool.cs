using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Gmail;

/// <summary>
/// Search emails using Gmail query syntax.
/// Supports queries like: from:user@example.com, subject:meeting, after:2024/01/01, is:unread, etc.
/// </summary>
internal sealed class GmailSearchTool : GmailToolBase
{
    public GmailSearchTool(GmailClient? client, ILogger? logger) : base(client, logger) { }

    public override string Name => "gmail_search";

    public override string Description =>
        "Search Gmail messages. Returns message summaries with IDs, subjects, senders, and snippets. " +
        "To get the latest emails, use query 'in:inbox' with max_results=5. " +
        "Query examples: 'in:inbox', 'from:boss@company.com', 'subject:meeting', " +
        "'is:unread', 'is:starred', 'has:attachment', 'in:sent', 'newer_than:1d', 'newer_than:7d'. " +
        "Date filters use YYYY/MM/DD with slashes (NOT hyphens): 'after:2024/01/01 before:2024/12/31'. " +
        "Combine terms: 'in:inbox is:unread from:alice newer_than:3d'.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["query"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Gmail search query. Use 'in:inbox' for latest emails, 'newer_than:1d' for recent, 'from:user@example.com' for sender. Dates use slashes: 'after:2024/01/01'. Combine freely."
            },
            ["max_results"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Maximum number of results (default 10, max 50)"
            },
            ["label"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Filter by label ID (e.g. INBOX, SENT, STARRED, UNREAD, or custom label ID)"
            },
        },
        ["required"] = new[] { "query" },
    };

    protected override async Task<string> ExecuteCoreAsync(Dictionary<string, object?> args)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return "Error: 'query' parameter is required.";

        var maxResults = GetInt(args, "max_results", 10);
        if (maxResults < 1) maxResults = 1;
        if (maxResults > 50) maxResults = 50;

        var label = GetString(args, "label");

        Logger?.LogDebug("gmail_search: query='{Query}' max={Max}", query, maxResults);
        var result = await Client!.SearchAsync(query, maxResults, label);

        // Check if there are messages
        if (!result.TryGetProperty("messages", out var messages) || messages.GetArrayLength() == 0)
            return $"No messages found matching query: {query}";

        var sb = new StringBuilder();
        var msgList = messages.EnumerateArray().ToList();
        var total = result.TryGetProperty("resultSizeEstimate", out var est) ? est.GetInt32() : msgList.Count;
        sb.AppendLine($"Found {total} message(s) (showing {msgList.Count}):");
        sb.AppendLine();

        // Fetch metadata for each message to get subject/from/date
        var count = 0;
        foreach (var msg in msgList)
        {
            count++;
            var id = msg.GetProperty("id").GetString()!;
            var threadId = msg.TryGetProperty("threadId", out var tid) ? tid.GetString() : "";

            try
            {
                var detail = await Client.GetMessageAsync(id, "metadata");
                var headers = detail.TryGetProperty("payload", out var payload)
                    && payload.TryGetProperty("headers", out var h) ? h : default;

                var subject = headers.ValueKind == JsonValueKind.Array
                    ? GmailClient.GetHeader(headers, "Subject") ?? "(no subject)" : "(no subject)";
                var from = headers.ValueKind == JsonValueKind.Array
                    ? GmailClient.GetHeader(headers, "From") ?? "?" : "?";
                var date = headers.ValueKind == JsonValueKind.Array
                    ? GmailClient.GetHeader(headers, "Date") ?? "" : "";

                var snippet = detail.TryGetProperty("snippet", out var snip) ? snip.GetString() : "";

                var labels = detail.TryGetProperty("labelIds", out var lbl)
                    ? string.Join(", ", lbl.EnumerateArray().Select(l => l.GetString()))
                    : "";

                sb.AppendLine($"{count}. **{subject}**");
                sb.AppendLine($"   From: {from}");
                sb.AppendLine($"   Date: {date}");
                sb.AppendLine($"   ID: {id} | Thread: {threadId}");
                if (!string.IsNullOrEmpty(labels))
                    sb.AppendLine($"   Labels: {labels}");
                if (!string.IsNullOrEmpty(snippet))
                    sb.AppendLine($"   Snippet: {snippet}");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{count}. Message {id} — error fetching details: {ex.Message}");
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }
}
