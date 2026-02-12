using Microsoft.Extensions.Logging;
using Sharpbot.Agent.Tools;
using Sharpbot.Plugins;

namespace SharpbotPlugin.Gmail;

/// <summary>
/// Gmail integration plugin — provides tools for searching, reading, sending,
/// replying to, and managing emails via OAuth2.
///
/// Configuration via environment variables:
///   GMAIL_CLIENT_ID      — OAuth2 client ID from Google Cloud Console
///   GMAIL_CLIENT_SECRET   — OAuth2 client secret
///   GMAIL_TOKEN_PATH      — (optional) path to store the refresh token,
///                           defaults to {data-dir}/gmail-token.json
///
/// One-time setup: run 'sharpbot gmail-auth' to authorize via browser.
/// </summary>
public sealed class GmailPlugin : IPlugin
{
    private GmailClient? _client;
    private GmailAuthManager? _auth;
    private ILogger? _logger;

    public string Name => "gmail";
    public string Description => "Gmail integration — search, read, send, reply, and manage emails via OAuth2.";

    public Task InitializeAsync(PluginContext context)
    {
        _logger = context.Logger;

        var clientId = Environment.GetEnvironmentVariable("GMAIL_CLIENT_ID") ?? "";
        var clientSecret = Environment.GetEnvironmentVariable("GMAIL_CLIENT_SECRET") ?? "";
        var tokenPath = Environment.GetEnvironmentVariable("GMAIL_TOKEN_PATH") ?? "";

        // Allow config overrides from appsettings.json Plugins:gmail section
        if (context.Config.TryGetValue("clientId", out var cfgId) && cfgId is string id && !string.IsNullOrEmpty(id))
            clientId = id;
        if (context.Config.TryGetValue("clientSecret", out var cfgSecret) && cfgSecret is string sec && !string.IsNullOrEmpty(sec))
            clientSecret = sec;
        if (context.Config.TryGetValue("tokenPath", out var cfgPath) && cfgPath is string tp && !string.IsNullOrEmpty(tp))
            tokenPath = tp;

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger?.LogWarning(
                "Gmail plugin: GMAIL_CLIENT_ID and GMAIL_CLIENT_SECRET must be set. " +
                "Tools will return errors until configured.");
            _client = null;
            return Task.CompletedTask;
        }

        // Default token path: inside the plugin's own directory
        if (string.IsNullOrEmpty(tokenPath))
        {
            var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins", "gmail", "data");
            tokenPath = Path.Combine(pluginDir, "gmail-token.json");
        }

        _auth = new GmailAuthManager(clientId, clientSecret, tokenPath, _logger);
        _client = new GmailClient(_auth, _logger);

        if (_auth.IsAuthorized)
            _logger?.LogInformation("Gmail plugin initialized — authorized, token at {TokenPath}", tokenPath);
        else
            _logger?.LogWarning(
                "Gmail plugin initialized but NOT authorized. Run 'sharpbot gmail-auth' to complete setup. " +
                "Token will be saved to {TokenPath}", tokenPath);

        return Task.CompletedTask;
    }

    public IEnumerable<ITool> GetTools() =>
    [
        new GmailSearchTool(_client, _logger),
        new GmailReadTool(_client, _logger),
        new GmailSendTool(_client, _logger),
        new GmailReplyTool(_client, _logger),
        new GmailManageTool(_client, _logger),
    ];

    public void Dispose()
    {
        _client?.Dispose();
        _logger?.LogInformation("Gmail plugin disposed");
    }
}
