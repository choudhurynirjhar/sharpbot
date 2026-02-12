using Sharpbot.Config;
using Sharpbot.Cron;
using Sharpbot.Session;

namespace Sharpbot.Agent;

/// <summary>
/// Options for configuring <see cref="AgentLoop"/>.
/// Replaces the 11-parameter constructor, adhering to OCP —
/// new settings can be added without changing the constructor signature.
/// </summary>
public sealed record AgentLoopOptions
{
    /// <summary>Root workspace directory.</summary>
    public required string Workspace { get; init; }

    /// <summary>LLM model to use (defaults to provider's default model).</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tool-call iterations per message.</summary>
    public int MaxIterations { get; init; } = 20;

    /// <summary>Default max tokens for LLM responses.</summary>
    public int MaxTokens { get; init; } = 8192;

    /// <summary>Default sampling temperature.</summary>
    public double Temperature { get; init; } = 0.7;

    /// <summary>Per-model parameter overrides (temperature, max tokens).</summary>
    public Dictionary<string, ModelOverride> ModelOverrides { get; init; } = [];

    /// <summary>Maximum session messages to include in LLM context.</summary>
    public int MaxSessionMessages { get; init; } = 50;

    /// <summary>Brave Search API key (optional).</summary>
    public string? BraveApiKey { get; init; }

    /// <summary>Shell exec tool configuration.</summary>
    public ExecToolConfig ExecConfig { get; init; } = new();

    /// <summary>Cron service for scheduled tasks (optional).</summary>
    public CronService? CronService { get; init; }

    /// <summary>Restrict file/exec tools to the workspace directory.</summary>
    public bool RestrictToWorkspace { get; init; }

    /// <summary>Session manager (optional, defaults to a new instance).</summary>
    public SessionManager? SessionManager { get; init; }

    /// <summary>Skills configuration for per-skill env injection, gating, etc.</summary>
    public SkillsConfig? SkillsConfig { get; init; }

    /// <summary>Full app config for config-based gating checks.</summary>
    public SharpbotConfig? AppConfig { get; init; }

    /// <summary>
    /// Override the auto-detected context token limit for compaction.
    /// If null, the limit is inferred from the model name.
    /// Useful for testing compaction with a small value.
    /// </summary>
    public int? MaxContextTokens { get; init; }

    /// <summary>Callback invoked after each message with the completed telemetry. Used for usage tracking.</summary>
    public Action<AgentTelemetry>? OnTelemetry { get; init; }
}
