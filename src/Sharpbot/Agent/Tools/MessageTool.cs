using Sharpbot.Bus;

namespace Sharpbot.Agent.Tools;

/// <summary>Tool to send messages to users on chat channels.</summary>
public sealed class MessageTool : ToolBase
{
    private Func<OutboundMessage, Task>? _sendCallback;
    private string _defaultChannel = "";
    private string _defaultChatId = "";

    public MessageTool(Func<OutboundMessage, Task>? sendCallback = null)
    {
        _sendCallback = sendCallback;
    }

    /// <summary>Set the current message context.</summary>
    public void SetContext(string channel, string chatId)
    {
        _defaultChannel = channel;
        _defaultChatId = chatId;
    }

    /// <summary>Set the callback for sending messages.</summary>
    public void SetSendCallback(Func<OutboundMessage, Task> callback) => _sendCallback = callback;

    public override string Name => "message";
    public override string Description => "Send a message to the user. Use this when you want to communicate something.";
    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["content"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The message content to send" },
            ["channel"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Optional: target channel (telegram, discord, etc.)" },
            ["chat_id"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Optional: target chat/user ID" },
        },
        ["required"] = new[] { "content" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var content = GetString(args, "content");
        var channel = GetString(args, "channel", _defaultChannel);
        var chatId = GetString(args, "chat_id", _defaultChatId);

        if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(chatId))
            return "Error: No target channel/chat specified";

        if (_sendCallback == null)
            return "Error: Message sending not configured";

        var msg = new OutboundMessage
        {
            Channel = channel,
            ChatId = chatId,
            Content = content,
        };

        try
        {
            await _sendCallback(msg);
            return $"Message sent to {channel}:{chatId}";
        }
        catch (Exception e)
        {
            return $"Error sending message: {e.Message}";
        }
    }
}
