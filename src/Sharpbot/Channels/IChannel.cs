using Microsoft.Extensions.Logging;
using Sharpbot.Bus;

namespace Sharpbot.Channels;

/// <summary>
/// Abstract base class for chat channel implementations.
/// Each channel (Telegram, Discord, etc.) should extend this class
/// to integrate with the sharpbot message bus.
/// </summary>
public abstract class BaseChannel(object config, MessageBus bus, ILogger? logger = null)
{
    public abstract string ChannelName { get; }
    protected object Config { get; } = config;
    protected MessageBus Bus { get; } = bus;
    protected ILogger? Logger { get; } = logger;
    protected volatile bool Running;

    /// <summary>Start the channel and begin listening for messages.</summary>
    public abstract Task StartAsync();

    /// <summary>Stop the channel and clean up resources.</summary>
    public abstract Task StopAsync();

    /// <summary>Send a message through this channel.</summary>
    public abstract Task SendAsync(OutboundMessage msg);

    /// <summary>Check if a sender is allowed to use this bot.</summary>
    public bool IsAllowed(string senderId, IReadOnlyList<string>? allowList = null)
    {
        if (allowList is null || allowList.Count == 0) return true;
        if (allowList.Contains(senderId)) return true;

        if (!senderId.Contains('|')) return false;

        return senderId.Split('|')
            .Any(part => !string.IsNullOrEmpty(part) && allowList.Contains(part));
    }

    /// <summary>Handle an incoming message from the chat platform.</summary>
    protected async Task HandleMessageAsync(
        string senderId,
        string chatId,
        string content,
        IReadOnlyList<string>? allowList = null,
        List<string>? media = null,
        Dictionary<string, object>? metadata = null)
    {
        if (!IsAllowed(senderId, allowList))
        {
            Logger?.LogWarning("Access denied for sender {SenderId} on channel {Channel}", senderId, ChannelName);
            return;
        }

        var msg = new InboundMessage
        {
            Channel = ChannelName,
            SenderId = senderId,
            ChatId = chatId,
            Content = content,
            Media = media ?? [],
            Metadata = metadata ?? new Dictionary<string, object>(),
        };

        await Bus.PublishInboundAsync(msg);
    }

    public bool IsRunning => Running;
}
