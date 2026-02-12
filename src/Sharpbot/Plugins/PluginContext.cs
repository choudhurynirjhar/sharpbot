using Microsoft.Extensions.Logging;

namespace Sharpbot.Plugins;

/// <summary>
/// Context provided to plugins during <see cref="IPlugin.InitializeAsync"/>.
/// Gives plugins access to configuration, logging, and the workspace
/// without exposing internal implementation details.
/// </summary>
public sealed class PluginContext
{
    /// <summary>Plugin-specific configuration from appsettings.json under Plugins:{name}.</summary>
    public IReadOnlyDictionary<string, object?> Config { get; init; } = new Dictionary<string, object?>();

    /// <summary>Root workspace directory path.</summary>
    public required string Workspace { get; init; }

    /// <summary>Logger for the plugin to use.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Application data directory (for plugin-specific persistent storage).</summary>
    public required string DataDir { get; init; }
}
