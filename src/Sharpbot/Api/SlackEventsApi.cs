using System.Text.Json;
using Sharpbot.Channels;
using Sharpbot.Config;
using Sharpbot.Services;

namespace Sharpbot.Api;

/// <summary>Slack Events API webhook endpoint for HTTP mode.</summary>
public static class SlackEventsApi
{
    public static void MapSlackEventsApi(this WebApplication app)
    {
        var config = app.Services.GetRequiredService<SharpbotConfig>();

        // Only register the webhook endpoint if Slack is enabled in HTTP mode
        if (!config.Channels.Slack.Enabled) return;
        if (!config.Channels.Slack.Mode.Equals("http", StringComparison.OrdinalIgnoreCase)) return;

        var webhookPath = config.Channels.Slack.WebhookPath;
        if (string.IsNullOrEmpty(webhookPath)) webhookPath = "/slack/events";

        app.MapPost(webhookPath, HandleSlackEvent).WithTags("Slack");
    }

    private static async Task<IResult> HandleSlackEvent(
        HttpRequest request,
        SharpbotHostedService gateway)
    {
        // Read the raw body
        string body;
        using (var reader = new StreamReader(request.Body))
            body = await reader.ReadToEndAsync();

        // Get the Slack channel from the channel manager
        var slackChannel = gateway.ChannelManager?.GetChannel<SlackChannel>("slack");
        if (slackChannel is null)
            return Results.Json(new { error = "Slack channel not available" }, statusCode: 503);

        // Extract Slack signature headers
        var signature = request.Headers["X-Slack-Signature"].FirstOrDefault();
        var timestamp = request.Headers["X-Slack-Request-Timestamp"].FirstOrDefault();

        // Delegate to the channel's HTTP event handler
        var result = await slackChannel.HandleHttpEventAsync(body, signature, timestamp);

        return Results.Json(result ?? new { ok = true });
    }
}
