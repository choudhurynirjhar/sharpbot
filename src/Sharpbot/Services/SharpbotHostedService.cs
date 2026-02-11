using Microsoft.Extensions.Logging;
using Sharpbot.Agent;
using Sharpbot.Bus;
using Sharpbot.Channels;
using Sharpbot.Config;
using Sharpbot.Cron;
using Sharpbot.Heartbeat;
using Sharpbot.Providers;
using Sharpbot.Session;
using Sharpbot.Telemetry;

namespace Sharpbot.Services;

/// <summary>
/// ASP.NET hosted service that manages the sharpbot gateway:
/// agent loop, cron service, heartbeat, and channel manager.
/// Exposes the AgentLoop for API access.
/// </summary>
public sealed class SharpbotHostedService : IHostedService, IDisposable
{
    private readonly SharpbotConfig _config;
    private readonly MessageBus _bus;
    private readonly SessionManager _sessionManager;
    private readonly CronService _cronService;
    private readonly ILogger<SharpbotHostedService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly UsageStore _usageStore;

    private AgentLoop? _agentLoop;
    private HeartbeatService? _heartbeat;
    private ChannelManager? _channels;
    private CancellationTokenSource? _cts;
    private Task? _agentTask;
    private Task? _channelsTask;

    private bool _isReady;
    private string? _error;
    private DateTime? _startedAt;

    public SharpbotHostedService(
        SharpbotConfig config,
        MessageBus bus,
        SessionManager sessionManager,
        CronService cronService,
        UsageStore usageStore,
        ILogger<SharpbotHostedService> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _bus = bus;
        _sessionManager = sessionManager;
        _cronService = cronService;
        _usageStore = usageStore;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>The active agent loop (null if not yet initialised or provider misconfigured).</summary>
    public AgentLoop? Agent => _agentLoop;

    /// <summary>Whether the agent loop is running and ready.</summary>
    public bool IsReady => _isReady;

    /// <summary>Initialisation error, if any.</summary>
    public string? Error => _error;

    /// <summary>The cron service.</summary>
    public CronService CronService => _cronService;

    /// <summary>The session manager.</summary>
    public SessionManager SessionManager => _sessionManager;

    /// <summary>The channel manager (null if not yet started).</summary>
    public ChannelManager? ChannelManager => _channels;

    /// <summary>When the service started.</summary>
    public DateTime? StartedAt => _startedAt;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("{Logo} Starting Sharpbot web gateway...", SharpbotInfo.Logo);

        // Auto-onboard if needed
        EnsureOnboarded();

        ILlmProvider provider;
        try
        {
            var providerLogger = _loggerFactory.CreateLogger("llm-provider");
            provider = SharpbotServiceFactory.CreateProvider(_config, providerLogger);
        }
        catch (ProviderConfigurationException ex)
        {
            _error = ex.Message;
            _logger.LogWarning("Agent not available: {Error}. Configure API keys via the Settings page.", ex.Message);
            _isReady = false;
            return;
        }

        var agentLogger = _loggerFactory.CreateLogger("agent");

        _agentLoop = new AgentLoop(_bus, provider, new AgentLoopOptions
        {
            Workspace = _config.WorkspacePath,
            Model = _config.Agents.Defaults.Model,
            MaxIterations = _config.Agents.Defaults.MaxToolIterations,
            MaxTokens = _config.Agents.Defaults.MaxTokens,
            Temperature = _config.Agents.Defaults.Temperature,
            ModelOverrides = _config.Agents.Defaults.ModelOverrides,
            MaxSessionMessages = _config.Agents.Defaults.MaxSessionMessages,
            BraveApiKey = string.IsNullOrEmpty(_config.Tools.Web.Search.ApiKey) ? null : _config.Tools.Web.Search.ApiKey,
            ExecConfig = _config.Tools.Exec,
            CronService = _cronService,
            RestrictToWorkspace = _config.Tools.RestrictToWorkspace,
            SessionManager = _sessionManager,
            SkillsConfig = _config.Skills,
            AppConfig = _config,
            OnTelemetry = telemetry => _usageStore.Record(telemetry),
        }, agentLogger);

        // Wire cron job execution
        _cronService.OnJob = async job =>
        {
            var response = await _agentLoop.ProcessDirectAsync(
                job.Payload.Message,
                sessionKey: $"cron:{job.Id}",
                channel: job.Payload.Channel ?? Channels.WellKnown.Cli,
                chatId: job.Payload.To ?? Channels.WellKnown.Direct);

            if (job.Payload.Deliver && job.Payload.To is not null)
            {
                await _bus.PublishOutboundAsync(new OutboundMessage
                {
                    Channel = job.Payload.Channel ?? Channels.WellKnown.Cli,
                    ChatId = job.Payload.To,
                    Content = response,
                });
            }

            return response;
        };

        // Start heartbeat
        _heartbeat = new HeartbeatService(
            workspace: _config.WorkspacePath,
            onHeartbeat: prompt => _agentLoop.ProcessDirectAsync(prompt, sessionKey: "heartbeat"),
            intervalSeconds: 30 * 60,
            enabled: true,
            logger: _loggerFactory.CreateLogger("heartbeat"));

        // Start channel manager (with session manager for Telegram /reset, and logger factory for per-channel loggers)
        _channels = new ChannelManager(
            _config, _bus,
            logger: _loggerFactory.CreateLogger("channels"),
            sessionManager: _sessionManager,
            loggerFactory: _loggerFactory);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start services
        await _cronService.StartAsync();
        await _heartbeat.StartAsync();

        _agentTask = _agentLoop.RunAsync(_cts.Token);
        _channelsTask = _channels.StartAllAsync(_cts.Token);

        _isReady = true;
        _error = null;
        _startedAt = DateTime.UtcNow;

        _logger.LogInformation("{Logo} Sharpbot gateway started — Model: {Model}", SharpbotInfo.Logo, _config.Agents.Defaults.Model);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Shutting down Sharpbot gateway...");

        _cts?.Cancel();

        _heartbeat?.Stop();
        _cronService.Stop();
        _agentLoop?.Stop();

        if (_channels != null)
            await _channels.StopAllAsync();

        if (_agentTask != null)
        {
            try { await _agentTask; }
            catch (OperationCanceledException) { }
        }

        _isReady = false;
    }

    private void EnsureOnboarded()
    {
        var configPath = ConfigLoader.GetConfigPath();
        if (File.Exists(configPath)) return;

        _logger.LogInformation("First run detected — creating config and workspace...");

        var configDir = Path.GetDirectoryName(configPath);
        if (configDir is not null) Directory.CreateDirectory(configDir);

        var minimalConfig = """
            {
              "Agents": {
                "Defaults": {
                  "Model": "gemini-2.5-flash"
                }
              },
              "Providers": {
                "Gemini": {
                  "ApiKey": ""
                }
              }
            }
            """;

        File.WriteAllText(configPath, minimalConfig);
        _logger.LogInformation("Created config at {Path}", configPath);

        var workspace = Utils.Helpers.GetWorkspacePath(_config.WorkspacePath);
        CreateWorkspaceTemplates(workspace);
        _logger.LogInformation("Created workspace at {Path}", workspace);
    }

    private void CreateWorkspaceTemplates(string workspace)
    {
        var templates = new Dictionary<string, string>
        {
            ["AGENTS.md"] = """
                # Agent Instructions

                You are a helpful AI assistant. Be concise, accurate, and friendly.

                ## Guidelines

                - Always explain what you're doing before taking actions
                - Ask for clarification when the request is ambiguous
                - Use tools to help accomplish tasks
                - Remember important information in your memory files
                """,
            ["SOUL.md"] = """
                # Soul

                I am sharpbot, a lightweight AI assistant.

                ## Personality

                - Helpful and friendly
                - Concise and to the point
                - Curious and eager to learn

                ## Values

                - Accuracy over speed
                - User privacy and safety
                - Transparency in actions
                """,
            ["USER.md"] = """
                # User

                Information about the user goes here.

                ## Preferences

                - Communication style: (casual/formal)
                - Timezone: (your timezone)
                - Language: (your preferred language)
                """,
        };

        foreach (var (filename, content) in templates)
        {
            var filePath = Path.Combine(workspace, filename);
            if (File.Exists(filePath)) continue;
            File.WriteAllText(filePath, content);
        }

        var memoryDir = Path.Combine(workspace, "memory");
        Directory.CreateDirectory(memoryDir);
        var memoryFile = Path.Combine(memoryDir, "MEMORY.md");

        if (File.Exists(memoryFile)) return;

        File.WriteAllText(memoryFile, """
            # Long-term Memory

            This file stores important information that should persist across sessions.

            ## User Information

            (Important facts about the user)

            ## Preferences

            (User preferences learned over time)

            ## Important Notes

            (Things to remember)
            """);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _agentLoop?.Dispose();
        _heartbeat?.Dispose();
        _channels?.Dispose();
    }
}
