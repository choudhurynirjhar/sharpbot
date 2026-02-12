using Microsoft.Extensions.Logging;
using Sharpbot.Agent.Tools;
using Sharpbot.Bus;
using Sharpbot.Channels;
using Sharpbot.Providers;

namespace Sharpbot.Plugins;

/// <summary>
/// Core interface for Sharpbot plugins.
/// Plugins are loaded from external .NET assemblies and can contribute
/// tools, channels, hooks, and providers to the agent.
/// </summary>
public interface IPlugin : IDisposable
{
    /// <summary>Unique plugin name (matches the directory/manifest name).</summary>
    string Name { get; }

    /// <summary>Human-readable description.</summary>
    string Description { get; }

    /// <summary>Called once at startup with the plugin's configuration and services.</summary>
    Task InitializeAsync(PluginContext context);

    /// <summary>Return any tools this plugin provides.</summary>
    IEnumerable<ITool> GetTools() => [];

    /// <summary>Return any channels this plugin provides.</summary>
    IEnumerable<BaseChannel> GetChannels(MessageBus bus, ILogger? logger) => [];

    /// <summary>Return any hooks this plugin provides.</summary>
    IEnumerable<IPluginHook> GetHooks() => [];

    /// <summary>
    /// Return named LLM providers this plugin provides.
    /// The key is the provider name (e.g., "ollama", "anthropic-native").
    /// Providers are selected when the configured model starts with the provider name prefix,
    /// or when explicitly set in configuration.
    /// </summary>
    IEnumerable<(string Name, ILlmProvider Provider)> GetProviders() => [];
}
