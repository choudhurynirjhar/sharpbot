using System.Text.Json;
using Sharpbot.Config;

namespace Sharpbot.Api;

/// <summary>Config API — view and manage sharpbot configuration.</summary>
public static class ConfigApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void MapConfigApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/config").WithTags("Config");

        group.MapGet("/", GetConfig);
        group.MapPut("/", UpdateConfig);
        group.MapPost("/onboard", Onboard);
    }

    /// <summary>Get current config (API keys masked).</summary>
    private static IResult GetConfig(SharpbotConfig config)
    {
        return Results.Json(new
        {
            agents = new
            {
                defaults = new
                {
                    workspace = config.Agents.Defaults.Workspace,
                    model = config.Agents.Defaults.Model,
                    maxTokens = config.Agents.Defaults.MaxTokens,
                    temperature = config.Agents.Defaults.Temperature,
                    maxToolIterations = config.Agents.Defaults.MaxToolIterations,
                }
            },
            providers = GetMaskedProviders(config),
            channels = new
            {
                whatsApp = new { enabled = config.Channels.WhatsApp.Enabled },
                telegram = new { enabled = config.Channels.Telegram.Enabled, hasToken = !string.IsNullOrEmpty(config.Channels.Telegram.Token) },
                discord = new { enabled = config.Channels.Discord.Enabled, hasToken = !string.IsNullOrEmpty(config.Channels.Discord.Token) },
                feishu = new { enabled = config.Channels.Feishu.Enabled },
                slack = new {
                    enabled = config.Channels.Slack.Enabled,
                    hasBotToken = !string.IsNullOrEmpty(config.Channels.Slack.BotToken),
                    hasAppToken = !string.IsNullOrEmpty(config.Channels.Slack.AppToken),
                    mode = config.Channels.Slack.Mode,
                },
            },
            tools = new
            {
                web = new
                {
                    search = new
                    {
                        hasApiKey = !string.IsNullOrEmpty(config.Tools.Web.Search.ApiKey),
                        maxResults = config.Tools.Web.Search.MaxResults,
                    }
                },
                exec = new
                {
                    timeout = config.Tools.Exec.Timeout,
                    security = config.Tools.Exec.Security,
                    ask = config.Tools.Exec.Ask,
                    askFallback = config.Tools.Exec.AskFallback,
                    approvalTimeoutSec = config.Tools.Exec.ApprovalTimeoutSec,
                    safeBins = config.Tools.Exec.SafeBins,
                    allowlist = config.Tools.Exec.Allowlist,
                },
                media = new
                {
                    enabled = config.Tools.Media.Enabled,
                    allowedMimeTypes = config.Tools.Media.AllowedMimeTypes,
                    maxBytesPerItem = config.Tools.Media.MaxBytesPerItem,
                    maxItemsPerMessage = config.Tools.Media.MaxItemsPerMessage,
                    tempTtlMinutes = config.Tools.Media.TempTtlMinutes,
                    quarantineUnknownMime = config.Tools.Media.QuarantineUnknownMime,
                    rejectOverLimit = config.Tools.Media.RejectOverLimit,
                    downloadTimeoutSec = config.Tools.Media.DownloadTimeoutSec,
                    processingTimeoutSec = config.Tools.Media.ProcessingTimeoutSec,
                    enableOcr = config.Tools.Media.EnableOcr,
                    enableTranscription = config.Tools.Media.EnableTranscription,
                    ocrProvider = config.Tools.Media.OcrProvider,
                    ocrModel = config.Tools.Media.OcrModel,
                    ocrApiBase = config.Tools.Media.OcrApiBase,
                    transcriptionProvider = config.Tools.Media.TranscriptionProvider,
                    transcriptionModel = config.Tools.Media.TranscriptionModel,
                    transcriptionApiBase = config.Tools.Media.TranscriptionApiBase,
                    defaultLanguage = config.Tools.Media.DefaultLanguage,
                    auditEvents = config.Tools.Media.AuditEvents,
                },
                restrictToWorkspace = config.Tools.RestrictToWorkspace,
            },
            gateway = new { host = config.Gateway.Host, port = config.Gateway.Port },
        });
    }

    /// <summary>JSON property names that contain secrets and must never be persisted to disk.</summary>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApiKey", "Token", "BotToken", "AppToken", "SigningSecret",
        "AppSecret", "EncryptKey", "VerificationToken",
        "ExtraHeaders",  // Provider headers may contain auth tokens
    };

    /// <summary>Top-level section names whose nested "Env" dictionaries may contain secrets.</summary>
    private static readonly HashSet<string> SectionsWithEnvSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "Entries",  // Skills.Entries.*.Env
    };

    /// <summary>Update config — merges with existing user config and saves to disk.
    /// Secret fields are stripped before saving; secrets must be set via environment variables.</summary>
    private static async Task<IResult> UpdateConfig(HttpRequest request)
    {
        try
        {
            var body = await new StreamReader(request.Body).ReadToEndAsync();
            var updates = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

            if (updates is null)
                return Results.BadRequest(new { error = true, message = "Invalid JSON body." });

            // Strip secret fields from the update payload — secrets must come from env vars only
            StripSecrets(updates);

            var configPath = ConfigLoader.GetConfigPath();

            // Read existing config
            Dictionary<string, JsonElement> existing;
            if (File.Exists(configPath))
            {
                var existingJson = await File.ReadAllTextAsync(configPath);
                existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson)
                    ?? new Dictionary<string, JsonElement>();
            }
            else
            {
                existing = new Dictionary<string, JsonElement>();
            }

            // Deep merge: for each top-level key in updates, merge into existing
            foreach (var (key, value) in updates)
                existing[key] = value;

            // Save merged config
            var dir = Path.GetDirectoryName(configPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            var mergedJson = JsonSerializer.Serialize(existing, JsonOptions);
            await File.WriteAllTextAsync(configPath, mergedJson);

            return Results.Json(new
            {
                success = true,
                message = "Non-secret configuration saved. Secrets must be set via SHARPBOT_ environment variables. Restart the server to apply changes.",
                configPath,
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = true, message = ex.Message }, statusCode: 500);
        }
    }

    /// <summary>Recursively remove any properties whose name matches a known secret key.
    /// Also strips "Env" dictionaries inside skill entries (Skills.Entries.*.Env).</summary>
    private static void StripSecrets(Dictionary<string, JsonElement> dict, bool insideEntries = false)
    {
        var keysToRemove = new List<string>();
        var keysToReplace = new List<(string Key, JsonElement Value)>();

        foreach (var (key, value) in dict)
        {
            if (SecretKeys.Contains(key))
            {
                keysToRemove.Add(key);
            }
            else if (insideEntries && key.Equals("Env", StringComparison.OrdinalIgnoreCase))
            {
                // Strip Env dictionaries inside skill entries — they may contain secrets
                keysToRemove.Add(key);
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                // Recurse into nested objects; flag when entering an "Entries" section
                var nested = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value.GetRawText());
                if (nested is not null)
                {
                    var isEntries = SectionsWithEnvSecrets.Contains(key);
                    StripSecrets(nested, insideEntries || isEntries);
                    keysToReplace.Add((key, JsonSerializer.SerializeToElement(nested)));
                }
            }
        }

        foreach (var k in keysToRemove) dict.Remove(k);
        foreach (var (k, v) in keysToReplace) dict[k] = v;
    }

    /// <summary>Initialize sharpbot config and workspace (onboard).</summary>
    private static IResult Onboard()
    {
        var configPath = ConfigLoader.GetConfigPath();
        var alreadyExists = File.Exists(configPath);

        if (!alreadyExists)
        {
            var configDir = Path.GetDirectoryName(configPath);
            if (configDir is not null) Directory.CreateDirectory(configDir);

            var minimalConfig = """
                {
                  "Agents": {
                    "Defaults": {
                      "Model": "gemini-2.5-flash"
                    }
                  }
                }
                """;

            File.WriteAllText(configPath, minimalConfig);
        }

        var workspace = Utils.Helpers.GetWorkspacePath();

        return Results.Json(new
        {
            success = true,
            alreadyExisted = alreadyExists,
            configPath,
            workspace,
            message = alreadyExists
                ? "Configuration already exists."
                : "Configuration and workspace created successfully.",
        });
    }

    private static object GetMaskedProviders(SharpbotConfig config)
    {
        return new
        {
            anthropic = MaskProvider(config.Providers.Anthropic),
            openai = MaskProvider(config.Providers.OpenAI),
            openrouter = MaskProvider(config.Providers.OpenRouter),
            deepseek = MaskProvider(config.Providers.DeepSeek),
            groq = MaskProvider(config.Providers.Groq),
            gemini = MaskProvider(config.Providers.Gemini),
            zhipu = MaskProvider(config.Providers.Zhipu),
            dashscope = MaskProvider(config.Providers.DashScope),
            vllm = MaskProvider(config.Providers.Vllm),
            moonshot = MaskProvider(config.Providers.Moonshot),
            aihubmix = MaskProvider(config.Providers.AiHubMix),
        };
    }

    private static object MaskProvider(ProviderConfig p) => new
    {
        hasApiKey = !string.IsNullOrEmpty(p.ApiKey),
        maskedKey = MaskKey(p.ApiKey),
        apiBase = p.ApiBase,
    };

    private static string? MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.Length <= 8) return "****";
        return key[..4] + "****" + key[^4..];
    }
}
