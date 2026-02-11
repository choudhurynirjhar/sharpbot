using Sharpbot.Config;

namespace Sharpbot.Providers;

/// <summary>
/// Resolves the active LLM provider configuration from the config.
/// Extracted from <see cref="SharpbotConfig"/> to honour the Single Responsibility Principle —
/// configuration classes should be POCOs; resolution logic belongs in a dedicated service.
/// Also eliminates the duplicate <c>GetProviderByName</c> that previously lived in both
/// <see cref="SharpbotConfig"/> and <c>SharpbotServiceFactory</c>.
/// </summary>
public static class ProviderResolver
{
    /// <summary>Look up a <see cref="ProviderConfig"/> by its registry name (case-insensitive).</summary>
    public static ProviderConfig? GetByName(ProvidersConfig providers, string name) => name.ToLowerInvariant() switch
    {
        "anthropic" => providers.Anthropic,
        "openai" => providers.OpenAI,
        "openrouter" => providers.OpenRouter,
        "deepseek" => providers.DeepSeek,
        "groq" => providers.Groq,
        "zhipu" => providers.Zhipu,
        "dashscope" => providers.DashScope,
        "vllm" => providers.Vllm,
        "gemini" => providers.Gemini,
        "moonshot" => providers.Moonshot,
        "aihubmix" => providers.AiHubMix,
        _ => null,
    };

    /// <summary>Get matched provider config for the given model. Falls back to first with a key.</summary>
    public static ProviderConfig? Resolve(ProvidersConfig providers, string model)
    {
        var modelLower = model.ToLowerInvariant();

        foreach (var spec in ProviderRegistry.Providers)
        {
            var p = GetByName(providers, spec.Name);
            if (p != null && spec.Keywords.Any(kw => modelLower.Contains(kw)) && !string.IsNullOrEmpty(p.ApiKey))
                return p;
        }

        // Fallback: first with key
        foreach (var spec in ProviderRegistry.Providers)
        {
            var p = GetByName(providers, spec.Name);
            if (p != null && !string.IsNullOrEmpty(p.ApiKey))
                return p;
        }

        return null;
    }

    /// <summary>Get API key for the given model.</summary>
    public static string? GetApiKey(ProvidersConfig providers, string model) =>
        Resolve(providers, model)?.ApiKey;

    /// <summary>Get API base URL for the given model.</summary>
    public static string? GetApiBase(ProvidersConfig providers, string model)
    {
        var p = Resolve(providers, model);
        if (!string.IsNullOrEmpty(p?.ApiBase)) return p!.ApiBase;

        // Check all providers (gateways and standard) for a DefaultApiBase
        foreach (var spec in ProviderRegistry.Providers)
        {
            if (!string.IsNullOrEmpty(spec.DefaultApiBase) && p == GetByName(providers, spec.Name))
                return spec.DefaultApiBase;
        }

        return null;
    }
}
