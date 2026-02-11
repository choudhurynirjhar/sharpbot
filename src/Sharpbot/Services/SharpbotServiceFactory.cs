using Microsoft.Extensions.Logging;
using Sharpbot.Config;
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

        return new OpenAiCompatibleProvider(
            apiKey: provider?.ApiKey,
            apiBase: apiBase,
            defaultModel: model,
            extraHeaders: provider?.ExtraHeaders,
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
    /// Resolve a <see cref="ProviderConfig"/> by provider name (case-insensitive).
    /// Delegates to the single source of truth: <see cref="ProviderResolver"/>.
    /// </summary>
    public static ProviderConfig? GetProviderByName(SharpbotConfig config, string name) =>
        ProviderResolver.GetByName(config.Providers, name);
}
