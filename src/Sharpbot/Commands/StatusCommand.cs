using System.CommandLine;
using Sharpbot.Config;
using Sharpbot.Providers;
using Sharpbot.Services;
using Spectre.Console;

namespace Sharpbot.Commands;

/// <summary>CLI command: display sharpbot status and configuration summary.</summary>
public sealed class StatusCommand : Command
{
    public StatusCommand() : base("status", "Show sharpbot status.")
    {
        this.SetAction(_ => Execute());
    }

    private static void Execute()
    {
        var configPath = ConfigLoader.GetConfigPath();
        var config = ConfigLoader.LoadConfig();
        var workspace = config.WorkspacePath;

        AnsiConsole.MarkupLine($"{SharpbotInfo.Logo} sharpbot Status (.NET {Environment.Version})\n");
        AnsiConsole.MarkupLine($"Config: {configPath} {(File.Exists(configPath) ? "[green]✓[/]" : "[red]✗[/]")}");
        AnsiConsole.MarkupLine($"Workspace: {workspace} {(Directory.Exists(workspace) ? "[green]✓[/]" : "[red]✗[/]")}");

        if (!File.Exists(configPath)) return;

        AnsiConsole.MarkupLine($"Model: {config.Agents.Defaults.Model}");

        foreach (var spec in ProviderRegistry.Providers)
        {
            var provider = SharpbotServiceFactory.GetProviderByName(config, spec.Name);
            if (provider is null) continue;

            if (spec.IsLocal)
            {
                AnsiConsole.MarkupLine(!string.IsNullOrEmpty(provider.ApiBase)
                    ? $"{spec.Label}: [green]✓ {provider.ApiBase}[/]"
                    : $"{spec.Label}: [dim]not set[/]");
            }
            else
            {
                var hasKey = !string.IsNullOrEmpty(provider.ApiKey);
                AnsiConsole.MarkupLine($"{spec.Label}: {(hasKey ? "[green]✓[/]" : "[dim]not set[/]")}");
            }
        }
    }
}
