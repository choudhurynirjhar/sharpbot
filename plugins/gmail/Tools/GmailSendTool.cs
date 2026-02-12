using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Gmail;

/// <summary>
/// Compose and send a new email via Gmail.
/// </summary>
internal sealed class GmailSendTool : GmailToolBase
{
    public GmailSendTool(GmailClient? client, ILogger? logger) : base(client, logger) { }

    public override string Name => "gmail_send";

    public override string Description =>
        "Compose and send a new email. " +
        "Requires 'to', 'subject', and 'body'. Optional: 'cc', 'bcc'.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["to"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Recipient email address(es), comma-separated for multiple"
            },
            ["subject"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Email subject line"
            },
            ["body"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Email body text (plain text)"
            },
            ["cc"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "CC recipients, comma-separated (optional)"
            },
            ["bcc"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "BCC recipients, comma-separated (optional)"
            },
        },
        ["required"] = new[] { "to", "subject", "body" },
    };

    protected override async Task<string> ExecuteCoreAsync(Dictionary<string, object?> args)
    {
        var to = GetString(args, "to");
        var subject = GetString(args, "subject");
        var body = GetString(args, "body");

        if (string.IsNullOrWhiteSpace(to)) return "Error: 'to' is required.";
        if (string.IsNullOrWhiteSpace(subject)) return "Error: 'subject' is required.";
        if (string.IsNullOrWhiteSpace(body)) return "Error: 'body' is required.";

        var cc = GetString(args, "cc");
        var bcc = GetString(args, "bcc");

        var result = await Client!.SendMessageAsync(to, subject, body, cc, bcc);

        var id = result.TryGetProperty("id", out var idProp) ? idProp.GetString() : "unknown";
        var threadId = result.TryGetProperty("threadId", out var tid) ? tid.GetString() : "";

        var sb = new StringBuilder();
        sb.AppendLine("Email sent successfully!");
        sb.AppendLine($"  Message ID: {id}");
        if (!string.IsNullOrEmpty(threadId))
            sb.AppendLine($"  Thread ID: {threadId}");
        sb.AppendLine($"  To: {to}");
        sb.AppendLine($"  Subject: {subject}");

        return sb.ToString().TrimEnd();
    }
}
