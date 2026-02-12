using System.CommandLine;
using Microsoft.Extensions.Logging;
using Sharpbot.Agent;
using Sharpbot.Bus;
using Sharpbot.Config;
using Sharpbot.Providers;
using Sharpbot.Services;
using Spectre.Console;

namespace Sharpbot.Commands;

/// <summary>CLI command: interact with the agent directly (single message or interactive REPL).</summary>
public sealed class AgentCommand : Command
{
    private readonly Option<string?> _messageOption = new("--message", "-m")
    {
        Description = "Message to send to the agent"
    };

    private readonly Option<string> _sessionOption = new("--session", "-s")
    {
        Description = "Session ID",
        DefaultValueFactory = _ => "cli:default"
    };

    private readonly Option<bool> _verboseOption = new("--verbose", "-v")
    {
        Description = "Verbose output (show telemetry, tool calls, etc.)"
    };

    public AgentCommand() : base("agent", "Interact with the agent directly.")
    {
        Options.Add(_messageOption);
        Options.Add(_sessionOption);
        Options.Add(_verboseOption);

        this.SetAction(async (parseResult, _) =>
        {
            var message = parseResult.GetValue(_messageOption);
            var sessionId = parseResult.GetValue(_sessionOption)!;
            var verbose = parseResult.GetValue(_verboseOption);
            await ExecuteAsync(message, sessionId, verbose);
        });
    }

    private static async Task ExecuteAsync(string? message, string sessionId, bool verbose)
    {
        using var loggerFactory = SharpbotServiceFactory.CreateLoggerFactory(
            verbose ? LogLevel.Debug : LogLevel.Information);
        var logger = loggerFactory.CreateLogger("sharpbot");

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

        // Create semantic memory store if enabled
        var smConfig = config.SemanticMemory;
        var db = Database.SharpbotDb.CreateDefault();
        SemanticMemoryStore? semanticMemory = null;
        if (smConfig.Enabled)
        {
            var embModel = EmbeddingModelResolver.Resolve(smConfig.EmbeddingModel, config.Agents.Defaults.Model);
            semanticMemory = new SemanticMemoryStore(db, provider, embModel, logger);
        }

        using var agentLoop = new AgentLoop(bus, provider, new AgentLoopOptions
        {
            Workspace = config.WorkspacePath,
            MaxTokens = config.Agents.Defaults.MaxTokens,
            Temperature = config.Agents.Defaults.Temperature,
            ModelOverrides = config.Agents.Defaults.ModelOverrides,
            MaxSessionMessages = config.Agents.Defaults.MaxSessionMessages,
            MaxContextTokens = config.Agents.Defaults.MaxContextTokens,
            BraveApiKey = string.IsNullOrEmpty(config.Tools.Web.Search.ApiKey) ? null : config.Tools.Web.Search.ApiKey,
            ExecConfig = config.Tools.Exec,
            RestrictToWorkspace = config.Tools.RestrictToWorkspace,
            SkillsConfig = config.Skills,
            AppConfig = config,
            SemanticMemory = semanticMemory,
            SemanticMemoryAutoEnrich = smConfig.AutoEnrich,
            SemanticMemoryAutoEnrichTopK = smConfig.AutoEnrichTopK,
            SemanticMemoryAutoEnrichMinScore = smConfig.MinScore,
        }, logger);

        if (message is not null)
        {
            var response = await agentLoop.ProcessDirectAsync(message, sessionId);
            AnsiConsole.MarkupLine($"\n{SharpbotInfo.Logo} {Markup.Escape(response)}");
            return;
        }

        // Interactive REPL
        AnsiConsole.MarkupLine($"{SharpbotInfo.Logo} Interactive mode (Ctrl+C to exit)\n");

        while (true)
        {
            try
            {
                var userInput = AnsiConsole.Ask<string>("[bold blue]You:[/] ");
                if (string.IsNullOrWhiteSpace(userInput)) continue;

                var response = await agentLoop.ProcessDirectAsync(userInput, sessionId);
                AnsiConsole.MarkupLine($"\n{SharpbotInfo.Logo} {Markup.Escape(response)}\n");
            }
            catch (Exception)
            {
                AnsiConsole.MarkupLine("\nGoodbye!");
                break;
            }
        }
    }
}
