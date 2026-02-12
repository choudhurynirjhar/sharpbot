using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Gmail;

/// <summary>
/// Manage Gmail messages — archive, trash, mark read/unread, add/remove labels.
/// </summary>
internal sealed class GmailManageTool : GmailToolBase
{
    public GmailManageTool(GmailClient? client, ILogger? logger) : base(client, logger) { }

    public override string Name => "gmail_manage";

    public override string Description =>
        "Manage Gmail messages: archive, trash, mark as read/unread, star/unstar, or add/remove labels. " +
        "Provide the 'message_id' and the 'action' to perform. " +
        "Actions: archive, trash, mark_read, mark_unread, star, unstar, add_label, remove_label. " +
        "For add_label/remove_label, also provide 'label' (e.g. IMPORTANT, or a custom label ID).";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["message_id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Gmail message ID to act on"
            },
            ["action"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Action to perform: archive, trash, mark_read, mark_unread, star, unstar, add_label, remove_label",
                ["enum"] = new[] { "archive", "trash", "mark_read", "mark_unread", "star", "unstar", "add_label", "remove_label" }
            },
            ["label"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Label ID for add_label/remove_label actions (e.g. IMPORTANT, STARRED, or custom label ID)"
            },
        },
        ["required"] = new[] { "message_id", "action" },
    };

    protected override async Task<string> ExecuteCoreAsync(Dictionary<string, object?> args)
    {
        var messageId = GetString(args, "message_id");
        var action = GetString(args, "action").ToLowerInvariant();
        var label = GetString(args, "label");

        if (string.IsNullOrWhiteSpace(messageId)) return "Error: 'message_id' is required.";
        if (string.IsNullOrWhiteSpace(action)) return "Error: 'action' is required.";

        List<string>? addLabels = null;
        List<string>? removeLabels = null;

        switch (action)
        {
            case "archive":
                removeLabels = ["INBOX"];
                break;

            case "trash":
                await Client!.TrashMessageAsync(messageId);
                return $"Message {messageId} moved to Trash.";

            case "mark_read":
                removeLabels = ["UNREAD"];
                break;

            case "mark_unread":
                addLabels = ["UNREAD"];
                break;

            case "star":
                addLabels = ["STARRED"];
                break;

            case "unstar":
                removeLabels = ["STARRED"];
                break;

            case "add_label":
                if (string.IsNullOrWhiteSpace(label))
                    return "Error: 'label' parameter is required for add_label action.";
                addLabels = [label];
                break;

            case "remove_label":
                if (string.IsNullOrWhiteSpace(label))
                    return "Error: 'label' parameter is required for remove_label action.";
                removeLabels = [label];
                break;

            default:
                return $"Error: Unknown action '{action}'. Use: archive, trash, mark_read, mark_unread, star, unstar, add_label, remove_label.";
        }

        await Client!.ModifyMessageAsync(messageId, addLabels, removeLabels);

        var sb = new StringBuilder();
        sb.Append($"Message {messageId}: {action} completed.");
        if (addLabels?.Count > 0)
            sb.Append($" Labels added: {string.Join(", ", addLabels)}");
        if (removeLabels?.Count > 0)
            sb.Append($" Labels removed: {string.Join(", ", removeLabels)}");

        return sb.ToString();
    }
}
