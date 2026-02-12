using Microsoft.Extensions.Logging;
using Sharpbot.Providers;

namespace Sharpbot.Agent;

/// <summary>
/// Handles context window management by estimating token usage and compacting
/// conversation history when approaching model context limits.
///
/// Strategy:
///   1. Estimate token count for the full message list.
///   2. If tokens exceed the compaction threshold (default 80% of context limit),
///      summarize the oldest messages while preserving recent context.
///   3. Replace summarized messages with a single condensed "summary" message.
/// </summary>
public sealed class ContextCompactor
{
    /// <summary>
    /// Known context window sizes (in tokens) for common model families.
    /// The lookup uses "contains" matching, so "gemini" matches "gemini-2.5-flash".
    /// </summary>
    private static readonly (string Pattern, int Limit)[] ModelContextLimits =
    [
        ("gemini-2.5",    1_048_576),
        ("gemini-2.0",    1_048_576),
        ("gemini-1.5",    1_048_576),
        ("gemini",          131_072),
        ("gpt-4.1",        1_047_576),
        ("gpt-4o",         128_000),
        ("gpt-4-turbo",    128_000),
        ("gpt-4",            8_192),
        ("gpt-3.5-turbo",   16_385),
        ("o3",              200_000),
        ("o4-mini",         200_000),
        ("claude-opus-4",   200_000),
        ("claude-sonnet-4", 200_000),
        ("claude-3",        200_000),
        ("claude",          200_000),
        ("deepseek",        128_000),
        ("qwen",            131_072),
        ("moonshot",        128_000),
        ("mistral",         128_000),
        ("llama",           128_000),
    ];

    /// <summary>Default context limit when the model is unknown.</summary>
    private const int DefaultContextLimit = 128_000;

    /// <summary>Fraction of context limit at which compaction triggers.</summary>
    private const double CompactionThreshold = 0.80;

    /// <summary>
    /// Target number of recent message pairs (user+assistant) to preserve verbatim.
    /// Actual preserve count is adaptive: reduced when conversation is small.
    /// </summary>
    private const int TargetPreserveRecentPairs = 6;

    /// <summary>Minimum messages from the conversation body we must summarize for compaction to be useful.</summary>
    private const int MinMessagesToSummarize = 2;

    /// <summary>Rough estimate: 1 token ≈ 4 characters for English text.</summary>
    private const double CharsPerToken = 4.0;

    private readonly ILlmProvider _provider;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly double _temperature;
    private readonly int? _contextLimitOverride;
    private readonly ILogger? _logger;

    public ContextCompactor(
        ILlmProvider provider,
        string model,
        int maxTokens = 4096,
        double temperature = 0.3,
        int? contextLimitOverride = null,
        ILogger? logger = null)
    {
        _provider = provider;
        _model = model;
        _maxTokens = maxTokens;
        _temperature = temperature;
        _contextLimitOverride = contextLimitOverride;
        _logger = logger;
    }

    /// <summary>Get the context window limit for a model.</summary>
    public static int GetContextLimit(string model)
    {
        foreach (var (pattern, limit) in ModelContextLimits)
        {
            if (model.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return limit;
        }
        return DefaultContextLimit;
    }

    /// <summary>
    /// Estimate the total token count of a message list.
    /// Uses a simple character-based heuristic (chars / 4).
    /// </summary>
    public static int EstimateTokens(List<Dictionary<string, object?>> messages)
    {
        long totalChars = 0;
        foreach (var msg in messages)
        {
            var content = msg.GetValueOrDefault("content")?.ToString();
            if (content != null)
                totalChars += content.Length;

            // Account for tool calls in assistant messages
            if (msg.GetValueOrDefault("tool_calls") is List<Dictionary<string, object?>> toolCalls)
            {
                foreach (var tc in toolCalls)
                {
                    if (tc.GetValueOrDefault("function") is Dictionary<string, object?> fn)
                    {
                        totalChars += fn.GetValueOrDefault("name")?.ToString()?.Length ?? 0;
                        totalChars += fn.GetValueOrDefault("arguments")?.ToString()?.Length ?? 0;
                    }
                }
            }

            // Per-message overhead (role, formatting tokens)
            totalChars += 16;
        }

        return (int)(totalChars / CharsPerToken);
    }

    /// <summary>
    /// Check whether the messages need compaction and, if so, compact them.
    /// Returns the (possibly compacted) message list and whether compaction occurred.
    /// </summary>
    public async Task<(List<Dictionary<string, object?>> Messages, bool WasCompacted)> CompactIfNeededAsync(
        List<Dictionary<string, object?>> messages,
        CancellationToken ct = default)
    {
        var estimatedTokens = EstimateTokens(messages);
        var contextLimit = _contextLimitOverride ?? GetContextLimit(_model);
        var threshold = (int)(contextLimit * CompactionThreshold);

        _logger?.LogDebug(
            "Context check: ~{Tokens} tokens estimated, limit={Limit}, threshold={Threshold}",
            estimatedTokens, contextLimit, threshold);

        if (estimatedTokens <= threshold)
            return (messages, false);

        _logger?.LogInformation(
            "⚡ Context compaction triggered: ~{Tokens} tokens exceeds {Threshold} threshold (limit: {Limit})",
            estimatedTokens, threshold, contextLimit);

        var compacted = await CompactMessagesAsync(messages, ct);
        var newTokens = EstimateTokens(compacted);

        _logger?.LogInformation(
            "✅ Context compacted: {Before} → {After} tokens ({Reduction}% reduction)",
            estimatedTokens, newTokens,
            estimatedTokens > 0 ? (int)((1.0 - (double)newTokens / estimatedTokens) * 100) : 0);

        return (compacted, true);
    }

    /// <summary>
    /// Compact the message list by summarizing older messages.
    /// Preserves: system prompt (index 0), recent messages, and tool call/result integrity.
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> CompactMessagesAsync(
        List<Dictionary<string, object?>> messages,
        CancellationToken ct)
    {
        if (messages.Count < 4) return messages; // nothing to compact

        // 1. Separate system prompt (always first), conversation body, and current user message (always last)
        var systemPrompt = messages[0]; // system message
        var currentUserMsg = messages[^1]; // latest user message
        var conversationBody = messages.GetRange(1, messages.Count - 2); // everything in between

        // 2. Calculate how many recent messages to preserve
        //    We preserve the last N pairs, but also avoid splitting tool call sequences
        var preserveCount = CalculatePreserveCount(conversationBody);

        if (preserveCount >= conversationBody.Count)
        {
            // Everything would be preserved — can't compact further
            _logger?.LogInformation("Context compaction: all messages are recent, nothing to summarize");
            return messages;
        }

        // 3. Split into "to summarize" and "to preserve"
        var toSummarize = conversationBody.GetRange(0, conversationBody.Count - preserveCount);
        var toPreserve = conversationBody.GetRange(conversationBody.Count - preserveCount, preserveCount);

        _logger?.LogInformation(
            "Compaction split: {Summarize} messages to summarize, {Preserve} messages to preserve",
            toSummarize.Count, toPreserve.Count);

        // 4. Build a summarization prompt and call the LLM
        var summary = await SummarizeMessagesAsync(toSummarize, ct);

        // 5. Reassemble: system + summary + preserved + current user message
        var result = new List<Dictionary<string, object?>> { systemPrompt };

        // Insert summary as a system message so the agent has context
        result.Add(new Dictionary<string, object?>
        {
            ["role"] = MessageRoles.User,
            ["content"] = "[Earlier conversation summary]\n" + summary,
        });
        result.Add(new Dictionary<string, object?>
        {
            ["role"] = MessageRoles.Assistant,
            ["content"] = "Understood. I have the context from our earlier conversation. How can I help?",
        });

        result.AddRange(toPreserve);
        result.Add(currentUserMsg);

        return result;
    }

    /// <summary>
    /// Calculate how many messages from the end of the conversation body to preserve.
    /// Adaptive: ensures at least <see cref="MinMessagesToSummarize"/> messages are available for summarization.
    /// Also ensures we don't split tool call / tool result sequences.
    /// </summary>
    private static int CalculatePreserveCount(List<Dictionary<string, object?>> body)
    {
        // Start by preserving the last N user+assistant pairs
        var targetMessages = TargetPreserveRecentPairs * 2;
        var preserveCount = Math.Min(targetMessages, body.Count);

        // Ensure we always have at least MinMessagesToSummarize to compact
        // (otherwise compaction is pointless)
        var maxPreserve = body.Count - MinMessagesToSummarize;
        if (maxPreserve < 0) maxPreserve = 0;
        preserveCount = Math.Min(preserveCount, maxPreserve);

        // Walk backwards from the split point to make sure we don't split a tool sequence
        var splitIdx = body.Count - preserveCount;
        while (splitIdx > 0 && splitIdx < body.Count)
        {
            var role = body[splitIdx].GetValueOrDefault("role")?.ToString();
            // If this is a tool result, we need to include the preceding assistant message with tool_calls
            if (role == MessageRoles.Tool)
            {
                splitIdx--;
                preserveCount++;
            }
            // If this is an assistant message with tool calls, include it and all following tool results
            else if (role == MessageRoles.Assistant && body[splitIdx].ContainsKey("tool_calls"))
            {
                splitIdx--;
                preserveCount++;
            }
            else
            {
                break;
            }
        }

        return Math.Min(preserveCount, body.Count);
    }

    /// <summary>
    /// Call the LLM to produce a concise summary of the given messages.
    /// </summary>
    private async Task<string> SummarizeMessagesAsync(
        List<Dictionary<string, object?>> messagesToSummarize,
        CancellationToken ct)
    {
        // Build a transcript of the messages for the summarizer
        var transcript = BuildTranscript(messagesToSummarize);

        var summarizeMessages = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["role"] = MessageRoles.System,
                ["content"] = """
                    You are a conversation summarizer. Your job is to produce a concise but comprehensive
                    summary of the conversation below. Preserve:
                    - Key decisions and conclusions
                    - Important facts, names, numbers, and code snippets mentioned
                    - The overall flow and topic of conversation
                    - Any pending tasks or action items
                    - Tool results and their outcomes (what was found/done)

                    Be concise but don't lose critical information. Write in third person narrative style.
                    Format the summary as a structured overview with bullet points.
                    """
            },
            new()
            {
                ["role"] = MessageRoles.User,
                ["content"] = $"Please summarize this conversation:\n\n{transcript}"
            }
        };

        try
        {
            var response = await _provider.ChatAsync(
                messages: summarizeMessages,
                tools: null,
                model: _model,
                maxTokens: _maxTokens,
                temperature: _temperature,
                ct: ct);

            return response.Content ?? "Previous conversation context (summary unavailable).";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to generate conversation summary, using fallback");
            return BuildFallbackSummary(messagesToSummarize);
        }
    }

    /// <summary>Build a human-readable transcript from messages for the summarizer.</summary>
    private static string BuildTranscript(List<Dictionary<string, object?>> messages)
    {
        var parts = new List<string>();

        foreach (var msg in messages)
        {
            var role = msg.GetValueOrDefault("role")?.ToString() ?? "unknown";
            var content = msg.GetValueOrDefault("content")?.ToString() ?? "";

            switch (role)
            {
                case MessageRoles.User:
                    parts.Add($"User: {Truncate(content, 2000)}");
                    break;
                case MessageRoles.Assistant:
                    var toolCalls = msg.GetValueOrDefault("tool_calls") as List<Dictionary<string, object?>>;
                    if (toolCalls is { Count: > 0 })
                    {
                        var toolNames = toolCalls.Select(tc =>
                        {
                            var fn = tc.GetValueOrDefault("function") as Dictionary<string, object?>;
                            return fn?.GetValueOrDefault("name")?.ToString() ?? "unknown";
                        });
                        parts.Add($"Assistant: [Called tools: {string.Join(", ", toolNames)}] {Truncate(content, 2000)}");
                    }
                    else
                    {
                        parts.Add($"Assistant: {Truncate(content, 2000)}");
                    }
                    break;
                case MessageRoles.Tool:
                    var toolName = msg.GetValueOrDefault("name")?.ToString() ?? "tool";
                    parts.Add($"Tool ({toolName}): {Truncate(content, 500)}");
                    break;
            }
        }

        // Cap the total transcript length to avoid the summarizer itself overflowing
        var transcript = string.Join("\n\n", parts);
        return transcript.Length > 50_000 ? transcript[..50_000] + "\n\n[... transcript truncated ...]" : transcript;
    }

    /// <summary>Fallback summary when the LLM call fails — just take first/last lines.</summary>
    private static string BuildFallbackSummary(List<Dictionary<string, object?>> messages)
    {
        var userMessages = messages
            .Where(m => m.GetValueOrDefault("role")?.ToString() == MessageRoles.User)
            .Select(m => m.GetValueOrDefault("content")?.ToString() ?? "")
            .Where(c => c.Length > 0)
            .ToList();

        if (userMessages.Count == 0)
            return "Previous conversation context (details unavailable).";

        var topics = userMessages.Select(m => Truncate(m, 100));
        return $"The user previously discussed the following topics:\n- {string.Join("\n- ", topics)}";
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
