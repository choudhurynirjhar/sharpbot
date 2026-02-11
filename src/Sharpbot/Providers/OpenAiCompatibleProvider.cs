using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using Sharpbot.Telemetry;

namespace Sharpbot.Providers;

/// <summary>
/// LLM provider using the OpenAI .NET SDK for multi-provider support.
/// Supports OpenAI, Anthropic (via OpenRouter), and any OpenAI-compatible API.
/// </summary>
public sealed class OpenAiCompatibleProvider : ILlmProvider
{
    private readonly ChatClient _client;
    private readonly string _defaultModel;
    private readonly ProviderSpec? _gateway;
    private readonly ILogger? _logger;

    public OpenAiCompatibleProvider(
        string? apiKey = null,
        string? apiBase = null,
        string defaultModel = "anthropic/claude-opus-4-5",
        Dictionary<string, string>? extraHeaders = null,
        ILogger? logger = null)
    {
        _defaultModel = defaultModel;
        _logger = logger;
        _gateway = ProviderRegistry.FindGateway(apiKey, apiBase);

        var resolvedModel = ResolveModel(defaultModel);

        // Build the OpenAI client options
        var options = new OpenAIClientOptions();
        
        // Resolve API base: explicit > gateway default > matched provider default
        // Note: IConfiguration may turn JSON null into "" so treat empty same as null
        var effectiveBase = (string.IsNullOrEmpty(apiBase) ? null : apiBase)
            ?? _gateway?.DefaultApiBase
            ?? ProviderRegistry.FindByModel(defaultModel)?.DefaultApiBase;
        if (!string.IsNullOrEmpty(effectiveBase))
        {
            options.Endpoint = new Uri(effectiveBase);
        }

        var credential = new ApiKeyCredential(apiKey ?? "no-key");
        var openAiClient = new OpenAIClient(credential, options);
        _client = openAiClient.GetChatClient(resolvedModel);
    }

    public async Task<LlmResponse> ChatAsync(
        List<Dictionary<string, object?>> messages,
        List<Dictionary<string, object?>>? tools = null,
        string? model = null,
        int maxTokens = 4096,
        double temperature = 0.7,
        CancellationToken ct = default)
    {
        using var activity = SharpbotInstrumentation.ActivitySource.StartActivity("llm.http_request");
        try
        {
            var chatMessages = ConvertMessages(messages);
            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = maxTokens,
                Temperature = (float)temperature,
            };

            var toolCount = 0;
            if (tools != null)
            {
                foreach (var toolDef in tools)
                {
                    var chatTool = ConvertTool(toolDef);
                    if (chatTool != null)
                    {
                        options.Tools.Add(chatTool);
                        toolCount++;
                    }
                }
                options.ToolChoice = ChatToolChoice.CreateAutoChoice();
            }

            var resolvedModel = model ?? _defaultModel;
            activity?.SetTag("llm.model", resolvedModel);
            activity?.SetTag("llm.messages", messages.Count);
            activity?.SetTag("llm.tools", toolCount);

            _logger?.LogInformation(
                "┌─ LLM HTTP Request ─────────────────────────────────\n" +
                "│ Provider: {Endpoint}\n" +
                "│ Model: {Model}\n" +
                "│ Messages: {MsgCount} | Tools: {ToolCount}\n" +
                "│ MaxTokens: {MaxTokens} | Temp: {Temp}\n" +
                "└───────────────────────────────────────────────────────",
                _client.Pipeline?.ToString() ?? "(default)",
                resolvedModel, messages.Count, toolCount, maxTokens, temperature);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var completion = await _client.CompleteChatAsync(chatMessages, options, ct);
            sw.Stop();

            var response = ParseResponse(completion.Value);

            activity?.SetTag("llm.finish_reason", response.FinishReason);
            activity?.SetTag("llm.tokens.total", response.Usage.GetValueOrDefault("total_tokens"));
            activity?.SetTag("llm.tool_calls", response.ToolCalls.Count);
            activity?.SetTag("llm.duration_ms", sw.Elapsed.TotalMilliseconds);

            _logger?.LogInformation(
                "┌─ LLM HTTP Response ({Duration}) ──────────────────\n" +
                "│ Finish: {FinishReason}\n" +
                "│ Tokens: {Prompt} prompt → {Completion} completion ({Total} total)\n" +
                "│ Tool calls: {ToolCalls}\n" +
                "│ Content length: {ContentLen} chars\n" +
                "└───────────────────────────────────────────────────────",
                FormatDuration(sw.Elapsed),
                response.FinishReason,
                response.Usage.GetValueOrDefault("prompt_tokens"),
                response.Usage.GetValueOrDefault("completion_tokens"),
                response.Usage.GetValueOrDefault("total_tokens"),
                response.ToolCalls.Count,
                response.Content?.Length ?? 0);

            return response;
        }
        catch (Exception e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            _logger?.LogError(e, "Error calling LLM");
            return new LlmResponse
            {
                Content = $"Error calling LLM: {e.Message}",
                FinishReason = "error",
            };
        }
    }

    public string GetDefaultModel() => _defaultModel;

    private string ResolveModel(string model)
    {
        if (_gateway != null)
        {
            if (_gateway.StripModelPrefix)
                model = model.Contains('/') ? model.Split('/').Last() : model;

            var prefix = _gateway.LiteLlmPrefix;
            if (!string.IsNullOrEmpty(prefix) && !model.StartsWith($"{prefix}/"))
                model = $"{prefix}/{model}";
            return model;
        }

        var spec = ProviderRegistry.FindByModel(model);
        if (spec != null && !string.IsNullOrEmpty(spec.LiteLlmPrefix))
        {
            if (!spec.SkipPrefixes.Any(model.StartsWith))
                model = $"{spec.LiteLlmPrefix}/{model}";
        }

        return model;
    }

    private static List<ChatMessage> ConvertMessages(List<Dictionary<string, object?>> messages)
    {
        var result = new List<ChatMessage>();

        foreach (var msg in messages)
        {
            var role = msg.GetValueOrDefault("role")?.ToString() ?? MessageRoles.User;
            var content = msg.GetValueOrDefault("content")?.ToString() ?? "";

            switch (role)
            {
                case MessageRoles.System:
                    result.Add(ChatMessage.CreateSystemMessage(content));
                    break;
                case MessageRoles.User:
                    result.Add(ChatMessage.CreateUserMessage(content));
                    break;
                case MessageRoles.Assistant:
                    if (msg.TryGetValue("tool_calls", out var toolCallsObj) && toolCallsObj is List<Dictionary<string, object?>> toolCalls)
                    {
                        var parts = new List<ChatMessageContentPart>();
                        if (!string.IsNullOrEmpty(content))
                            parts.Add(ChatMessageContentPart.CreateTextPart(content));

                        var chatToolCalls = new List<ChatToolCall>();
                        foreach (var tc in toolCalls)
                        {
                            var tcId = tc.GetValueOrDefault("id")?.ToString() ?? "";
                            var fn = tc.GetValueOrDefault("function") as Dictionary<string, object?>;
                            var fnName = fn?.GetValueOrDefault("name")?.ToString() ?? "";
                            var fnArgs = fn?.GetValueOrDefault("arguments")?.ToString() ?? "{}";
                            chatToolCalls.Add(ChatToolCall.CreateFunctionToolCall(tcId, fnName, BinaryData.FromString(fnArgs)));
                        }
                        var assistantMsg = new AssistantChatMessage(chatToolCalls);
                        if (!string.IsNullOrEmpty(content))
                            assistantMsg.Content.Add(ChatMessageContentPart.CreateTextPart(content));
                        result.Add(assistantMsg);
                    }
                    else
                    {
                        result.Add(ChatMessage.CreateAssistantMessage(content));
                    }
                    break;
                case MessageRoles.Tool:
                    var toolCallId = msg.GetValueOrDefault("tool_call_id")?.ToString() ?? "";
                    result.Add(ChatMessage.CreateToolMessage(toolCallId, content));
                    break;
            }
        }

        return result;
    }

    private static ChatTool? ConvertTool(Dictionary<string, object?> toolDef)
    {
        if (toolDef.GetValueOrDefault("type")?.ToString() != "function")
            return null;

        var function = toolDef.GetValueOrDefault("function") as Dictionary<string, object?>;
        if (function == null) return null;

        var name = function.GetValueOrDefault("name")?.ToString() ?? "";
        var description = function.GetValueOrDefault("description")?.ToString() ?? "";
        var parameters = function.GetValueOrDefault("parameters");

        var parametersJson = parameters != null
            ? BinaryData.FromString(JsonSerializer.Serialize(parameters))
            : null;

        return ChatTool.CreateFunctionTool(name, description, parametersJson);
    }

    private static LlmResponse ParseResponse(ChatCompletion completion)
    {
        var toolCalls = new List<ToolCallRequest>();
        foreach (var tc in completion.ToolCalls)
        {
            Dictionary<string, object?> args;
            try
            {
                args = JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.FunctionArguments.ToString()) ?? [];
            }
            catch
            {
                args = new() { ["raw"] = tc.FunctionArguments.ToString() };
            }

            toolCalls.Add(new ToolCallRequest(tc.Id, tc.FunctionName, args));
        }

        var usage = new Dictionary<string, int>();
        if (completion.Usage != null)
        {
            usage["prompt_tokens"] = completion.Usage.InputTokenCount;
            usage["completion_tokens"] = completion.Usage.OutputTokenCount;
            usage["total_tokens"] = completion.Usage.TotalTokenCount;
        }

        // Get text content
        string? content = null;
        foreach (var part in completion.Content)
        {
            if (part.Kind == ChatMessageContentPartKind.Text)
            {
                content = (content ?? "") + part.Text;
            }
        }

        return new LlmResponse
        {
            Content = content,
            ToolCalls = toolCalls,
            FinishReason = completion.FinishReason.ToString() ?? "stop",
            Usage = usage,
        };
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalMinutes >= 1) return $"{ts.TotalMinutes:F1}m";
        if (ts.TotalSeconds >= 1) return $"{ts.TotalSeconds:F1}s";
        return $"{ts.TotalMilliseconds:F0}ms";
    }
}
