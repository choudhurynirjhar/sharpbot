using Microsoft.Extensions.Logging;
using Sharpbot.Agent.Tools;
using Sharpbot.Plugins;

namespace SharpbotPlugin.Jira;

/// <summary>
/// Jira integration plugin — provides tools for searching, browsing,
/// and exploring Jira tickets, projects, boards, and sprints.
///
/// Configuration via environment variables:
///   JIRA_BASE_URL       — Jira instance URL (e.g. https://yourcompany.atlassian.net or https://jira.company.com)
///   JIRA_EMAIL          — Atlassian account email (or username for Data Center)
///   JIRA_API_TOKEN      — API token or personal access token
///   JIRA_API_VERSION    — (optional) "2" for Data Center/Server, "3" for Cloud. Auto-detected if omitted.
/// </summary>
public sealed class JiraPlugin : IPlugin
{
    private JiraClient? _client;
    private ILogger? _logger;

    public string Name => "jira";
    public string Description => "Jira integration — search, browse, and explore tickets, projects, and boards.";

    public Task InitializeAsync(PluginContext context)
    {
        _logger = context.Logger;

        var baseUrl = Environment.GetEnvironmentVariable("JIRA_BASE_URL") ?? "";
        var email = Environment.GetEnvironmentVariable("JIRA_EMAIL") ?? "";
        var apiToken = Environment.GetEnvironmentVariable("JIRA_API_TOKEN") ?? "";

        // Also allow config overrides from appsettings.json Plugins:jira section
        if (context.Config.TryGetValue("baseUrl", out var cfgUrl) && cfgUrl is string url && !string.IsNullOrEmpty(url))
            baseUrl = url;
        if (context.Config.TryGetValue("email", out var cfgEmail) && cfgEmail is string em && !string.IsNullOrEmpty(em))
            email = em;
        if (context.Config.TryGetValue("apiToken", out var cfgToken) && cfgToken is string tok && !string.IsNullOrEmpty(tok))
            apiToken = tok;

        // Optional explicit API version override
        var apiVersion = Environment.GetEnvironmentVariable("JIRA_API_VERSION") ?? "";
        if (context.Config.TryGetValue("apiVersion", out var cfgVer) && cfgVer is string ver && !string.IsNullOrEmpty(ver))
            apiVersion = ver;

        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiToken))
        {
            _logger?.LogWarning(
                "Jira plugin: JIRA_BASE_URL and JIRA_API_TOKEN must be set. " +
                "Tools will return errors until configured.");
            _client = null;
        }
        else
        {
            _client = new JiraClient(baseUrl, email, apiToken,
                string.IsNullOrEmpty(apiVersion) ? null : apiVersion);

            // Log what was auto-detected
            var detectedVersion = baseUrl.Contains(".atlassian.net", StringComparison.OrdinalIgnoreCase) ? "3" : "2";
            var effectiveVersion = string.IsNullOrEmpty(apiVersion) ? detectedVersion : apiVersion;
            var mode = baseUrl.Contains(".atlassian.net", StringComparison.OrdinalIgnoreCase) ? "Cloud" : "Data Center/Server";
            _logger?.LogInformation("Jira plugin initialized — {Mode} ({Url}), REST API v{Version}",
                mode, baseUrl, effectiveVersion);
        }

        return Task.CompletedTask;
    }

    public IEnumerable<ITool> GetTools() =>
    [
        new JiraSearchTool(_client, _logger),
        new JiraTicketTool(_client, _logger),
        new JiraProjectsTool(_client, _logger),
        new JiraBoardsTool(_client, _logger),
        new JiraSprintsTool(_client, _logger),
        new JiraCommentsTool(_client, _logger),
    ];

    public void Dispose()
    {
        _client?.Dispose();
        _logger?.LogInformation("Jira plugin disposed");
    }
}
