using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Gmail;

/// <summary>
/// Reply to an existing email thread.
/// Handles In-Reply-To and References headers automatically.
/// </summary>
internal sealed class GmailReplyTool : GmailToolBase
{
    public GmailReplyTool(GmailClient? client, ILogger? logger) : base(client, logger) { }

    public override string Name => "gmail_reply";

    public override string Description =>
        "Reply to an existing email thread. " +
        "Provide the 'thread_id' and 'message_id' of the message you are replying to, and the 'body' of your reply. " +
        "The subject is auto-prefixed with 'Re: ' and headers are set automatically. " +
        "Optionally override 'to' (defaults to the original sender).";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["thread_id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Gmail thread ID to reply to"
            },
            ["message_id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Gmail message ID of the email to reply to"
            },
            ["body"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Reply body text (plain text)"
            },
            ["to"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Override recipient (defaults to original sender)"
            },
        },
        ["required"] = new[] { "thread_id", "message_id", "body" },
    };

    protected override async Task<string> ExecuteCoreAsync(Dictionary<string, object?> args)
    {
        var threadId = GetString(args, "thread_id");
        var messageId = GetString(args, "message_id");
        var body = GetString(args, "body");

        if (string.IsNullOrWhiteSpace(threadId)) return "Error: 'thread_id' is required.";
        if (string.IsNullOrWhiteSpace(messageId)) return "Error: 'message_id' is required.";
        if (string.IsNullOrWhiteSpace(body)) return "Error: 'body' is required.";

        // Fetch original message to get subject and Message-ID header
        var original = await Client!.GetMessageAsync(messageId, "metadata");

        JsonElement headers = default;
        if (original.TryGetProperty("payload", out var payload) && payload.TryGetProperty("headers", out headers))
        {
            // fine
        }

        var origSubject = headers.ValueKind == JsonValueKind.Array
            ? GmailClient.GetHeader(headers, "Subject") ?? "" : "";
        var origFrom = headers.ValueKind == JsonValueKind.Array
            ? GmailClient.GetHeader(headers, "From") ?? "" : "";
        var origMessageIdHeader = headers.ValueKind == JsonValueKind.Array
            ? GmailClient.GetHeader(headers, "Message-ID") ?? "" : "";
        var origReferences = headers.ValueKind == JsonValueKind.Array
            ? GmailClient.GetHeader(headers, "References") ?? "" : "";

        // Determine reply-to address
        var to = GetString(args, "to");
        if (string.IsNullOrEmpty(to))
            to = origFrom;

        if (string.IsNullOrEmpty(to))
            return "Error: Could not determine reply recipient. Provide 'to' explicitly.";

        // Build subject
        var subject = origSubject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? origSubject
            : $"Re: {origSubject}";

        // Build references chain
        var references = string.IsNullOrEmpty(origReferences)
            ? origMessageIdHeader
            : $"{origReferences} {origMessageIdHeader}";

        var result = await Client.SendMessageAsync(
            to, subject, body,
            inReplyTo: origMessageIdHeader,
            references: references.Trim(),
            threadId: threadId);

        var newId = result.TryGetProperty("id", out var idProp) ? idProp.GetString() : "unknown";

        var sb = new StringBuilder();
        sb.AppendLine("Reply sent successfully!");
        sb.AppendLine($"  Message ID: {newId}");
        sb.AppendLine($"  Thread ID: {threadId}");
        sb.AppendLine($"  To: {to}");
        sb.AppendLine($"  Subject: {subject}");

        return sb.ToString().TrimEnd();
    }
}
