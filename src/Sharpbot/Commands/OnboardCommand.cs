using System.CommandLine;
using Sharpbot.Config;
using Spectre.Console;

namespace Sharpbot.Commands;

/// <summary>CLI command: initialise Sharpbot configuration and workspace.</summary>
public sealed class OnboardCommand : Command
{
    public OnboardCommand() : base("onboard", "Initialize sharpbot configuration and workspace.")
    {
        this.SetAction(_ => Execute());
    }

    private static void Execute()
    {
        var configPath = ConfigLoader.GetConfigPath();

        if (File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[yellow]Config already exists at {configPath}[/]");
            if (!AnsiConsole.Confirm("Overwrite?", defaultValue: false))
                return;
        }

        // Write a minimal user config — only user-specific overrides.
        // All defaults come from the app-level appsettings.json shipped with the binary.
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
        AnsiConsole.MarkupLine($"[green]✓[/] Created config at {configPath}");

        var workspace = Utils.Helpers.GetWorkspacePath();
        AnsiConsole.MarkupLine($"[green]✓[/] Created workspace at {workspace}");

        CreateWorkspaceTemplates(workspace);

        AnsiConsole.MarkupLine($"\n{SharpbotInfo.Logo} sharpbot is ready!");
        AnsiConsole.MarkupLine("\nNext steps:");
        AnsiConsole.MarkupLine($"  1. Add your API key to [cyan]{Markup.Escape(configPath)}[/]");
        AnsiConsole.MarkupLine("     Or set env var: [cyan]SHARPBOT_Providers__Gemini__ApiKey=your-key[/]");
        AnsiConsole.MarkupLine("  2. Chat: [cyan]sharpbot agent -m \"Hello!\"[/]");
        AnsiConsole.MarkupLine("\n[dim]All defaults live in the app-level appsettings.json.[/]");
        AnsiConsole.MarkupLine($"[dim]Only put overrides in {Markup.Escape(configPath)}.[/]");
    }

    // ------------------------------------------------------------------
    // Workspace template helpers
    // ------------------------------------------------------------------

    private static void CreateWorkspaceTemplates(string workspace)
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
            AnsiConsole.MarkupLine($"  [dim]Created {filename}[/]");
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
        AnsiConsole.MarkupLine("  [dim]Created memory/MEMORY.md[/]");
    }
}
