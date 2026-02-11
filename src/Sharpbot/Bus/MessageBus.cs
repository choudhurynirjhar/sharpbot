using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sharpbot.Bus;

/// <summary>
/// Async message bus that decouples chat channels from the agent core.
/// Channels push messages to the inbound queue, and the agent processes
/// them and pushes responses to the outbound queue.
/// </summary>
public sealed class MessageBus(ILogger? logger = null) : IDisposable
{
    private readonly Channel<InboundMessage> _inbound = Channel.CreateUnbounded<InboundMessage>();
    private readonly Channel<OutboundMessage> _outbound = Channel.CreateUnbounded<OutboundMessage>();
    private readonly Dictionary<string, List<Func<OutboundMessage, Task>>> _outboundSubscribers = [];
    private readonly Lock _subscribersLock = new();
    private volatile bool _running;

    /// <summary>Publish a message from a channel to the agent.</summary>
    public ValueTask PublishInboundAsync(InboundMessage msg) =>
        _inbound.Writer.WriteAsync(msg);

    /// <summary>Consume the next inbound message.</summary>
    public ValueTask<InboundMessage> ConsumeInboundAsync(CancellationToken ct = default) =>
        _inbound.Reader.ReadAsync(ct);

    /// <summary>Try to consume an inbound message with timeout.</summary>
    public async Task<InboundMessage?> TryConsumeInboundAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _inbound.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Publish a response from the agent to channels.</summary>
    public ValueTask PublishOutboundAsync(OutboundMessage msg) =>
        _outbound.Writer.WriteAsync(msg);

    /// <summary>Consume the next outbound message.</summary>
    public ValueTask<OutboundMessage> ConsumeOutboundAsync(CancellationToken ct = default) =>
        _outbound.Reader.ReadAsync(ct);

    /// <summary>Try to consume an outbound message with timeout.</summary>
    public async Task<OutboundMessage?> TryConsumeOutboundAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _outbound.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Subscribe to outbound messages for a specific channel. Thread-safe.</summary>
    public void SubscribeOutbound(string channel, Func<OutboundMessage, Task> callback)
    {
        lock (_subscribersLock)
        {
            if (!_outboundSubscribers.TryGetValue(channel, out var subscribers))
            {
                subscribers = [];
                _outboundSubscribers[channel] = subscribers;
            }

            subscribers.Add(callback);
        }
    }

    /// <summary>Dispatch outbound messages to subscribed channels. Run as a background task.</summary>
    public async Task DispatchOutboundAsync(CancellationToken ct = default)
    {
        _running = true;
        while (_running && !ct.IsCancellationRequested)
        {
            var msg = await TryConsumeOutboundAsync(TimeSpan.FromSeconds(1), ct);
            if (msg is null) continue;

            List<Func<OutboundMessage, Task>>? subscribers;
            lock (_subscribersLock)
            {
                _outboundSubscribers.TryGetValue(msg.Channel, out subscribers);
                subscribers = subscribers?.ToList(); // snapshot under lock
            }

            if (subscribers is null) continue;

            foreach (var callback in subscribers)
            {
                try
                {
                    await callback(msg);
                }
                catch (Exception e)
                {
                    logger?.LogError(e, "Error dispatching to {Channel}", msg.Channel);
                }
            }
        }
    }

    /// <summary>Stop the dispatcher loop.</summary>
    public void Stop() => _running = false;

    public int InboundSize => _inbound.Reader.Count;
    public int OutboundSize => _outbound.Reader.Count;

    public void Dispose()
    {
        _inbound.Writer.TryComplete();
        _outbound.Writer.TryComplete();
    }
}
