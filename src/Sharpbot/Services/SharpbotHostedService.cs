using Microsoft.Extensions.Logging;
using Sharpbot.Agent;
using Sharpbot.Bus;
using Sharpbot.Channels;
using Sharpbot.Config;
using Sharpbot.Cron;
using Sharpbot.Heartbeat;
using Sharpbot.Plugins;
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
    private readonly Database.SharpbotDb _db;
    private readonly PluginLoader _pluginLoader;
    private readonly ILogger<SharpbotHostedService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly UsageStore _usageStore;
    private readonly ExecApprovalManager _execApprovalManager;

    private AgentLoop? _agentLoop;
    private Sharpbot.Agent.SemanticMemoryStore? _semanticMemory;
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
        Database.SharpbotDb db,
        PluginLoader pluginLoader,
        ExecApprovalManager execApprovalManager,
        UsageStore usageStore,
        ILogger<SharpbotHostedService> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _bus = bus;
        _sessionManager = sessionManager;
        _cronService = cronService;
        _db = db;
        _pluginLoader = pluginLoader;
        _execApprovalManager = execApprovalManager;
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

    /// <summary>The app configuration.</summary>
    public SharpbotConfig? Config => _config;

    /// <summary>The semantic memory store (null if disabled or not yet initialised).</summary>
    public Sharpbot.Agent.SemanticMemoryStore? SemanticMemory => _semanticMemory;

    /// <summary>When the service started.</summary>
    public DateTime? StartedAt => _startedAt;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("{Logo} Starting Sharpbot web gateway...", SharpbotInfo.Logo);

        // Auto-onboard if needed
        EnsureOnboarded();

        // Load plugins early (independent of provider availability)
        var pluginsDir = _config.Plugins.PluginsDir;
        if (!Path.IsPathRooted(pluginsDir))
            pluginsDir = Path.Combine(AppContext.BaseDirectory, pluginsDir);
        await _pluginLoader.LoadPluginsAsync(
            pluginsDir,
            _config.WorkspacePath,
            _config.Plugins.Entries,
            _loggerFactory);

        ILlmProvider provider;
        try
        {
            var providerLogger = _loggerFactory.CreateLogger("llm-provider");
            provider = SharpbotServiceFactory.CreateProviderWithPlugins(_config, providerLogger, _pluginLoader);
        }
        catch (ProviderConfigurationException ex)
        {
            _error = ex.Message;
            _logger.LogWarning("Agent not available: {Error}. Configure API keys via the Settings page.", ex.Message);
            _isReady = false;
            return;
        }

        var agentLogger = _loggerFactory.CreateLogger("agent");

        // Create semantic memory store if enabled
        var smConfig = _config.SemanticMemory;
        Sharpbot.Agent.SemanticMemoryStore? semanticMemory = null;
        if (smConfig.Enabled)
        {
            var smLogger = _loggerFactory.CreateLogger("semantic-memory");
            var embeddingModel = Sharpbot.Agent.EmbeddingModelResolver.Resolve(smConfig.EmbeddingModel, _config.Agents.Defaults.Model);
            semanticMemory = new Sharpbot.Agent.SemanticMemoryStore(
                _db, provider,
                embeddingModel,
                smLogger);
            _logger.LogInformation("Semantic memory enabled (model: {Model}, autoEnrich: {AutoEnrich})",
                embeddingModel, smConfig.AutoEnrich);
            _semanticMemory = semanticMemory;
        }

        _agentLoop = new AgentLoop(_bus, provider, new AgentLoopOptions
        {
            Workspace = _config.WorkspacePath,
            Model = _config.Agents.Defaults.Model,
            MaxIterations = _config.Agents.Defaults.MaxToolIterations,
            MaxTokens = _config.Agents.Defaults.MaxTokens,
            Temperature = _config.Agents.Defaults.Temperature,
            ModelOverrides = _config.Agents.Defaults.ModelOverrides,
            MaxSessionMessages = _config.Agents.Defaults.MaxSessionMessages,
            MaxContextTokens = _config.Agents.Defaults.MaxContextTokens,
            BraveApiKey = string.IsNullOrEmpty(_config.Tools.Web.Search.ApiKey) ? null : _config.Tools.Web.Search.ApiKey,
            ExecConfig = _config.Tools.Exec,
            ExecApprovalManager = _execApprovalManager,
            CronService = _cronService,
            RestrictToWorkspace = _config.Tools.RestrictToWorkspace,
            SessionManager = _sessionManager,
            SkillsConfig = _config.Skills,
            AppConfig = _config,
            SemanticMemory = semanticMemory,
            SemanticMemoryAutoEnrich = smConfig.AutoEnrich,
            SemanticMemoryAutoEnrichTopK = smConfig.AutoEnrichTopK,
            SemanticMemoryAutoEnrichMinScore = smConfig.MinScore,
            OnTelemetry = telemetry => _usageStore.Record(telemetry),
            PluginLoader = _pluginLoader,
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

        // Register plugin-contributed channels
        var pluginChannels = _pluginLoader.GetAllChannels(_bus, _loggerFactory.CreateLogger("plugin-channels"));
        foreach (var channel in pluginChannels)
        {
            _channels.RegisterChannel(channel.ChannelName, channel);
            _logger.LogInformation("Registered plugin channel: {Name}", channel.ChannelName);
        }

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
            # Pinned Notes

            This file contains always-visible facts that are injected into every conversation.
            Keep it small — only core identity and critical preferences belong here.
            For everything else, use `memory_index` / `memory_search` (semantic memory).

            ## User Information

            (Your name, role, or identity)

            ## Core Preferences

            (Always-needed preferences like timezone, language, communication style)
            """);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _agentLoop?.Dispose();
        _heartbeat?.Dispose();
        _channels?.Dispose();
        _pluginLoader.Dispose();
    }
}
