using Microsoft.Extensions.Logging;
using Sharpbot.Bus;
using Sharpbot.Config;
using Sharpbot.Session;

namespace Sharpbot.Channels;

/// <summary>
/// Manages chat channels and coordinates message routing.
/// Responsibilities:
/// - Initialize enabled channels (Telegram, WhatsApp, Discord, Feishu, Slack)
/// - Start/stop channels
/// - Route outbound messages to the correct channel
/// </summary>
public sealed class ChannelManager : IDisposable
{
    private readonly SharpbotConfig _config;
    private readonly MessageBus _bus;
    private readonly Dictionary<string, BaseChannel> _channels = [];
    private Task? _dispatchTask;
    private CancellationTokenSource? _cts;
    private readonly ILogger? _logger;

    public ChannelManager(
        SharpbotConfig config,
        MessageBus bus,
        ILogger? logger = null,
        SessionManager? sessionManager = null,
        ILoggerFactory? loggerFactory = null)
    {
        _config = config;
        _bus = bus;
        _logger = logger;

        InitChannels(sessionManager, loggerFactory);
    }

    /// <summary>Initialize channel instances based on configuration.</summary>
    private void InitChannels(SessionManager? sessionManager, ILoggerFactory? loggerFactory)
    {
        // Telegram channel
        if (_config.Channels.Telegram.Enabled)
        {
            try
            {
                var channel = new TelegramChannel(
                    _config.Channels.Telegram,
                    _bus,
                    sessionManager: sessionManager,
                    groqApiKey: !string.IsNullOrEmpty(_config.Providers.Groq.ApiKey)
                        ? _config.Providers.Groq.ApiKey : null,
                    logger: loggerFactory?.CreateLogger("telegram"));
                RegisterChannel("telegram", channel);
                _logger?.LogInformation("Telegram channel enabled");
            }
            catch (Exception e)
            {
                _logger?.LogWarning(e, "Telegram channel not available");
            }
        }

        // WhatsApp channel
        if (_config.Channels.WhatsApp.Enabled)
        {
            try
            {
                var channel = new WhatsAppChannel(
                    _config.Channels.WhatsApp,
                    _bus,
                    logger: loggerFactory?.CreateLogger("whatsapp"));
                RegisterChannel("whatsapp", channel);
                _logger?.LogInformation("WhatsApp channel enabled");
            }
            catch (Exception e)
            {
                _logger?.LogWarning(e, "WhatsApp channel not available");
            }
        }

        // Discord channel
        if (_config.Channels.Discord.Enabled)
        {
            try
            {
                var channel = new DiscordChannel(
                    _config.Channels.Discord,
                    _bus,
                    logger: loggerFactory?.CreateLogger("discord"));
                RegisterChannel("discord", channel);
                _logger?.LogInformation("Discord channel enabled");
            }
            catch (Exception e)
            {
                _logger?.LogWarning(e, "Discord channel not available");
            }
        }

        // Feishu channel
        if (_config.Channels.Feishu.Enabled)
        {
            try
            {
                var channel = new FeishuChannel(
                    _config.Channels.Feishu,
                    _bus,
                    logger: loggerFactory?.CreateLogger("feishu"));
                RegisterChannel("feishu", channel);
                _logger?.LogInformation("Feishu channel enabled");
            }
            catch (Exception e)
            {
                _logger?.LogWarning(e, "Feishu channel not available");
            }
        }

        // Slack channel
        if (_config.Channels.Slack.Enabled)
        {
            try
            {
                var channel = new SlackChannel(
                    _config.Channels.Slack,
                    _bus,
                    logger: loggerFactory?.CreateLogger("slack"));
                RegisterChannel("slack", channel);
                _logger?.LogInformation("Slack channel enabled (mode: {Mode})", _config.Channels.Slack.Mode);
            }
            catch (Exception e)
            {
                _logger?.LogWarning(e, "Slack channel not available");
            }
        }
    }

    /// <summary>Register a channel.</summary>
    public void RegisterChannel(string name, BaseChannel channel)
    {
        _channels[name] = channel;
    }

    /// <summary>Start all channels and the outbound dispatcher.</summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        if (_channels.Count == 0)
        {
            _logger?.LogWarning("No channels enabled");
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _dispatchTask = Task.Run(() => DispatchOutboundAsync(_cts.Token), _cts.Token);

        var tasks = new List<Task>();
        foreach (var (name, channel) in _channels)
        {
            _logger?.LogInformation("Starting {Name} channel...", name);
            tasks.Add(Task.Run(async () =>
            {
                try { await channel.StartAsync(); }
                catch (Exception e) { _logger?.LogError(e, "Failed to start channel {Name}", name); }
            }, _cts.Token));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>Stop all channels and the dispatcher.</summary>
    public async Task StopAllAsync()
    {
        _logger?.LogInformation("Stopping all channels...");
        _cts?.Cancel();

        if (_dispatchTask != null)
        {
            try { await _dispatchTask; }
            catch (OperationCanceledException) { }
        }

        foreach (var (name, channel) in _channels)
        {
            try
            {
                await channel.StopAsync();
                _logger?.LogInformation("Stopped {Name} channel", name);
            }
            catch (Exception e) { _logger?.LogError(e, "Error stopping {Name}", name); }
        }
    }

    private async Task DispatchOutboundAsync(CancellationToken ct)
    {
        _logger?.LogInformation("Outbound dispatcher started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var msg = await _bus.TryConsumeOutboundAsync(TimeSpan.FromSeconds(1), ct);
                if (msg == null) continue;

                if (_channels.TryGetValue(msg.Channel, out var channel))
                {
                    try { await channel.SendAsync(msg); }
                    catch (Exception e) { _logger?.LogError(e, "Error sending to {Channel}", msg.Channel); }
                }
                else
                {
                    _logger?.LogWarning("Unknown channel: {Channel}", msg.Channel);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Get a channel by name.</summary>
    public BaseChannel? GetChannel(string name) => _channels.GetValueOrDefault(name);

    /// <summary>Get a typed channel by name.</summary>
    public T? GetChannel<T>(string name) where T : BaseChannel => _channels.GetValueOrDefault(name) as T;

    /// <summary>Get status of all channels.</summary>
    public Dictionary<string, object> GetStatus()
    {
        return _channels.ToDictionary(
            kvp => kvp.Key,
            kvp => (object)new { enabled = true, running = kvp.Value.IsRunning });
    }

    /// <summary>Get list of enabled channel names.</summary>
    public List<string> EnabledChannels => [.. _channels.Keys];

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
