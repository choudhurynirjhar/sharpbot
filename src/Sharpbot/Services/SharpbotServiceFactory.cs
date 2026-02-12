using Microsoft.Extensions.Logging;
using Sharpbot.Config;
using Sharpbot.Plugins;
using Sharpbot.Providers;
using Spectre.Console;

namespace Sharpbot.Services;

/// <summary>
/// Thrown when no valid LLM provider can be resolved from configuration.
/// Replaces the previous <c>Environment.Exit(1)</c> call, making the code
/// testable and following proper exception-based error handling.
/// </summary>
public sealed class ProviderConfigurationException : InvalidOperationException
{
    public ProviderConfigurationException(string message) : base(message) { }
}

/// <summary>
/// Factory for creating shared Sharpbot services.
/// Centralises wiring logic so individual commands stay thin.
/// </summary>
public static class SharpbotServiceFactory
{
    /// <summary>
    /// Resolve the active LLM provider from configuration.
    /// Throws <see cref="ProviderConfigurationException"/> when no valid API key is found
    /// (unless the model is a Bedrock model).
    /// </summary>
    /// <exception cref="ProviderConfigurationException">No API key configured.</exception>
    public static ILlmProvider CreateProvider(SharpbotConfig config, ILogger logger)
    {
        var provider = config.GetProvider();
        var model = config.Agents.Defaults.Model;

        if (provider is null || string.IsNullOrEmpty(provider.ApiKey))
        {
            if (!model.StartsWith("bedrock/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProviderConfigurationException(
                    $"No API key configured. Set one in {ConfigLoader.GetConfigPath()} under providers section, " +
                    "or via environment variable (e.g. SHARPBOT_Providers__Gemini__ApiKey).");
            }
        }

        var apiBase = config.GetApiBase();

        // Resolve embedding provider (may be different from the chat provider)
        var smConfig = config.SemanticMemory;
        string? embeddingApiKey = null;
        string? embeddingApiBase = null;
        if (smConfig.Enabled)
        {
            // Honor explicit overrides first
            embeddingApiKey = string.IsNullOrEmpty(smConfig.EmbeddingApiKey) ? null : smConfig.EmbeddingApiKey;
            embeddingApiBase = string.IsNullOrEmpty(smConfig.EmbeddingApiBase) ? null : smConfig.EmbeddingApiBase;

            // If the chat provider supports embeddings (same endpoint), use it directly
            // In this case, embeddingApiBase stays null so the main client is reused
            if (embeddingApiKey == null && embeddingApiBase == null)
            {
                // The chat provider's endpoint works for embeddings too, just pass the key
                embeddingApiKey = provider?.ApiKey;
                // embeddingApiBase stays null = reuse main client
            }
        }

        return new OpenAiCompatibleProvider(
            apiKey: provider?.ApiKey,
            apiBase: apiBase,
            defaultModel: model,
            extraHeaders: provider?.ExtraHeaders,
            embeddingApiKey: embeddingApiKey,
            embeddingApiBase: embeddingApiBase,
            logger: logger);
    }

    /// <summary>Create a console <see cref="ILoggerFactory"/> at the requested level.</summary>
    public static ILoggerFactory CreateLoggerFactory(LogLevel minimumLevel = LogLevel.Information)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(minimumLevel);
        });
    }

    /// <summary>
    /// Resolve an LLM provider, checking plugin providers first.
    /// If the configured model prefix matches a plugin provider name, use the plugin provider.
    /// Otherwise, fall back to the default provider resolution.
    /// </summary>
    public static ILlmProvider CreateProviderWithPlugins(
        SharpbotConfig config,
        ILogger logger,
        PluginLoader? pluginLoader)
    {
        if (pluginLoader != null)
        {
            var model = config.Agents.Defaults.Model;
            var pluginProviders = pluginLoader.GetAllProviders();

            // Check if any plugin provider matches the model prefix (e.g., "ollama/llama3")
            foreach (var (name, provider) in pluginProviders)
            {
                if (model.StartsWith($"{name}/", StringComparison.OrdinalIgnoreCase)
                    || model.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Using plugin provider '{Name}' for model '{Model}'", name, model);
                    return provider;
                }
            }

            // Also check if there's a plugin provider name in config
            var providerName = config.Agents.Defaults.Provider;
            if (!string.IsNullOrEmpty(providerName) && pluginProviders.TryGetValue(providerName, out var configured))
            {
                logger.LogInformation("Using configured plugin provider '{Name}'", providerName);
                return configured;
            }
        }

        // Fall back to default built-in provider
        return CreateProvider(config, logger);
    }

    /// <summary>
    /// Resolve a <see cref="ProviderConfig"/> by provider name (case-insensitive).
    /// Delegates to the single source of truth: <see cref="ProviderResolver"/>.
    /// </summary>
    public static ProviderConfig? GetProviderByName(SharpbotConfig config, string name) =>
        ProviderResolver.GetByName(config.Providers, name);
}
