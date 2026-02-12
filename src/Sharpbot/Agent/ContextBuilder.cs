using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Sharpbot.Agent;

/// <summary>
/// Builds the context (system prompt + messages) for the agent.
/// Assembles bootstrap files, memory, skills, and conversation history
/// into a coherent prompt for the LLM.
/// </summary>
public sealed class ContextBuilder
{
    private static readonly string[] BootstrapFiles = ["AGENTS.md", "SOUL.md", "USER.md", "TOOLS.md", "IDENTITY.md"];

    private readonly string _workspace;
    private readonly MemoryStore _memory;
    private readonly SkillsLoader _skills;
    private readonly SemanticMemoryStore? _semanticMemory;
    private readonly bool _autoEnrich;
    private readonly int _autoEnrichTopK;
    private readonly float _autoEnrichMinScore;
    private readonly ILogger? _logger;

    public ContextBuilder(
        string workspace,
        MemoryStore? memory = null,
        SkillsLoader? skills = null,
        SemanticMemoryStore? semanticMemory = null,
        bool autoEnrich = true,
        int autoEnrichTopK = 3,
        float autoEnrichMinScore = 0.5f,
        ILogger? logger = null)
    {
        _workspace = workspace;
        _memory = memory ?? new MemoryStore(workspace);
        _skills = skills ?? new SkillsLoader(workspace);
        _semanticMemory = semanticMemory;
        _autoEnrich = autoEnrich;
        _autoEnrichTopK = autoEnrichTopK;
        _autoEnrichMinScore = autoEnrichMinScore;
        _logger = logger;
    }

    /// <summary>Build the system prompt from bootstrap files, memory, and skills.</summary>
    public string BuildSystemPrompt(List<string>? skillNames = null)
    {
        var parts = new List<string>();

        // Core identity
        parts.Add(GetIdentity());

        // Bootstrap files
        var bootstrap = LoadBootstrapFiles();
        if (!string.IsNullOrEmpty(bootstrap))
            parts.Add(bootstrap);

        // Memory context
        var memory = _memory.GetMemoryContext();
        if (!string.IsNullOrEmpty(memory))
            parts.Add($"# Memory\n\n{memory}");

        // Skills — progressive loading (FR-504):
        //   1. "always" skills → full content loaded into context
        //   2. Other available skills → summary only (name + description)
        //      The agent can call the `load_skill` tool to get full content on demand
        //   3. Unavailable skills → listed separately so the agent can help install deps
        var allSkills = _skills.ListAllSkills();
        _logger?.LogInformation("Skill discovery: found {Total} skill(s)", allSkills.Count);

        if (allSkills.Count > 0)
        {
            // Split into always-loaded vs on-demand
            var alwaysSkillNames = _skills.GetAlwaysSkills();
            var alwaysSet = new HashSet<string>(alwaysSkillNames, StringComparer.OrdinalIgnoreCase);

            var available = allSkills.Where(s => s.Available).ToList();
            var alwaysSkills = available.Where(s => alwaysSet.Contains(s.Name)).ToList();
            var onDemandSkills = available.Where(s => !alwaysSet.Contains(s.Name)).ToList();
            var unavailable = allSkills.Where(s => !s.Available).ToList();

            foreach (var skill in alwaysSkills)
                _logger?.LogInformation("  ✓ Skill always-loaded: {Name} (source: {Source})",
                    skill.Name, skill.Source);
            foreach (var skill in onDemandSkills)
                _logger?.LogInformation("  ○ Skill available (on-demand): {Name} (source: {Source})",
                    skill.Name, skill.Source);
            foreach (var skill in unavailable)
                _logger?.LogInformation("  ✗ Skill unavailable: {Name} — {Reason}",
                    skill.Name, skill.UnavailableReason ?? "unknown");

            // 1. Always-loaded skills — full content in context
            if (alwaysSkills.Count > 0)
            {
                _logger?.LogInformation("Always-loaded skills injected: [{Skills}]",
                    string.Join(", ", alwaysSkillNames));

                var alwaysContent = _skills.LoadSkillsForContext(alwaysSkillNames);
                if (!string.IsNullOrEmpty(alwaysContent))
                    parts.Add($"# Active Skills\n\n{alwaysContent}");
            }

            // 2. On-demand skills — summaries only
            if (onDemandSkills.Count > 0)
            {
                var summaryLines = new List<string>
                {
                    "# Available Skills",
                    "",
                    "The following skills are available but not loaded into context.",
                    "To use a skill, call the `load_skill` tool with the skill name to get its full instructions.",
                    "",
                    "<available_skills>",
                };
                foreach (var s in onDemandSkills)
                {
                    summaryLines.Add($"  <skill>");
                    summaryLines.Add($"    <name>{EscapeXml(s.Name)}</name>");
                    summaryLines.Add($"    <description>{EscapeXml(s.Description)}</description>");
                    summaryLines.Add($"  </skill>");
                }
                summaryLines.Add("</available_skills>");
                parts.Add(string.Join("\n", summaryLines));
            }

            // 3. Unavailable skills
            if (unavailable.Count > 0)
            {
                var lines = new List<string> { "<unavailable_skills>" };
                foreach (var s in unavailable)
                {
                    lines.Add($"  <skill>");
                    lines.Add($"    <name>{EscapeXml(s.Name)}</name>");
                    lines.Add($"    <description>{EscapeXml(s.Description)}</description>");
                    if (s.UnavailableReason != null)
                        lines.Add($"    <reason>{EscapeXml(s.UnavailableReason)}</reason>");
                    lines.Add($"  </skill>");
                }
                lines.Add("</unavailable_skills>");

                parts.Add($"""
                    # Unavailable Skills

                    The following skills are installed but have unmet requirements.
                    You can try to help the user install the missing dependencies.

                    {string.Join("\n", lines)}
                    """);
            }
        }

        return string.Join("\n\n---\n\n", parts);
    }

    private string GetIdentity()
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm (dddd)");
        var workspacePath = Path.GetFullPath(_workspace);
        var osName = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" :
                     RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";
        var runtime = $"{osName} {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}";

        var hasSemanticMemory = _semanticMemory != null;
        var memoryInstructions = hasSemanticMemory
            ? """
            ## Memory

            You have two memory systems:

            1. **Semantic memory** (primary) — Use the `memory_index` tool to store important facts,
               user preferences, or key information. Use `memory_search` to recall past information.
               Semantic memory is searchable by meaning and persists across sessions.

            2. **Pinned notes** — The file `memory/MEMORY.md` in your workspace contains a small set of
               always-visible facts (user identity, core preferences). Only edit this file for
               critical, always-needed information. For everything else, use semantic memory.

            When the user asks you to remember something, ALWAYS use `memory_index` (not file writes).
            When you need to recall something, ALWAYS try `memory_search` first.
            """
            : $"""
            ## Memory

            When remembering something, write to {workspacePath}/memory/MEMORY.md
            Daily notes can be written to {workspacePath}/memory/YYYY-MM-DD.md
            """;

        return $"""
            # sharpbot 🐈

            You are sharpbot, a helpful AI assistant. You have access to tools that allow you to:
            - Read, write, and edit files
            - Execute shell commands
            - Search the web and fetch web pages
            - Make HTTP API requests (GET, POST, PUT, PATCH, DELETE) with custom headers, body, and authentication
            - Send messages to users on chat channels
            - Spawn subagents for complex background tasks
            - Search and store information in semantic memory

            ## Current Time
            {now}

            ## Runtime
            {runtime}

            ## Workspace
            Your workspace is at: {workspacePath}
            - Pinned notes: {workspacePath}/memory/MEMORY.md
            - Custom skills: {workspacePath}/skills/<skill-name>/SKILL.md

            {memoryInstructions}

            ## Skills
            Skills are markdown files (SKILL.md) that teach you how to perform specific tasks like calling APIs.
            When you see a skill that describes an HTTP API call, use the `http_request` tool to execute it.
            Skills can define URL, method, headers, body, and authentication — follow their instructions precisely.

            IMPORTANT: When responding to direct questions or conversations, reply directly with your text response.
            Only use the 'message' tool when you need to send a message to a specific chat channel (like WhatsApp).
            For normal conversation, just respond with text - do not call the message tool.

            Always be helpful, accurate, and concise. When using tools, explain what you're doing.
            """;
    }

    private string LoadBootstrapFiles()
    {
        var parts = new List<string>();
        foreach (var filename in BootstrapFiles)
        {
            var filePath = Path.Combine(_workspace, filename);
            if (File.Exists(filePath))
            {
                var content = File.ReadAllText(filePath);
                parts.Add($"## {filename}\n\n{content}");
            }
        }
        return string.Join("\n\n", parts);
    }

    /// <summary>Build the complete message list for an LLM call (async for semantic memory enrichment).</summary>
    public async Task<List<Dictionary<string, object?>>> BuildMessagesAsync(
        List<Dictionary<string, object?>> history,
        string currentMessage,
        List<string>? skillNames = null,
        List<string>? media = null,
        string? channel = null,
        string? chatId = null,
        CancellationToken ct = default)
    {
        var messages = new List<Dictionary<string, object?>>();

        // System prompt
        var systemPrompt = BuildSystemPrompt(skillNames);

        // Semantic memory auto-enrichment
        if (_autoEnrich && _semanticMemory != null && !string.IsNullOrWhiteSpace(currentMessage))
        {
            try
            {
                var results = await _semanticMemory.SearchAsync(currentMessage, _autoEnrichTopK, _autoEnrichMinScore, ct);
                if (results.Count > 0)
                {
                    var memoryLines = results.Select(r =>
                        $"- [{r.Score:F2}] ({r.Source}) {r.Content}");
                    systemPrompt += $"\n\n---\n\n# Relevant Memories\n\n{string.Join("\n", memoryLines)}";
                    _logger?.LogInformation("Semantic memory enrichment: injected {Count} relevant memories into system prompt", results.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to enrich context with semantic memories");
            }
        }

        if (channel != null && chatId != null)
            systemPrompt += $"\n\n## Current Session\nChannel: {channel}\nChat ID: {chatId}";

        messages.Add(new() { ["role"] = MessageRoles.System, ["content"] = systemPrompt });

        // History
        messages.AddRange(history);

        // Current message
        messages.Add(new() { ["role"] = MessageRoles.User, ["content"] = currentMessage });

        return messages;
    }

    /// <summary>Add a tool result to the message list.</summary>
    public List<Dictionary<string, object?>> AddToolResult(
        List<Dictionary<string, object?>> messages,
        string toolCallId,
        string toolName,
        string result)
    {
        messages.Add(new()
        {
            ["role"] = MessageRoles.Tool,
            ["tool_call_id"] = toolCallId,
            ["name"] = toolName,
            ["content"] = result,
        });
        return messages;
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Add an assistant message to the message list.</summary>
    public List<Dictionary<string, object?>> AddAssistantMessage(
        List<Dictionary<string, object?>> messages,
        string? content,
        List<Dictionary<string, object?>>? toolCalls = null)
    {
        var msg = new Dictionary<string, object?>
        {
            ["role"] = MessageRoles.Assistant,
            ["content"] = content ?? "",
        };
        if (toolCalls != null)
            msg["tool_calls"] = toolCalls;

        messages.Add(msg);
        return messages;
    }
}
