namespace Sharpbot.Providers;

/// <summary>
/// Metadata about a single LLM provider.
/// </summary>
public record ProviderSpec
{
    public required string Name { get; init; }
    public required string[] Keywords { get; init; }
    public required string EnvKey { get; init; }
    public string DisplayName { get; init; } = "";
    public string LiteLlmPrefix { get; init; } = "";
    public string[] SkipPrefixes { get; init; } = [];
    public bool IsGateway { get; init; }
    public bool IsLocal { get; init; }
    public string DetectByKeyPrefix { get; init; } = "";
    public string DetectByBaseKeyword { get; init; } = "";
    public string DefaultApiBase { get; init; } = "";
    public bool StripModelPrefix { get; init; }

    public string Label => string.IsNullOrEmpty(DisplayName) ? Name[..1].ToString().ToUpper() + Name[1..] : DisplayName;
}

/// <summary>
/// Provider Registry — single source of truth for LLM provider metadata.
/// Order matters — it controls match priority and fallback.
/// </summary>
public static class ProviderRegistry
{
    public static readonly ProviderSpec[] Providers =
    [
        // === Gateways ===
        new ProviderSpec
        {
            Name = "openrouter",
            Keywords = ["openrouter"],
            EnvKey = "OPENROUTER_API_KEY",
            DisplayName = "OpenRouter",
            LiteLlmPrefix = "openrouter",
            IsGateway = true,
            DetectByKeyPrefix = "sk-or-",
            DetectByBaseKeyword = "openrouter",
            DefaultApiBase = "https://openrouter.ai/api/v1",
        },
        new ProviderSpec
        {
            Name = "aihubmix",
            Keywords = ["aihubmix"],
            EnvKey = "OPENAI_API_KEY",
            DisplayName = "AiHubMix",
            LiteLlmPrefix = "openai",
            IsGateway = true,
            DetectByBaseKeyword = "aihubmix",
            DefaultApiBase = "https://aihubmix.com/v1",
            StripModelPrefix = true,
        },

        // === Standard providers ===
        new ProviderSpec
        {
            Name = "anthropic",
            Keywords = ["anthropic", "claude"],
            EnvKey = "ANTHROPIC_API_KEY",
            DisplayName = "Anthropic",
        },
        new ProviderSpec
        {
            Name = "openai",
            Keywords = ["openai", "gpt"],
            EnvKey = "OPENAI_API_KEY",
            DisplayName = "OpenAI",
        },
        new ProviderSpec
        {
            Name = "deepseek",
            Keywords = ["deepseek"],
            EnvKey = "DEEPSEEK_API_KEY",
            DisplayName = "DeepSeek",
            LiteLlmPrefix = "deepseek",
            SkipPrefixes = ["deepseek/"],
        },
        new ProviderSpec
        {
            Name = "gemini",
            Keywords = ["gemini"],
            EnvKey = "GEMINI_API_KEY",
            DisplayName = "Gemini",
            DefaultApiBase = "https://generativelanguage.googleapis.com/v1beta/openai/",
        },
        new ProviderSpec
        {
            Name = "zhipu",
            Keywords = ["zhipu", "glm", "zai"],
            EnvKey = "ZAI_API_KEY",
            DisplayName = "Zhipu AI",
            LiteLlmPrefix = "zai",
            SkipPrefixes = ["zhipu/", "zai/", "openrouter/", "hosted_vllm/"],
        },
        new ProviderSpec
        {
            Name = "dashscope",
            Keywords = ["qwen", "dashscope"],
            EnvKey = "DASHSCOPE_API_KEY",
            DisplayName = "DashScope",
            LiteLlmPrefix = "dashscope",
            SkipPrefixes = ["dashscope/", "openrouter/"],
        },
        new ProviderSpec
        {
            Name = "moonshot",
            Keywords = ["moonshot", "kimi"],
            EnvKey = "MOONSHOT_API_KEY",
            DisplayName = "Moonshot",
            LiteLlmPrefix = "moonshot",
            SkipPrefixes = ["moonshot/", "openrouter/"],
            DefaultApiBase = "https://api.moonshot.ai/v1",
        },

        // === Local deployment ===
        new ProviderSpec
        {
            Name = "vllm",
            Keywords = ["vllm"],
            EnvKey = "HOSTED_VLLM_API_KEY",
            DisplayName = "vLLM/Local",
            LiteLlmPrefix = "hosted_vllm",
            IsLocal = true,
        },

        // === Auxiliary ===
        new ProviderSpec
        {
            Name = "groq",
            Keywords = ["groq"],
            EnvKey = "GROQ_API_KEY",
            DisplayName = "Groq",
            LiteLlmPrefix = "groq",
            SkipPrefixes = ["groq/"],
        },
    ];

    /// <summary>Match a standard provider by model-name keyword (case-insensitive).</summary>
    public static ProviderSpec? FindByModel(string model)
    {
        var modelLower = model.ToLowerInvariant();
        return Providers.FirstOrDefault(spec =>
            !spec.IsGateway && !spec.IsLocal &&
            spec.Keywords.Any(kw => modelLower.Contains(kw)));
    }

    /// <summary>Detect gateway/local by api_key prefix or api_base substring.</summary>
    public static ProviderSpec? FindGateway(string? apiKey, string? apiBase)
    {
        foreach (var spec in Providers)
        {
            if (!string.IsNullOrEmpty(spec.DetectByKeyPrefix) && apiKey?.StartsWith(spec.DetectByKeyPrefix) == true)
                return spec;
            if (!string.IsNullOrEmpty(spec.DetectByBaseKeyword) && apiBase?.Contains(spec.DetectByBaseKeyword) == true)
                return spec;
        }

        if (!string.IsNullOrEmpty(apiBase))
        {
            // Only assume local if the apiBase doesn't belong to a known standard provider
            var isKnownProvider = Providers.Any(s =>
                !s.IsLocal && !s.IsGateway &&
                !string.IsNullOrEmpty(s.DefaultApiBase) &&
                s.DefaultApiBase == apiBase);
            if (!isKnownProvider)
                return Providers.FirstOrDefault(s => s.IsLocal);
        }

        return null;
    }

    /// <summary>Find a provider spec by config field name.</summary>
    public static ProviderSpec? FindByName(string name) =>
        Providers.FirstOrDefault(spec => spec.Name == name);
}
