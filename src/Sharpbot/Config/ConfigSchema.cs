using System.Text.Json.Serialization;

namespace Sharpbot.Config;

/// <summary>WhatsApp channel configuration.</summary>
public class WhatsAppConfig
{
    public bool Enabled { get; set; }
    public string BridgeUrl { get; set; } = "ws://localhost:3001";
    public List<string> AllowFrom { get; set; } = [];
}

/// <summary>Telegram channel configuration.</summary>
public class TelegramConfig
{
    public bool Enabled { get; set; }
    public string Token { get; set; } = "";
    public List<string> AllowFrom { get; set; } = [];
    public string? Proxy { get; set; }
}

/// <summary>Feishu/Lark channel configuration.</summary>
public class FeishuConfig
{
    public bool Enabled { get; set; }
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string EncryptKey { get; set; } = "";
    public string VerificationToken { get; set; } = "";
    public List<string> AllowFrom { get; set; } = [];
}

/// <summary>Discord channel configuration.</summary>
public class DiscordConfig
{
    public bool Enabled { get; set; }
    public string Token { get; set; } = "";
    public List<string> AllowFrom { get; set; } = [];
    public string GatewayUrl { get; set; } = "wss://gateway.discord.gg/?v=10&encoding=json";
    public int Intents { get; set; } = 37377;
}

/// <summary>Slack channel configuration.</summary>
public class SlackConfig
{
    public bool Enabled { get; set; }

    /// <summary>Bot token (xoxb-...) — required for both modes.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>App-level token (xapp-...) — required for Socket Mode.</summary>
    public string AppToken { get; set; } = "";

    /// <summary>Signing secret — required for HTTP Events API mode.</summary>
    public string SigningSecret { get; set; } = "";

    /// <summary>Connection mode: "socket" (default, no public URL needed) or "http" (Events API webhook).</summary>
    public string Mode { get; set; } = "socket";

    /// <summary>Webhook path for HTTP mode (default: /slack/events).</summary>
    public string WebhookPath { get; set; } = "/slack/events";

    /// <summary>Allowed sender IDs (empty = allow all).</summary>
    public List<string> AllowFrom { get; set; } = [];

    /// <summary>Maximum text chunk size for outbound messages (Slack limit ~4000).</summary>
    public int TextChunkLimit { get; set; } = 3900;

    /// <summary>Default channel to reply in threads ("off", "always"). "off" replies in-channel; "always" replies in thread.</summary>
    public string ReplyInThread { get; set; } = "always";
}

/// <summary>Configuration for chat channels.</summary>
public class ChannelsConfig
{
    public WhatsAppConfig WhatsApp { get; set; } = new();
    public TelegramConfig Telegram { get; set; } = new();
    public DiscordConfig Discord { get; set; } = new();
    public FeishuConfig Feishu { get; set; } = new();
    public SlackConfig Slack { get; set; } = new();
}

/// <summary>Per-model parameter overrides (temperature, max tokens, etc.).</summary>
public class ModelOverride
{
    /// <summary>Override temperature for this model (null = use default).</summary>
    public double? Temperature { get; set; }

    /// <summary>Override max tokens for this model (null = use default).</summary>
    public int? MaxTokens { get; set; }
}

/// <summary>Default agent configuration.</summary>
public class AgentDefaults
{
    public string Workspace { get; set; } = "data/workspace";
    public string Model { get; set; } = "anthropic/claude-opus-4-5";
    public int MaxTokens { get; set; } = 8192;
    public double Temperature { get; set; } = 0.7;
    public int MaxToolIterations { get; set; } = 20;

    /// <summary>Maximum number of session messages to include in LLM context (default: 50).</summary>
    public int MaxSessionMessages { get; set; } = 50;

    /// <summary>
    /// Override the auto-detected context token limit for compaction (null = auto-detect from model).
    /// Set to a small value (e.g. 2000) for testing compaction behavior.
    /// </summary>
    public int? MaxContextTokens { get; set; }

    /// <summary>Embedding model used for semantic memory (default: text-embedding-3-small).</summary>
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";

    /// <summary>Per-model parameter overrides keyed by model name or pattern.</summary>
    public Dictionary<string, ModelOverride> ModelOverrides { get; set; } = [];
}

/// <summary>Agent configuration.</summary>
public class AgentsConfig
{
    public AgentDefaults Defaults { get; set; } = new();
}

/// <summary>LLM provider configuration.</summary>
public class ProviderConfig
{
    public string ApiKey { get; set; } = "";
    public string? ApiBase { get; set; }
    public Dictionary<string, string>? ExtraHeaders { get; set; }
}

/// <summary>Configuration for LLM providers.</summary>
public class ProvidersConfig
{
    public ProviderConfig Anthropic { get; set; } = new();
    public ProviderConfig OpenAI { get; set; } = new();
    public ProviderConfig OpenRouter { get; set; } = new();
    public ProviderConfig DeepSeek { get; set; } = new();
    public ProviderConfig Groq { get; set; } = new();
    public ProviderConfig Zhipu { get; set; } = new();
    public ProviderConfig DashScope { get; set; } = new();
    public ProviderConfig Vllm { get; set; } = new();
    public ProviderConfig Gemini { get; set; } = new();
    public ProviderConfig Moonshot { get; set; } = new();
    public ProviderConfig AiHubMix { get; set; } = new();
}

/// <summary>Gateway/server configuration.</summary>
public class GatewayConfig
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 56789;
}

/// <summary>Web search tool configuration.</summary>
public class WebSearchConfig
{
    public string ApiKey { get; set; } = "";
    public int MaxResults { get; set; } = 5;
}

/// <summary>Web tools configuration.</summary>
public class WebToolsConfig
{
    public WebSearchConfig Search { get; set; } = new();
}

/// <summary>Shell exec tool configuration.</summary>
public class ExecToolConfig
{
    /// <summary>Foreground command timeout in seconds (default: 60).</summary>
    public int Timeout { get; set; } = 60;

    /// <summary>Default auto-background yield delay in milliseconds (default: 10000).
    /// If a foreground command hasn't finished after this delay, it is backgrounded.</summary>
    public int BackgroundYieldMs { get; set; } = 10_000;

    /// <summary>Maximum time a background process can run before being killed, in seconds (default: 1800 = 30 min).</summary>
    public int BackgroundTimeoutSec { get; set; } = 1800;

    /// <summary>Maximum output characters buffered per background session (default: 500,000).</summary>
    public int MaxOutputChars { get; set; } = 500_000;

    /// <summary>TTL for finished background sessions before auto-cleanup, in milliseconds (default: 1,800,000 = 30 min).</summary>
    public int SessionCleanupMs { get; set; } = 1_800_000;
}

/// <summary>Tools configuration.</summary>
public class ToolsConfig
{
    public WebToolsConfig Web { get; set; } = new();
    public ExecToolConfig Exec { get; set; } = new();
    public bool RestrictToWorkspace { get; set; }
}

/// <summary>Per-skill configuration entry.</summary>
public class SkillEntryConfig
{
    /// <summary>Whether the skill is enabled (default: true if not specified).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>API key convenience field. Injected as the skill's primaryEnv.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Environment variables to inject when this skill is active.</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Custom per-skill configuration bag.</summary>
    public Dictionary<string, string> Config { get; set; } = [];
}

/// <summary>Skills loader configuration.</summary>
public class SkillsLoadConfig
{
    /// <summary>Additional skill directories (lowest precedence).</summary>
    public List<string> ExtraDirs { get; set; } = [];
}

/// <summary>Skills configuration.</summary>
public class SkillsConfig
{
    /// <summary>Per-skill configuration entries (keyed by skill name).</summary>
    public Dictionary<string, SkillEntryConfig> Entries { get; set; } = [];

    /// <summary>Skills loading configuration.</summary>
    public SkillsLoadConfig Load { get; set; } = new();
}

/// <summary>Semantic memory configuration.</summary>
public class SemanticMemoryConfig
{
    /// <summary>Enable semantic memory (default: true).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Embedding model to use (default: text-embedding-3-small).</summary>
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";

    /// <summary>Automatically enrich the system prompt with relevant semantic memories.</summary>
    public bool AutoEnrich { get; set; } = true;

    /// <summary>Number of top-K results for auto-enrichment (default: 3).</summary>
    public int AutoEnrichTopK { get; set; } = 3;

    /// <summary>Minimum similarity score to include a result (default: 0.5).</summary>
    public float MinScore { get; set; } = 0.5f;

    /// <summary>Override API base URL for embeddings (e.g., https://openrouter.ai/api/v1). Auto-detected if empty.</summary>
    public string? EmbeddingApiBase { get; set; }

    /// <summary>Override API key for embeddings. Auto-detected if empty.</summary>
    public string? EmbeddingApiKey { get; set; }
}

/// <summary>Root configuration for Sharpbot.</summary>
public class SharpbotConfig
{
    public AgentsConfig Agents { get; set; } = new();
    public ChannelsConfig Channels { get; set; } = new();
    public ProvidersConfig Providers { get; set; } = new();
    public GatewayConfig Gateway { get; set; } = new();
    public ToolsConfig Tools { get; set; } = new();
    public SkillsConfig Skills { get; set; } = new();
    public SemanticMemoryConfig SemanticMemory { get; set; } = new();

    /// <summary>Get expanded workspace path (relative paths resolved against app directory).</summary>
    [JsonIgnore]
    public string WorkspacePath
    {
        get
        {
            var ws = Agents.Defaults.Workspace;
            // Resolve relative paths against the app base directory
            if (!Path.IsPathRooted(ws))
                ws = Path.Combine(AppContext.BaseDirectory, ws);
            // Expand ~/ to app data directory (not home directory)
            else if (ws.StartsWith("~/") || ws.StartsWith("~\\"))
                ws = Path.Combine(Utils.Helpers.GetDataPath(), ws[2..]);
            return ws;
        }
    }

    /// <summary>Get matched provider config. Falls back to first available.</summary>
    public ProviderConfig? GetProvider(string? model = null) =>
        Sharpbot.Providers.ProviderResolver.Resolve(Providers, model ?? Agents.Defaults.Model);

    /// <summary>Get API key for the given model.</summary>
    public string? GetApiKey(string? model = null) => GetProvider(model)?.ApiKey;

    /// <summary>Get API base URL for the given model.</summary>
    public string? GetApiBase(string? model = null) =>
        Sharpbot.Providers.ProviderResolver.GetApiBase(Providers, model ?? Agents.Defaults.Model);
}
