using Sharpbot.Config;
using Sharpbot.Services;

namespace Sharpbot.Api;

/// <summary>Channels API — view channel configuration and status.</summary>
public static class ChannelsApi
{
    public static void MapChannelsApi(this WebApplication app)
    {
        app.MapGet("/api/channels", GetChannels).WithTags("Channels");
    }

    private static IResult GetChannels(SharpbotConfig config, SharpbotHostedService gateway)
    {
        var runningChannels = gateway.ChannelManager?.GetStatus() ?? new Dictionary<string, object>();

        var channels = new object[]
        {
            BuildChannelInfo("telegram", "Telegram",
                config.Channels.Telegram.Enabled,
                runningChannels.ContainsKey("telegram"),
                new {
                    hasToken = !string.IsNullOrEmpty(config.Channels.Telegram.Token),
                    allowedUsers = config.Channels.Telegram.AllowFrom?.Count ?? 0,
                }),
            BuildChannelInfo("whatsapp", "WhatsApp",
                config.Channels.WhatsApp.Enabled,
                runningChannels.ContainsKey("whatsapp"),
                new {
                    hasBridgeUrl = !string.IsNullOrEmpty(config.Channels.WhatsApp.BridgeUrl),
                    bridgeUrl = config.Channels.WhatsApp.BridgeUrl ?? "",
                }),
            BuildChannelInfo("discord", "Discord",
                config.Channels.Discord.Enabled,
                runningChannels.ContainsKey("discord"),
                new {
                    hasToken = !string.IsNullOrEmpty(config.Channels.Discord.Token),
                }),
            BuildChannelInfo("feishu", "Feishu",
                config.Channels.Feishu.Enabled,
                runningChannels.ContainsKey("feishu"),
                new {
                    hasAppId = !string.IsNullOrEmpty(config.Channels.Feishu.AppId),
                }),
            BuildChannelInfo("slack", "Slack",
                config.Channels.Slack.Enabled,
                runningChannels.ContainsKey("slack"),
                new {
                    hasBotToken = !string.IsNullOrEmpty(config.Channels.Slack.BotToken),
                    hasAppToken = !string.IsNullOrEmpty(config.Channels.Slack.AppToken),
                    mode = config.Channels.Slack.Mode,
                    allowedUsers = config.Channels.Slack.AllowFrom?.Count ?? 0,
                }),
        };

        var enabledCount = channels.Cast<dynamic>().Count(c => c.enabled == true);
        var runningCount = channels.Cast<dynamic>().Count(c => c.running == true);

        return Results.Json(new { channels, enabledCount, runningCount });
    }

    private static object BuildChannelInfo(string id, string label, bool enabled, bool running, object config)
    {
        string status;
        if (running) status = "running";
        else if (enabled) status = "enabled";
        else status = "disabled";

        return new { id, label, enabled, running, status, config };
    }
}
