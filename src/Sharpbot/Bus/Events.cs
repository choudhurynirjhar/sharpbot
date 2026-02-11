namespace Sharpbot.Bus;

/// <summary>
/// Message received from a chat channel.
/// Immutable after construction — use <c>with</c> expressions to derive variants.
/// </summary>
public sealed record InboundMessage
{
    public required string Channel { get; init; }
    public required string SenderId { get; init; }
    public required string ChatId { get; init; }
    public required string Content { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public IReadOnlyList<string> Media { get; init; } = [];
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

    /// <summary>Unique key for session identification.</summary>
    public string SessionKey => $"{Channel}:{ChatId}";
}

/// <summary>
/// Message to send to a chat channel.
/// Immutable after construction — use <c>with</c> expressions to derive variants.
/// </summary>
public sealed record OutboundMessage
{
    public required string Channel { get; init; }
    public required string ChatId { get; init; }
    public required string Content { get; init; }
    public string? ReplyTo { get; init; }
    public IReadOnlyList<string> Media { get; init; } = [];
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
}
