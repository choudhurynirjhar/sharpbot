using System.Runtime.InteropServices;
using Sharpbot.Config;
using Sharpbot.Media;
using Sharpbot.Providers;
using Sharpbot.Services;

namespace Sharpbot.Api;

/// <summary>Status API — system health and configuration summary.</summary>
public static class StatusApi
{
    public static void MapStatusApi(this WebApplication app)
    {
        app.MapGet("/api/status", GetStatus).WithTags("Status");
    }

    private static IResult GetStatus(SharpbotConfig config, SharpbotHostedService gateway, MediaPipelineService mediaPipeline)
    {
        var configPath = ConfigLoader.GetConfigPath();
        var workspace = config.WorkspacePath;

        var providers = new List<object>();
        foreach (var spec in ProviderRegistry.Providers)
        {
            var p = SharpbotServiceFactory.GetProviderByName(config, spec.Name);
            if (p is null) continue;

            providers.Add(new
            {
                name = spec.Name,
                label = spec.Label,
                isLocal = spec.IsLocal,
                configured = spec.IsLocal
                    ? !string.IsNullOrEmpty(p.ApiBase)
                    : !string.IsNullOrEmpty(p.ApiKey),
            });
        }

        var cronStatus = gateway.CronService.Status();

        // Uptime
        var startedAt = gateway.StartedAt;
        var uptime = startedAt.HasValue ? DateTime.UtcNow - startedAt.Value : TimeSpan.Zero;

        // Channels
        var channelStatus = gateway.ChannelManager?.GetStatus() ?? new Dictionary<string, object>();
        var channels = new[] { "telegram", "whatsapp", "discord", "feishu" }.Select(name =>
        {
            var enabled = name switch
            {
                "telegram" => config.Channels.Telegram.Enabled,
                "whatsapp" => config.Channels.WhatsApp.Enabled,
                "discord" => config.Channels.Discord.Enabled,
                "feishu" => config.Channels.Feishu.Enabled,
                _ => false,
            };
            var running = channelStatus.ContainsKey(name);
            return new { name, enabled, running };
        }).ToList();

        // Tools
        var tools = gateway.Agent?.ToolNames ?? [];

        // Sessions
        var sessionCount = gateway.SessionManager.ListSessions().Count;
        var mediaStats = mediaPipeline.GetStats();

        return Results.Json(new
        {
            version = SharpbotInfo.Version,
            runtime = $".NET {Environment.Version}",
            os = RuntimeInformation.OSDescription,
            configPath,
            configExists = File.Exists(configPath),
            workspace,
            workspaceExists = Directory.Exists(workspace),
            model = config.Agents.Defaults.Model,
            agentReady = gateway.IsReady,
            agentError = gateway.Error,
            startedAt = startedAt?.ToString("o"),
            uptimeSeconds = (int)uptime.TotalSeconds,
            providers,
            channels,
            tools,
            toolCount = tools.Count,
            sessionCount,
            media = new
            {
                enabled = config.Tools.Media.Enabled,
                totalAssets = mediaStats.TotalAssets,
                activeAssets = mediaStats.ActiveAssets,
                expiredAssets = mediaStats.ExpiredAssets,
                byState = mediaStats.ByState,
                byDecision = mediaStats.ByDecision,
            },
            cron = cronStatus,
        });
    }
}
