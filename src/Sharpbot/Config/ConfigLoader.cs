using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Sharpbot.Config;

/// <summary>
/// Configuration loading using standard .NET IConfiguration.
/// Layers: app-level appsettings.json → data-dir appsettings.json → environment variables.
/// All paths are relative to the application directory (no home-directory dependency).
/// </summary>
public static class ConfigLoader
{
    private const string ConfigFileName = "appsettings.json";
    private const string EnvPrefix = "SHARPBOT_";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Get the sharpbot data directory ({app}/data).</summary>
    public static string GetConfigDir() => Utils.Helpers.GetDataPath();

    /// <summary>Get the user-level config file path ({app}/data/appsettings.json).</summary>
    public static string GetConfigPath() => Path.Combine(GetConfigDir(), ConfigFileName);

    /// <summary>Get the sharpbot data directory ({app}/data).</summary>
    public static string GetDataDir() => Utils.Helpers.GetDataPath();

    /// <summary>
    /// Build an <see cref="IConfiguration"/> from the standard layered sources.
    /// <list type="number">
    ///   <item>appsettings.json next to the executable (app-level defaults)</item>
    ///   <item>{app}/data/appsettings.json (user-level overrides)</item>
    ///   <item>Environment variables prefixed with <c>SHARPBOT_</c> (e.g. SHARPBOT_Providers__Gemini__ApiKey)</item>
    /// </list>
    /// </summary>
    public static IConfiguration BuildConfiguration(string? configPath = null)
    {
        var builder = new ConfigurationBuilder();

        // Layer 1: App-level defaults (appsettings.json beside the binary)
        var appDir = AppContext.BaseDirectory;
        builder.AddJsonFile(Path.Combine(appDir, ConfigFileName), optional: true, reloadOnChange: false);

        // Layer 2: User-level config in data directory ({app}/data/appsettings.json)
        var userConfigPath = configPath ?? GetConfigPath();
        builder.AddJsonFile(userConfigPath, optional: true, reloadOnChange: false);

        // Layer 3: Environment variables (SHARPBOT_ prefix is stripped)
        //   e.g.  SHARPBOT_Providers__Gemini__ApiKey  →  Providers:Gemini:ApiKey
        builder.AddEnvironmentVariables(EnvPrefix);

        return builder.Build();
    }

    /// <summary>
    /// Load configuration from the standard sources and bind to <see cref="SharpbotConfig"/>.
    /// Automatically migrates the user config file if it uses an older format.
    /// Secrets (API keys, tokens) are loaded exclusively from environment variables.
    /// </summary>
    public static SharpbotConfig LoadConfig(string? configPath = null)
    {
        // Run migration on the user-level config file before loading
        var userConfigPath = configPath ?? GetConfigPath();
        ConfigMigrator.MigrateFile(userConfigPath);

        var configuration = BuildConfiguration(configPath);
        var config = new SharpbotConfig();
        configuration.Bind(config);

        // Overwrite secrets from environment variables ONLY — config files are
        // never trusted for secrets, even if they contain values.
        BindSecretsFromEnv(config);

        return config;
    }

    /// <summary>
    /// All secret field names that must come from environment variables only.
    /// Each entry maps to SHARPBOT_{key} (with __ as the section separator).
    /// </summary>
    internal static readonly (string EnvSuffix, Action<SharpbotConfig, string> Apply)[] SecretBindings =
    [
        // Provider API keys
        ("Providers__Anthropic__ApiKey",  (c, v) => c.Providers.Anthropic.ApiKey  = v),
        ("Providers__OpenAI__ApiKey",     (c, v) => c.Providers.OpenAI.ApiKey     = v),
        ("Providers__OpenRouter__ApiKey", (c, v) => c.Providers.OpenRouter.ApiKey = v),
        ("Providers__DeepSeek__ApiKey",   (c, v) => c.Providers.DeepSeek.ApiKey   = v),
        ("Providers__Groq__ApiKey",       (c, v) => c.Providers.Groq.ApiKey       = v),
        ("Providers__Gemini__ApiKey",     (c, v) => c.Providers.Gemini.ApiKey     = v),
        ("Providers__Zhipu__ApiKey",      (c, v) => c.Providers.Zhipu.ApiKey      = v),
        ("Providers__DashScope__ApiKey",  (c, v) => c.Providers.DashScope.ApiKey  = v),
        ("Providers__Moonshot__ApiKey",   (c, v) => c.Providers.Moonshot.ApiKey   = v),
        ("Providers__AiHubMix__ApiKey",   (c, v) => c.Providers.AiHubMix.ApiKey   = v),
        ("Providers__Vllm__ApiKey",       (c, v) => c.Providers.Vllm.ApiKey       = v),

        // Channel tokens & secrets
        ("Channels__Telegram__Token",              (c, v) => c.Channels.Telegram.Token              = v),
        ("Channels__Discord__Token",               (c, v) => c.Channels.Discord.Token               = v),
        ("Channels__Feishu__AppSecret",            (c, v) => c.Channels.Feishu.AppSecret            = v),
        ("Channels__Feishu__EncryptKey",           (c, v) => c.Channels.Feishu.EncryptKey           = v),
        ("Channels__Feishu__VerificationToken",    (c, v) => c.Channels.Feishu.VerificationToken    = v),
        ("Channels__Slack__BotToken",              (c, v) => c.Channels.Slack.BotToken              = v),
        ("Channels__Slack__AppToken",              (c, v) => c.Channels.Slack.AppToken              = v),
        ("Channels__Slack__SigningSecret",          (c, v) => c.Channels.Slack.SigningSecret          = v),

        // Tool API keys
        ("Tools__Web__Search__ApiKey",  (c, v) => c.Tools.Web.Search.ApiKey = v),
    ];

    /// <summary>
    /// Overwrite all secret fields exclusively from environment variables.
    /// Any secrets present in config files are cleared and replaced.
    /// </summary>
    private static void BindSecretsFromEnv(SharpbotConfig config)
    {
        foreach (var (envSuffix, apply) in SecretBindings)
        {
            var value = Environment.GetEnvironmentVariable($"{EnvPrefix}{envSuffix}") ?? "";
            apply(config, value);
        }
    }

    /// <summary>
    /// Expose the raw <see cref="IConfiguration"/> for advanced scenarios (DI, options pattern, etc.).
    /// </summary>
    public static IConfiguration GetConfiguration(string? configPath = null) =>
        BuildConfiguration(configPath);

    /// <summary>
    /// Save a <see cref="SharpbotConfig"/> to the user-level config file.
    /// Used by the <c>onboard</c> command to bootstrap the initial config.
    /// </summary>
    public static void SaveConfig(SharpbotConfig config, string? configPath = null)
    {
        var path = configPath ?? GetConfigPath();
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, WriteOptions);
        File.WriteAllText(path, json);
    }
}
