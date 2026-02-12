using System.CommandLine;
using Microsoft.Extensions.Logging;
using Sharpbot.Agent;
using Sharpbot.Bus;
using Sharpbot.Channels;
using Sharpbot.Config;
using Sharpbot.Cron;
using Sharpbot.Heartbeat;
using Sharpbot.Providers;
using Sharpbot.Services;
using Sharpbot.Session;
using Spectre.Console;

namespace Sharpbot.Commands;

/// <summary>CLI command: start the sharpbot gateway with all services.</summary>
public sealed class GatewayCommand : Command
{
    private readonly Option<int> _portOption = new("--port", "-p")
    {
        Description = "Gateway port",
        DefaultValueFactory = _ => 56789
    };

    private readonly Option<bool> _verboseOption = new("--verbose")
    {
        Description = "Verbose output"
    };

    public GatewayCommand() : base("gateway", "Start the sharpbot gateway.")
    {
        Options.Add(_portOption);
        Options.Add(_verboseOption);

        this.SetAction(async (parseResult, cancellationToken) =>
        {
            var port = parseResult.GetValue(_portOption);
            var verbose = parseResult.GetValue(_verboseOption);
            await ExecuteAsync(port, verbose);
        });
    }

    private static async Task ExecuteAsync(int port, bool verbose)
    {
        using var loggerFactory = SharpbotServiceFactory.CreateLoggerFactory(
            verbose ? LogLevel.Debug : LogLevel.Information);
        var logger = loggerFactory.CreateLogger("sharpbot");

        AnsiConsole.MarkupLine($"{SharpbotInfo.Logo} Starting sharpbot gateway on port {port}...");

        var config = ConfigLoader.LoadConfig();
        using var bus = new MessageBus(logger);

        ILlmProvider provider;
        try
        {
            provider = SharpbotServiceFactory.CreateProvider(config, logger);
        }
        catch (ProviderConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return;
        }
        var db = Database.SharpbotDb.CreateDefault();
        var sessionManager = new SessionManager(db, logger);
        using var cronService = new CronService(db, logger);

        // Create semantic memory store if enabled
        var smConfig = config.SemanticMemory;
        SemanticMemoryStore? semanticMemory = null;
        if (smConfig.Enabled)
        {
            var embModel = EmbeddingModelResolver.Resolve(smConfig.EmbeddingModel, config.Agents.Defaults.Model);
            semanticMemory = new SemanticMemoryStore(db, provider, embModel, logger);
            logger.LogInformation("Semantic memory enabled (model: {Model})", embModel);
        }

        using var agent = new AgentLoop(bus, provider, new AgentLoopOptions
        {
            Workspace = config.WorkspacePath,
            Model = config.Agents.Defaults.Model,
            MaxIterations = config.Agents.Defaults.MaxToolIterations,
            MaxTokens = config.Agents.Defaults.MaxTokens,
            Temperature = config.Agents.Defaults.Temperature,
            ModelOverrides = config.Agents.Defaults.ModelOverrides,
            MaxSessionMessages = config.Agents.Defaults.MaxSessionMessages,
            MaxContextTokens = config.Agents.Defaults.MaxContextTokens,
            BraveApiKey = string.IsNullOrEmpty(config.Tools.Web.Search.ApiKey) ? null : config.Tools.Web.Search.ApiKey,
            ExecConfig = config.Tools.Exec,
            CronService = cronService,
            RestrictToWorkspace = config.Tools.RestrictToWorkspace,
            SessionManager = sessionManager,
            SkillsConfig = config.Skills,
            AppConfig = config,
            SemanticMemory = semanticMemory,
            SemanticMemoryAutoEnrich = smConfig.AutoEnrich,
            SemanticMemoryAutoEnrichTopK = smConfig.AutoEnrichTopK,
            SemanticMemoryAutoEnrichMinScore = smConfig.MinScore,
        }, logger);

        cronService.OnJob = async job =>
        {
            var response = await agent.ProcessDirectAsync(
                job.Payload.Message,
                sessionKey: $"cron:{job.Id}",
                channel: job.Payload.Channel ?? Channels.WellKnown.Cli,
                chatId: job.Payload.To ?? Channels.WellKnown.Direct);

            if (job.Payload.Deliver && job.Payload.To is not null)
            {
                await bus.PublishOutboundAsync(new OutboundMessage
                {
                    Channel = job.Payload.Channel ?? Channels.WellKnown.Cli,
                    ChatId = job.Payload.To,
                    Content = response,
                });
            }

            return response;
        };

        using var heartbeat = new HeartbeatService(
            workspace: config.WorkspacePath,
            onHeartbeat: prompt => agent.ProcessDirectAsync(prompt, sessionKey: "heartbeat"),
            intervalSeconds: 30 * 60,
            enabled: true,
            logger: logger);

        using var channels = new ChannelManager(config, bus, logger);

        if (channels.EnabledChannels.Count > 0)
            AnsiConsole.MarkupLine($"[green]✓[/] Channels enabled: {string.Join(", ", channels.EnabledChannels)}");
        else
            AnsiConsole.MarkupLine("[yellow]Warning: No channels enabled[/]");

        var cronStatus = cronService.Status();
        if ((int)(cronStatus["jobs"] ?? 0) > 0)
            AnsiConsole.MarkupLine($"[green]✓[/] Cron: {cronStatus["jobs"]} scheduled jobs");

        AnsiConsole.MarkupLine("[green]✓[/] Heartbeat: every 30m");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await cronService.StartAsync();
            await heartbeat.StartAsync();
            await Task.WhenAll(
                agent.RunAsync(cts.Token),
                channels.StartAllAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\nShutting down...");
            heartbeat.Stop();
            cronService.Stop();
            agent.Stop();
            await channels.StopAllAsync();
        }
    }
}
