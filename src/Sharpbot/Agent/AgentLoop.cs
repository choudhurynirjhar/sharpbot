using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sharpbot.Agent.Browser;
using Sharpbot.Agent.Tools;
using Sharpbot.Bus;
using Sharpbot.Channels;
using Sharpbot.Config;
using Sharpbot.Cron;
using Sharpbot.Providers;
using Sharpbot.Session;
using Sharpbot.Telemetry;

namespace Sharpbot.Agent;

/// <summary>
/// The agent loop is the core processing engine.
/// It:
/// 1. Receives messages from the bus
/// 2. Builds context with history, memory, skills
/// 3. Calls the LLM
/// 4. Executes tool calls
/// 5. Sends responses back
/// </summary>
public sealed class AgentLoop : IDisposable
{
    private readonly MessageBus _bus;
    private readonly ILlmProvider _provider;
    private readonly string _workspace;
    private readonly string _model;
    private readonly int _maxIterations;
    private readonly int _maxTokens;
    private readonly double _temperature;
    private readonly Dictionary<string, ModelOverride> _modelOverrides;
    private readonly int _maxSessionMessages;
    private readonly string? _braveApiKey;
    private readonly ExecToolConfig _execConfig;
    private readonly CronService? _cronService;
    private readonly bool _restrictToWorkspace;
    private readonly ContextBuilder _context;
    private readonly SkillsLoader _skills;
    private readonly SessionManager _sessions;
    private readonly ToolRegistry _tools;
    private readonly SubagentManager _subagents;
    private readonly BrowserManager _browserManager;
    private readonly ContextCompactor _compactor;
    private readonly ProcessSessionManager _processManager;
    private readonly SemanticMemoryStore? _semanticMemory;
    private readonly Action<AgentTelemetry>? _onTelemetry;
    private readonly ILogger? _logger;
    private volatile bool _running;

    public AgentLoop(
        MessageBus bus,
        ILlmProvider provider,
        AgentLoopOptions options,
        ILogger? logger = null)
    {
        _bus = bus;
        _provider = provider;
        _workspace = options.Workspace;
        _model = options.Model ?? provider.GetDefaultModel();
        _maxIterations = options.MaxIterations;
        _maxTokens = options.MaxTokens;
        _temperature = options.Temperature;
        _modelOverrides = options.ModelOverrides;
        _maxSessionMessages = options.MaxSessionMessages;
        _braveApiKey = options.BraveApiKey;
        _execConfig = options.ExecConfig;
        _cronService = options.CronService;
        _restrictToWorkspace = options.RestrictToWorkspace;
        _onTelemetry = options.OnTelemetry;
        _logger = logger;

        // Built-in skills are in the "skills" directory next to the executable
        var builtinSkillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
        _skills = new SkillsLoader(_workspace,
            builtinSkillsDir: Directory.Exists(builtinSkillsDir) ? builtinSkillsDir : null,
            skillsConfig: options.SkillsConfig,
            appConfig: options.AppConfig);
        _context = new ContextBuilder(
            _workspace,
            skills: _skills,
            semanticMemory: options.SemanticMemory,
            autoEnrich: options.SemanticMemoryAutoEnrich,
            autoEnrichTopK: options.SemanticMemoryAutoEnrichTopK,
            autoEnrichMinScore: options.SemanticMemoryAutoEnrichMinScore,
            logger: logger);
        _sessions = options.SessionManager ?? new SessionManager(Database.SharpbotDb.CreateDefault());
        _tools = new ToolRegistry(logger);
        _subagents = new SubagentManager(
            provider: provider,
            workspace: _workspace,
            bus: bus,
            model: _model,
            braveApiKey: _braveApiKey,
            execConfig: _execConfig,
            restrictToWorkspace: _restrictToWorkspace,
            logger: logger);
        _browserManager = new BrowserManager(headless: true, logger: logger);
        _compactor = new ContextCompactor(provider, _model, maxTokens: _maxTokens, contextLimitOverride: options.MaxContextTokens, logger: logger);
        _semanticMemory = options.SemanticMemory;
        _processManager = new ProcessSessionManager(
            maxOutputChars: _execConfig.MaxOutputChars,
            backgroundTimeoutSec: _execConfig.BackgroundTimeoutSec,
            sessionCleanupMs: _execConfig.SessionCleanupMs,
            defaultWorkDir: _workspace,
            logger: logger);

        RegisterDefaultTools();
    }

    private void RegisterDefaultTools()
    {
        var allowedDir = _restrictToWorkspace ? _workspace : null;
        _tools.Register(new ReadFileTool(allowedDir));
        _tools.Register(new WriteFileTool(allowedDir));
        _tools.Register(new EditFileTool(allowedDir));
        _tools.Register(new ListDirTool(allowedDir));

        _tools.Register(new ExecTool(
            timeout: _execConfig.Timeout,
            defaultYieldMs: _execConfig.BackgroundYieldMs,
            workingDir: _workspace,
            restrictToWorkspace: _restrictToWorkspace,
            processManager: _processManager));
        _tools.Register(new ProcessTool(_processManager));

        _tools.Register(new WebSearchTool(_braveApiKey));
        _tools.Register(new WebFetchTool());
        _tools.Register(new HttpRequestTool());

        var messageTool = new MessageTool(msg => _bus.PublishOutboundAsync(msg).AsTask());
        _tools.Register(messageTool);

        var spawnTool = new SpawnTool(_subagents);
        _tools.Register(spawnTool);

        if (_cronService != null)
            _tools.Register(new CronTool(_cronService));

        // Progressive skill loading — lets the agent request full skill content on demand
        _tools.Register(new LoadSkillTool(_skills));

        // Browser automation tools (Playwright)
        _tools.Register(new BrowserNavigateTool(_browserManager));
        _tools.Register(new BrowserSnapshotTool(_browserManager));
        _tools.Register(new BrowserScreenshotTool(_browserManager, _workspace));
        _tools.Register(new BrowserClickTool(_browserManager));
        _tools.Register(new BrowserTypeTool(_browserManager));
        _tools.Register(new BrowserSelectTool(_browserManager));
        _tools.Register(new BrowserPressKeyTool(_browserManager));
        _tools.Register(new BrowserEvaluateTool(_browserManager));
        _tools.Register(new BrowserWaitTool(_browserManager));
        _tools.Register(new BrowserTabsTool(_browserManager));
        _tools.Register(new BrowserBackTool(_browserManager));

        // Semantic memory tools (search and index)
        if (_semanticMemory != null)
        {
            _tools.Register(new MemorySearchTool(_semanticMemory));
            _tools.Register(new MemoryIndexTool(_semanticMemory));
        }
    }

    /// <summary>The skills loader for accessing skill metadata.</summary>
    public SkillsLoader Skills => _skills;

    /// <summary>Get the list of registered tool names.</summary>
    public List<string> ToolNames => _tools.ToolNames;

    /// <summary>Get the count of registered tools.</summary>
    public int ToolCount => _tools.Count;

    /// <summary>Run the agent loop, processing messages from the bus.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _running = true;
        _logger?.LogInformation("Agent loop started");

        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                var msg = await _bus.TryConsumeInboundAsync(TimeSpan.FromSeconds(1), ct);
                if (msg == null) continue;

                try
                {
                    var response = await ProcessMessageAsync(msg);
                    if (response != null)
                        await _bus.PublishOutboundAsync(response);
                }
                catch (Exception e)
                {
                    _logger?.LogError(e, "Error processing message");
                    await _bus.PublishOutboundAsync(new OutboundMessage
                    {
                        Channel = msg.Channel,
                        ChatId = msg.ChatId,
                        Content = $"Sorry, I encountered an error: {e.Message}",
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Stop the agent loop.</summary>
    public void Stop()
    {
        _running = false;
        _logger?.LogInformation("Agent loop stopping");
    }

    private Task<(OutboundMessage? Message, AgentTelemetry Telemetry)> ProcessMessageWithTelemetryAsync(InboundMessage msg)
        => ProcessMessageInternalAsync(msg);

    private async Task<OutboundMessage?> ProcessMessageAsync(InboundMessage msg)
    {
        var (message, _) = await ProcessMessageInternalAsync(msg);
        return message;
    }

    private async Task<(OutboundMessage? Message, AgentTelemetry Telemetry)> ProcessMessageInternalAsync(InboundMessage msg)
    {
        // Handle system messages (subagent announces)
        if (msg.Channel == Channels.WellKnown.System)
        {
            var sysResult = await ProcessSystemMessageAsync(msg);
            return (sysResult, new AgentTelemetry { Channel = msg.Channel, SenderId = msg.SenderId, SessionKey = msg.SessionKey, Model = _model });
        }

        var preview = msg.Content.Length > 80 ? msg.Content[..80] + "..." : msg.Content;
        _logger?.LogInformation("Processing message from {Channel}:{SenderId}: {Preview}", msg.Channel, msg.SenderId, preview);

        // Inject per-skill environment variables; restore on completion
        var restoreEnv = _skills.InjectSkillEnvironment();
        try
        {
            return await ProcessMessageCoreAsync(msg, preview);
        }
        finally
        {
            restoreEnv();
        }
    }

    private async Task<(OutboundMessage?, AgentTelemetry)> ProcessMessageCoreAsync(InboundMessage msg, string preview)
    {
        using var activity = SharpbotInstrumentation.ActivitySource.StartActivity("agent.process_message");
        activity?.SetTag("sharpbot.channel", msg.Channel);
        activity?.SetTag("sharpbot.sender", msg.SenderId);
        activity?.SetTag("sharpbot.session", msg.SessionKey);
        activity?.SetTag("sharpbot.model", _model);

        var telemetry = new AgentTelemetry
        {
            Channel = msg.Channel,
            SenderId = msg.SenderId,
            SessionKey = msg.SessionKey,
            Model = _model,
        };

        try
        {
            var session = _sessions.GetOrCreate(msg.SessionKey);

            // Update tool contexts
            SetToolContexts(msg.Channel, msg.ChatId);

            // Build initial messages
            var messages = await _context.BuildMessagesAsync(
                history: session.GetHistory(_maxSessionMessages),
                currentMessage: msg.Content,
                channel: msg.Channel,
                chatId: msg.ChatId);

            // Run the iterative LLM loop (shared helper)
            var finalContent = await RunIterativeLoopAsync(messages, _tools, telemetry: telemetry);
            finalContent ??= "I've completed processing but have no response to give.";

            var responsePreview = finalContent.Length > 120 ? finalContent[..120] + "..." : finalContent;
            _logger?.LogInformation("Response to {Channel}:{SenderId}: {Preview}", msg.Channel, msg.SenderId, responsePreview);

            // Save to session
            session.AddMessage(MessageRoles.User, msg.Content);
            session.AddMessage(MessageRoles.Assistant, finalContent);
            _sessions.Save(session);

            telemetry.Complete();
            _logger?.LogInformation("Agent telemetry:\n{Telemetry}", telemetry.ToLogString());
            SafeInvokeTelemetry(telemetry);

            // Record OTel metrics
            SharpbotInstrumentation.RequestsTotal.Add(1,
                new KeyValuePair<string, object?>("channel", msg.Channel));
            SharpbotInstrumentation.RequestsSuccess.Add(1,
                new KeyValuePair<string, object?>("channel", msg.Channel));
            SharpbotInstrumentation.RequestDuration.Record(telemetry.TotalDuration.TotalMilliseconds,
                new KeyValuePair<string, object?>("channel", msg.Channel));

            activity?.SetTag("sharpbot.iterations", telemetry.Iterations);
            activity?.SetTag("sharpbot.tokens.total", telemetry.TotalTokens);
            activity?.SetTag("sharpbot.tool_calls.total", telemetry.TotalToolCalls);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return (new OutboundMessage
            {
                Channel = msg.Channel,
                ChatId = msg.ChatId,
                Content = finalContent,
            }, telemetry);
        }
        catch (Exception ex)
        {
            telemetry.Fail(ex.Message);
            _logger?.LogWarning("Agent telemetry (failed):\n{Telemetry}", telemetry.ToLogString());
            SafeInvokeTelemetry(telemetry);

            // Record OTel metrics for failure
            SharpbotInstrumentation.RequestsTotal.Add(1,
                new KeyValuePair<string, object?>("channel", msg.Channel));
            SharpbotInstrumentation.RequestsFailed.Add(1,
                new KeyValuePair<string, object?>("channel", msg.Channel));

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private async Task<OutboundMessage?> ProcessSystemMessageAsync(InboundMessage msg)
    {
        _logger?.LogInformation("Processing system message from {SenderId}", msg.SenderId);

        string originChannel, originChatId;
        var colonIdx = msg.ChatId.IndexOf(':');
        if (colonIdx >= 0)
        {
            originChannel = msg.ChatId[..colonIdx];
            originChatId = msg.ChatId[(colonIdx + 1)..];
        }
        else
        {
            originChannel = Channels.WellKnown.Cli;
            originChatId = msg.ChatId;
        }

        var sessionKey = $"{originChannel}:{originChatId}";

        var telemetry = new AgentTelemetry
        {
            Channel = originChannel,
            SenderId = msg.SenderId,
            SessionKey = sessionKey,
            Model = _model,
        };

        try
        {
            var session = _sessions.GetOrCreate(sessionKey);
            SetToolContexts(originChannel, originChatId);

            var messages = await _context.BuildMessagesAsync(
                history: session.GetHistory(_maxSessionMessages),
                currentMessage: msg.Content,
                channel: originChannel,
                chatId: originChatId);

            var finalContent = await RunIterativeLoopAsync(messages, _tools, telemetry: telemetry);
            finalContent ??= "Background task completed.";

            session.AddMessage(MessageRoles.User, $"[System: {msg.SenderId}] {msg.Content}");
            session.AddMessage(MessageRoles.Assistant, finalContent);
            _sessions.Save(session);

            telemetry.Complete();
            _logger?.LogInformation("Agent telemetry (system):\n{Telemetry}", telemetry.ToLogString());
            SafeInvokeTelemetry(telemetry);

            return new OutboundMessage
            {
                Channel = originChannel,
                ChatId = originChatId,
                Content = finalContent,
            };
        }
        catch (Exception ex)
        {
            telemetry.Fail(ex.Message);
            _logger?.LogWarning("Agent telemetry (system, failed):\n{Telemetry}", telemetry.ToLogString());
            SafeInvokeTelemetry(telemetry);
            throw;
        }
    }

    /// <summary>
    /// Core iterative LLM loop: call the provider, execute tool calls, repeat.
    /// Extracted to eliminate duplication between ProcessMessageAsync, ProcessSystemMessageAsync, and SubagentManager.
    /// </summary>
    internal async Task<string?> RunIterativeLoopAsync(
        List<Dictionary<string, object?>> messages,
        ToolRegistry tools,
        int? maxIterations = null,
        AgentTelemetry? telemetry = null,
        CancellationToken ct = default)
    {
        var iterations = maxIterations ?? _maxIterations;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            // ── Context compaction ─────────────────────────────────────
            var (compactedMessages, wasCompacted) = await _compactor.CompactIfNeededAsync(messages, ct);
            if (wasCompacted)
            {
                messages = compactedMessages;
                telemetry?.RecordCompaction();
            }

            // ── Log LLM request ──────────────────────────────────────
            _logger?.LogInformation(
                "┌─ LLM Request (iteration {Iteration}) ─────────────────────\n" +
                "│ Model: {Model}\n" +
                "│ Messages: {Count} ({Roles})\n" +
                "│ Last user message: {Last}\n" +
                "└───────────────────────────────────────────────────────",
                iteration + 1,
                _model,
                messages.Count,
                SummarizeRoles(messages),
                Truncate(GetLastMessageContent(messages, MessageRoles.User), 300));

            using var llmActivity = SharpbotInstrumentation.ActivitySource.StartActivity("agent.llm_call");
            llmActivity?.SetTag("sharpbot.model", _model);
            llmActivity?.SetTag("sharpbot.iteration", iteration + 1);
            llmActivity?.SetTag("sharpbot.messages", messages.Count);

            var effectiveTemp = ResolveTemperature(_model);
            var effectiveMaxTokens = ResolveMaxTokens(_model);

            var llmSw = Stopwatch.StartNew();
            var response = await _provider.ChatAsync(
                messages: messages,
                tools: tools.GetDefinitions(),
                model: _model,
                maxTokens: effectiveMaxTokens,
                temperature: effectiveTemp,
                ct: ct);
            llmSw.Stop();

            llmActivity?.SetTag("sharpbot.finish_reason", response.FinishReason);
            llmActivity?.SetTag("sharpbot.tokens.prompt", response.Usage.GetValueOrDefault("prompt_tokens"));
            llmActivity?.SetTag("sharpbot.tokens.completion", response.Usage.GetValueOrDefault("completion_tokens"));
            llmActivity?.SetTag("sharpbot.tool_calls", response.ToolCalls.Count);

            // Record OTel metrics for this LLM call
            SharpbotInstrumentation.LlmDuration.Record(llmSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("model", _model));
            SharpbotInstrumentation.PromptTokens.Add(response.Usage.GetValueOrDefault("prompt_tokens"),
                new KeyValuePair<string, object?>("model", _model));
            SharpbotInstrumentation.CompletionTokens.Add(response.Usage.GetValueOrDefault("completion_tokens"),
                new KeyValuePair<string, object?>("model", _model));

            // ── Log LLM response ─────────────────────────────────────
            _logger?.LogInformation(
                "┌─ LLM Response (iteration {Iteration}, {Duration}) ────────\n" +
                "│ Finish: {FinishReason} | Tokens: {Prompt}→{Completion} ({Total} total)\n" +
                "│ Tool calls: {ToolCallCount}\n" +
                "│ Content: {Content}\n" +
                "└───────────────────────────────────────────────────────",
                iteration + 1,
                FormatDuration(llmSw.Elapsed),
                response.FinishReason,
                response.Usage.GetValueOrDefault("prompt_tokens"),
                response.Usage.GetValueOrDefault("completion_tokens"),
                response.Usage.GetValueOrDefault("total_tokens"),
                response.ToolCalls.Count,
                Truncate(response.Content ?? "(none)", 500));

            // Record LLM call telemetry
            telemetry?.AddLlmCall(new LlmCallTelemetry
            {
                Model = _model,
                Iteration = iteration,
                Duration = llmSw.Elapsed,
                PromptTokens = response.Usage.GetValueOrDefault("prompt_tokens"),
                CompletionTokens = response.Usage.GetValueOrDefault("completion_tokens"),
                TotalTokens = response.Usage.GetValueOrDefault("total_tokens"),
                FinishReason = response.FinishReason,
                HasToolCalls = response.HasToolCalls,
                ToolCallCount = response.ToolCalls.Count,
            });

            if (!response.HasToolCalls)
                return response.Content;

            var toolCallDicts = response.ToolCalls.Select(tc => new Dictionary<string, object?>
            {
                ["id"] = tc.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tc.Name,
                    ["arguments"] = JsonSerializer.Serialize(tc.Arguments),
                },
            }).ToList();

            messages = _context.AddAssistantMessage(messages, response.Content, toolCallDicts);

            foreach (var toolCall in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();

                var argsStr = JsonSerializer.Serialize(toolCall.Arguments);

                // ── Log tool request ─────────────────────────────────
                _logger?.LogInformation(
                    "┌─ Tool Request: {Name} ─────────────────────────────\n" +
                    "│ Call ID: {CallId}\n" +
                    "│ Args: {Args}\n" +
                    "└───────────────────────────────────────────────────────",
                    toolCall.Name, toolCall.Id, Truncate(argsStr, 1000));

                using var toolActivity = SharpbotInstrumentation.ActivitySource.StartActivity("agent.tool_call");
                toolActivity?.SetTag("sharpbot.tool.name", toolCall.Name);
                toolActivity?.SetTag("sharpbot.tool.call_id", toolCall.Id);

                var toolSw = Stopwatch.StartNew();
                string result;
                bool success = true;
                string? error = null;
                try
                {
                    result = await tools.ExecuteAsync(toolCall.Name, toolCall.Arguments);
                }
                catch (Exception ex)
                {
                    result = $"Error: {ex.Message}";
                    success = false;
                    error = ex.Message;
                    toolActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                }
                toolSw.Stop();

                toolActivity?.SetTag("sharpbot.tool.success", success);
                toolActivity?.SetTag("sharpbot.tool.result_length", result.Length);

                // Record OTel metrics for this tool call
                SharpbotInstrumentation.ToolCallsTotal.Add(1,
                    new KeyValuePair<string, object?>("tool", toolCall.Name));
                if (!success)
                    SharpbotInstrumentation.ToolCallsFailed.Add(1,
                        new KeyValuePair<string, object?>("tool", toolCall.Name));

                // ── Log tool response ────────────────────────────────
                _logger?.LogInformation(
                    "┌─ Tool Response: {Name} ({Duration}) ──────────────────\n" +
                    "│ Status: {Status}\n" +
                    "│ Result ({Length} chars): {Result}\n" +
                    "└───────────────────────────────────────────────────────",
                    toolCall.Name,
                    FormatDuration(toolSw.Elapsed),
                    success ? "✓ OK" : $"✗ Error: {error}",
                    result.Length,
                    Truncate(result, 1000));

                // Record tool call telemetry
                telemetry?.AddToolCall(new ToolCallTelemetry
                {
                    Name = toolCall.Name,
                    CallId = toolCall.Id,
                    Iteration = iteration,
                    Duration = toolSw.Elapsed,
                    Success = success,
                    Error = error,
                    ResultLength = result.Length,
                });

                messages = _context.AddToolResult(messages, toolCall.Id, toolCall.Name, result);
            }
        }

        return null; // max iterations reached
    }

    // ── Logging helpers ──────────────────────────────────────────────

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + $"... ({value.Length} chars total)";

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalMilliseconds < 1000 ? $"{ts.TotalMilliseconds:F0}ms" :
        ts.TotalSeconds < 60 ? $"{ts.TotalSeconds:F1}s" :
        $"{ts.TotalMinutes:F1}m";

    private static string SummarizeRoles(List<Dictionary<string, object?>> messages)
    {
        var counts = messages
            .GroupBy(m => m.GetValueOrDefault("role")?.ToString() ?? "?")
            .Select(g => $"{g.Key}:{g.Count()}")
            .ToArray();
        return string.Join(", ", counts);
    }

    private static string GetLastMessageContent(List<Dictionary<string, object?>> messages, string role)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].GetValueOrDefault("role")?.ToString() == role)
                return messages[i].GetValueOrDefault("content")?.ToString() ?? "";
        }
        return "(none)";
    }

    /// <summary>Safely invoke the telemetry callback (never let it throw).</summary>
    private void SafeInvokeTelemetry(AgentTelemetry telemetry)
    {
        try { _onTelemetry?.Invoke(telemetry); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Telemetry callback failed"); }
    }

    /// <summary>Resolve effective temperature for a model, checking per-model overrides first.</summary>
    private double ResolveTemperature(string model)
    {
        if (_modelOverrides.TryGetValue(model, out var over) && over.Temperature.HasValue)
            return over.Temperature.Value;

        // Check for partial/prefix matches (e.g., "claude" matches "anthropic/claude-opus-4-5")
        foreach (var (pattern, mo) in _modelOverrides)
        {
            if (mo.Temperature.HasValue && model.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return mo.Temperature.Value;
        }

        return _temperature;
    }

    /// <summary>Resolve effective max tokens for a model, checking per-model overrides first.</summary>
    private int ResolveMaxTokens(string model)
    {
        if (_modelOverrides.TryGetValue(model, out var over) && over.MaxTokens.HasValue)
            return over.MaxTokens.Value;

        // Check for partial/prefix matches
        foreach (var (pattern, mo) in _modelOverrides)
        {
            if (mo.MaxTokens.HasValue && model.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return mo.MaxTokens.Value;
        }

        return _maxTokens;
    }

    private void SetToolContexts(string channel, string chatId)
    {
        if (_tools.Get("message") is MessageTool messageTool)
            messageTool.SetContext(channel, chatId);
        if (_tools.Get("spawn") is SpawnTool spawnTool)
            spawnTool.SetContext(channel, chatId);
        if (_tools.Get("cron") is CronTool cronTool)
            cronTool.SetContext(channel, chatId);
    }

    /// <summary>
    /// Streaming iterative LLM loop: streams text deltas, tool events, and a final done event.
    /// Used by the SSE streaming endpoint.
    /// </summary>
    internal async IAsyncEnumerable<AgentStreamEvent> RunIterativeLoopStreamingAsync(
        List<Dictionary<string, object?>> messages,
        ToolRegistry tools,
        int? maxIterations = null,
        AgentTelemetry? telemetry = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var iterations = maxIterations ?? _maxIterations;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            // ── Context compaction ─────────────────────────────────────
            var (compactedMessages, wasCompacted) = await _compactor.CompactIfNeededAsync(messages, ct);
            if (wasCompacted)
            {
                messages = compactedMessages;
                telemetry?.RecordCompaction();
                yield return AgentStreamEvent.Status("Context compacted — summarized older messages to stay within limits", iteration);
            }

            if (iteration > 0)
                yield return AgentStreamEvent.Status($"Running iteration {iteration + 1}", iteration);

            _logger?.LogInformation(
                "┌─ LLM Stream Request (iteration {Iteration}) ─────────────\n" +
                "│ Model: {Model} | Messages: {Count}\n" +
                "└───────────────────────────────────────────────────────",
                iteration + 1, _model, messages.Count);

            var effectiveTemp = ResolveTemperature(_model);
            var effectiveMaxTokens = ResolveMaxTokens(_model);
            var llmSw = Stopwatch.StartNew();

            // Stream the LLM response
            Providers.LlmResponse? llmResponse = null;

            await foreach (var chunk in _provider.ChatStreamAsync(
                messages: messages,
                tools: tools.GetDefinitions(),
                model: _model,
                maxTokens: effectiveMaxTokens,
                temperature: effectiveTemp,
                ct: ct))
            {
                if (chunk.Type == "text_delta" && chunk.Delta != null)
                {
                    yield return AgentStreamEvent.TextDelta(chunk.Delta);
                }
                else if (chunk.Type == "done" && chunk.Response != null)
                {
                    llmResponse = chunk.Response;
                }
            }

            llmSw.Stop();

            if (llmResponse == null)
            {
                yield return AgentStreamEvent.Failed("No response from LLM");
                yield break;
            }

            // Record OTel metrics
            SharpbotInstrumentation.LlmDuration.Record(llmSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("model", _model));
            SharpbotInstrumentation.PromptTokens.Add(llmResponse.Usage.GetValueOrDefault("prompt_tokens"),
                new KeyValuePair<string, object?>("model", _model));
            SharpbotInstrumentation.CompletionTokens.Add(llmResponse.Usage.GetValueOrDefault("completion_tokens"),
                new KeyValuePair<string, object?>("model", _model));

            // Record telemetry
            telemetry?.AddLlmCall(new LlmCallTelemetry
            {
                Model = _model,
                Iteration = iteration,
                Duration = llmSw.Elapsed,
                PromptTokens = llmResponse.Usage.GetValueOrDefault("prompt_tokens"),
                CompletionTokens = llmResponse.Usage.GetValueOrDefault("completion_tokens"),
                TotalTokens = llmResponse.Usage.GetValueOrDefault("total_tokens"),
                FinishReason = llmResponse.FinishReason,
                HasToolCalls = llmResponse.HasToolCalls,
                ToolCallCount = llmResponse.ToolCalls.Count,
            });

            // If no tool calls, we're done — the text was already streamed
            if (!llmResponse.HasToolCalls)
                yield break;

            // There are tool calls — execute them
            var toolCallDicts = llmResponse.ToolCalls.Select(tc => new Dictionary<string, object?>
            {
                ["id"] = tc.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tc.Name,
                    ["arguments"] = JsonSerializer.Serialize(tc.Arguments),
                },
            }).ToList();

            messages = _context.AddAssistantMessage(messages, llmResponse.Content, toolCallDicts);

            foreach (var toolCall in llmResponse.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();

                yield return AgentStreamEvent.ToolStart(toolCall.Name, toolCall.Id);

                var toolSw = Stopwatch.StartNew();
                string result;
                bool success = true;
                string? error = null;
                try
                {
                    result = await tools.ExecuteAsync(toolCall.Name, toolCall.Arguments);
                }
                catch (Exception ex)
                {
                    result = $"Error: {ex.Message}";
                    success = false;
                    error = ex.Message;
                }
                toolSw.Stop();

                // Record OTel + telemetry
                SharpbotInstrumentation.ToolCallsTotal.Add(1,
                    new KeyValuePair<string, object?>("tool", toolCall.Name));
                if (!success)
                    SharpbotInstrumentation.ToolCallsFailed.Add(1,
                        new KeyValuePair<string, object?>("tool", toolCall.Name));

                telemetry?.AddToolCall(new ToolCallTelemetry
                {
                    Name = toolCall.Name,
                    CallId = toolCall.Id,
                    Iteration = iteration,
                    Duration = toolSw.Elapsed,
                    Success = success,
                    Error = error,
                    ResultLength = result.Length,
                });

                yield return AgentStreamEvent.ToolEnd(
                    toolCall.Name, toolCall.Id, success,
                    (int)toolSw.Elapsed.TotalMilliseconds, error, result.Length);

                messages = _context.AddToolResult(messages, toolCall.Id, toolCall.Name, result);
            }

            // Continue to next iteration (LLM will be called again with tool results)
        }
    }

    /// <summary>Process a message directly with streaming. Used by the SSE chat endpoint.</summary>
    public async IAsyncEnumerable<AgentStreamEvent> ProcessDirectStreamingAsync(
        string content,
        string? sessionKey = null,
        string channel = Channels.WellKnown.Cli,
        string chatId = Channels.WellKnown.Direct,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var msg = new InboundMessage
        {
            Channel = channel,
            SenderId = MessageRoles.User,
            ChatId = chatId,
            Content = content,
        };

        var effectiveSessionKey = sessionKey ?? $"{channel}:{chatId}";

        var telemetry = new AgentTelemetry
        {
            Channel = msg.Channel,
            SenderId = msg.SenderId,
            SessionKey = effectiveSessionKey,
            Model = _model,
        };

        // Inject per-skill environment variables
        var restoreEnv = _skills.InjectSkillEnvironment();
        try
        {
            var session = _sessions.GetOrCreate(effectiveSessionKey);
            SetToolContexts(msg.Channel, msg.ChatId);

            var messages = await _context.BuildMessagesAsync(
                history: session.GetHistory(_maxSessionMessages),
                currentMessage: msg.Content,
                channel: msg.Channel,
                chatId: msg.ChatId,
                ct: ct);

            // Stream the iterative loop
            var fullContent = new System.Text.StringBuilder();

            await foreach (var evt in RunIterativeLoopStreamingAsync(messages, _tools, telemetry: telemetry, ct: ct))
            {
                // Accumulate text for session saving
                if (evt.Type == "text_delta" && evt.Delta != null)
                    fullContent.Append(evt.Delta);

                yield return evt;
            }

            // Save to session
            var finalContent = fullContent.Length > 0 ? fullContent.ToString() : "I've completed processing but have no response to give.";
            session.AddMessage(MessageRoles.User, msg.Content);
            session.AddMessage(MessageRoles.Assistant, finalContent);
            _sessions.Save(session);

            telemetry.Complete();
            _logger?.LogInformation("Agent telemetry (streaming):\n{Telemetry}", telemetry.ToLogString());
            SafeInvokeTelemetry(telemetry);

            // Record OTel metrics
            SharpbotInstrumentation.RequestsTotal.Add(1,
                new KeyValuePair<string, object?>("channel", msg.Channel));
            SharpbotInstrumentation.RequestsSuccess.Add(1,
                new KeyValuePair<string, object?>("channel", msg.Channel));
            SharpbotInstrumentation.RequestDuration.Record(telemetry.TotalDuration.TotalMilliseconds,
                new KeyValuePair<string, object?>("channel", msg.Channel));

            // Emit final done event with stats
            yield return AgentStreamEvent.Completed(
                message: finalContent,
                sessionId: effectiveSessionKey,
                toolCalls: telemetry.ToolCalls.Select(tc => new Api.ToolCallDto
                {
                    Name = tc.Name,
                    DurationMs = (int)tc.Duration.TotalMilliseconds,
                    Success = tc.Success,
                    Error = tc.Error,
                    ResultLength = tc.ResultLength,
                    Iteration = tc.Iteration,
                }).ToList(),
                stats: new Api.ChatStatsDto
                {
                    TotalDurationMs = (int)telemetry.TotalDuration.TotalMilliseconds,
                    Iterations = telemetry.Iterations,
                    TotalTokens = telemetry.TotalTokens,
                    PromptTokens = telemetry.TotalPromptTokens,
                    CompletionTokens = telemetry.TotalCompletionTokens,
                    Model = telemetry.Model,
                    ContextCompactions = telemetry.CompactionCount,
                });
        }
        finally
        {
            restoreEnv();
        }
    }

    /// <summary>Process a message directly (for CLI or cron usage).</summary>
    public async Task<string> ProcessDirectAsync(
        string content,
        string? sessionKey = null,
        string channel = Channels.WellKnown.Cli,
        string chatId = Channels.WellKnown.Direct)
    {
        var (response, _) = await ProcessDirectWithTelemetryAsync(content, sessionKey, channel, chatId);
        return response;
    }

    /// <summary>Process a message directly and return telemetry alongside the response.</summary>
    public async Task<(string Content, AgentTelemetry Telemetry)> ProcessDirectWithTelemetryAsync(
        string content,
        string? sessionKey = null,
        string channel = Channels.WellKnown.Cli,
        string chatId = Channels.WellKnown.Direct)
    {
        var msg = new InboundMessage
        {
            Channel = channel,
            SenderId = MessageRoles.User,
            ChatId = chatId,
            Content = content,
        };

        var (response, telemetry) = await ProcessMessageWithTelemetryAsync(msg);
        return (response?.Content ?? "", telemetry);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _running = false;
        _processManager.Dispose();
        _browserManager.Dispose();
    }
}
