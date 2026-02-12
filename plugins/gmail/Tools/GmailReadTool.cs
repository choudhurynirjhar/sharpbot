using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Gmail;

/// <summary>
/// Read a specific Gmail message or thread by ID.
/// Returns full details: sender, recipients, subject, date, body, and attachments.
/// </summary>
internal sealed class GmailReadTool : GmailToolBase
{
    public GmailReadTool(GmailClient? client, ILogger? logger) : base(client, logger) { }

    public override string Name => "gmail_read";

    public override string Description =>
        "Read a specific email message or entire thread by ID. " +
        "Returns sender, recipients, subject, date, body text, and attachment list. " +
        "Use a message_id to read one email, or thread_id to read an entire conversation.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["message_id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Gmail message ID to read a single email"
            },
            ["thread_id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Gmail thread ID to read all messages in a conversation"
            },
        },
    };

    protected override async Task<string> ExecuteCoreAsync(Dictionary<string, object?> args)
    {
        var messageId = GetString(args, "message_id");
        var threadId = GetString(args, "thread_id");

        if (string.IsNullOrEmpty(messageId) && string.IsNullOrEmpty(threadId))
            return "Error: Provide either 'message_id' or 'thread_id'.";

        // Read a thread (multiple messages)
        if (!string.IsNullOrEmpty(threadId))
            return await ReadThreadAsync(threadId);

        // Read a single message
        return await ReadMessageAsync(messageId);
    }

    private async Task<string> ReadMessageAsync(string messageId)
    {
        var msg = await Client!.GetMessageAsync(messageId);
        return FormatMessage(msg);
    }

    private async Task<string> ReadThreadAsync(string threadId)
    {
        var thread = await Client!.GetThreadAsync(threadId);

        if (!thread.TryGetProperty("messages", out var messages))
            return "No messages found in this thread.";

        var sb = new StringBuilder();
        var msgList = messages.EnumerateArray().ToList();
        sb.AppendLine($"Thread {threadId} — {msgList.Count} message(s):");
        sb.AppendLine(new string('─', 60));

        foreach (var msg in msgList)
        {
            sb.AppendLine(FormatMessage(msg));
            sb.AppendLine(new string('─', 60));
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatMessage(JsonElement msg)
    {
        var sb = new StringBuilder();

        var id = msg.TryGetProperty("id", out var idProp) ? idProp.GetString() : "?";
        var threadId = msg.TryGetProperty("threadId", out var tidProp) ? tidProp.GetString() : "";

        JsonElement headers = default;
        JsonElement payload = default;
        if (msg.TryGetProperty("payload", out payload) && payload.TryGetProperty("headers", out headers))
        {
            // fine
        }

        var subject = headers.ValueKind == JsonValueKind.Array
            ? GmailClient.GetHeader(headers, "Subject") ?? "(no subject)" : "(no subject)";
        var from = headers.ValueKind == JsonValueKind.Array
            ? GmailClient.GetHeader(headers, "From") ?? "?" : "?";
        var to = headers.ValueKind == JsonValueKind.Array
            ? GmailClient.GetHeader(headers, "To") ?? "" : "";
        var cc = headers.ValueKind == JsonValueKind.Array
            ? GmailClient.GetHeader(headers, "Cc") ?? "" : "";
        var date = headers.ValueKind == JsonValueKind.Array
            ? GmailClient.GetHeader(headers, "Date") ?? "" : "";

        sb.AppendLine($"**{subject}**");
        sb.AppendLine($"Message ID: {id}");
        if (!string.IsNullOrEmpty(threadId))
            sb.AppendLine($"Thread ID: {threadId}");
        sb.AppendLine($"From: {from}");
        if (!string.IsNullOrEmpty(to))
            sb.AppendLine($"To: {to}");
        if (!string.IsNullOrEmpty(cc))
            sb.AppendLine($"Cc: {cc}");
        sb.AppendLine($"Date: {date}");

        // Labels
        if (msg.TryGetProperty("labelIds", out var labels))
        {
            var labelList = labels.EnumerateArray().Select(l => l.GetString()).ToList();
            sb.AppendLine($"Labels: {string.Join(", ", labelList)}");
        }

        sb.AppendLine();

        // Body
        if (payload.ValueKind == JsonValueKind.Object)
        {
            var body = GmailClient.ExtractBody(payload);
            // Truncate very long bodies
            if (body.Length > 5000)
                body = body[..5000] + "\n\n... (truncated, message is very long)";
            sb.AppendLine(body);
        }

        // Attachments
        var attachments = CollectAttachments(payload);
        if (attachments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Attachments:");
            foreach (var att in attachments)
                sb.AppendLine($"  - {att.Filename} ({att.MimeType}, {FormatSize(att.Size)})");
        }

        return sb.ToString();
    }

    private static List<AttachmentInfo> CollectAttachments(JsonElement payload)
    {
        var result = new List<AttachmentInfo>();
        if (payload.ValueKind != JsonValueKind.Object) return result;

        if (payload.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                var filename = part.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                if (!string.IsNullOrEmpty(filename))
                {
                    var mimeType = part.TryGetProperty("mimeType", out var mt) ? mt.GetString() : "unknown";
                    var size = part.TryGetProperty("body", out var b) && b.TryGetProperty("size", out var s)
                        ? s.GetInt32() : 0;
                    result.Add(new AttachmentInfo(filename, mimeType ?? "unknown", size));
                }

                // Recurse into nested parts
                result.AddRange(CollectAttachments(part));
            }
        }

        return result;
    }

    private static string FormatSize(int bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1048576) return $"{bytes / 1024.0:F1}KB";
        return $"{bytes / 1048576.0:F1}MB";
    }

    private record AttachmentInfo(string Filename, string MimeType, int Size);
}
