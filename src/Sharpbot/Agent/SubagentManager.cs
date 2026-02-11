using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sharpbot.Agent.Tools;
using Sharpbot.Bus;
using Sharpbot.Config;
using Sharpbot.Providers;

namespace Sharpbot.Agent;

/// <summary>
/// Manages background subagent execution.
/// Subagents are lightweight agent instances that run in the background
/// to handle specific tasks.
/// Thread-safe: tasks are tracked via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class SubagentManager
{
    private readonly ILlmProvider _provider;
    private readonly string _workspace;
    private readonly MessageBus _bus;
    private readonly string _model;
    private readonly string? _braveApiKey;
    private readonly ExecToolConfig _execConfig;
    private readonly bool _restrictToWorkspace;
    private readonly ConcurrentDictionary<string, Task> _runningTasks = new();
    private readonly ILogger? _logger;

    public SubagentManager(
        ILlmProvider provider,
        string workspace,
        MessageBus bus,
        string? model = null,
        string? braveApiKey = null,
        ExecToolConfig? execConfig = null,
        bool restrictToWorkspace = false,
        ILogger? logger = null)
    {
        _provider = provider;
        _workspace = workspace;
        _bus = bus;
        _model = model ?? provider.GetDefaultModel();
        _braveApiKey = braveApiKey;
        _execConfig = execConfig ?? new ExecToolConfig();
        _restrictToWorkspace = restrictToWorkspace;
        _logger = logger;
    }

    /// <summary>Spawn a subagent to execute a task in the background.</summary>
    public Task<string> SpawnAsync(
        string task,
        string? label = null,
        string? originChannel = null,
        string? originChatId = null)
    {
        var taskId = Guid.NewGuid().ToString()[..8];
        var displayLabel = label ?? (task.Length > 30 ? task[..30] + "..." : task);

        var origin = new SubagentOrigin(
            originChannel ?? Channels.WellKnown.Cli,
            originChatId ?? Channels.WellKnown.Direct);

        var bgTask = Task.Run(() => RunSubagentAsync(taskId, task, displayLabel, origin));
        _runningTasks[taskId] = bgTask;
        _ = bgTask.ContinueWith(t => _runningTasks.TryRemove(taskId, out Task? _));

        _logger?.LogInformation("Spawned subagent [{TaskId}]: {Label}", taskId, displayLabel);
        return Task.FromResult($"Subagent [{displayLabel}] started (id: {taskId}). I'll notify you when it completes.");
    }

    private async Task RunSubagentAsync(string taskId, string task, string label, SubagentOrigin origin)
    {
        _logger?.LogInformation("Subagent [{TaskId}] starting task: {Label}", taskId, label);

        var telemetry = new AgentTelemetry
        {
            Channel = $"subagent:{taskId}",
            SenderId = "subagent",
            SessionKey = $"subagent:{taskId}",
            Model = _model,
        };

        try
        {
            // Build subagent tools (no message tool, no spawn tool — ISP: subagents get a limited toolset)
            var tools = new ToolRegistry();
            var allowedDir = _restrictToWorkspace ? _workspace : null;
            tools.Register(new ReadFileTool(allowedDir));
            tools.Register(new WriteFileTool(allowedDir));
            tools.Register(new ListDirTool(allowedDir));
            tools.Register(new ExecTool(
                timeout: _execConfig.Timeout,
                workingDir: _workspace,
                restrictToWorkspace: _restrictToWorkspace));
            tools.Register(new WebSearchTool(_braveApiKey));
            tools.Register(new WebFetchTool());

            // Build messages
            var systemPrompt = BuildSubagentPrompt(task);
            var messages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = MessageRoles.System, ["content"] = systemPrompt },
                new() { ["role"] = MessageRoles.User, ["content"] = task },
            };

            // Run agent loop (limited iterations)
            const int maxIterations = 15;
            var finalResult = await RunSubagentLoopAsync(messages, tools, maxIterations, taskId, telemetry);
            finalResult ??= "Task completed but no final response was generated.";

            telemetry.Complete();
            _logger?.LogInformation("Subagent [{TaskId}] completed. Telemetry:\n{Telemetry}", taskId, telemetry.ToLogString());
            await AnnounceResultAsync(label, task, finalResult, origin, "ok");
        }
        catch (Exception e)
        {
            telemetry.Fail(e.Message);
            _logger?.LogError(e, "Subagent [{TaskId}] failed. Telemetry:\n{Telemetry}", taskId, telemetry.ToLogString());
            await AnnounceResultAsync(label, task, $"Error: {e.Message}", origin, "error");
        }
    }

    /// <summary>
    /// Subagent-specific LLM iteration loop.
    /// Structurally identical to <see cref="AgentLoop.RunIterativeLoopAsync"/> but
    /// uses simplified message management (no ContextBuilder dependency).
    /// </summary>
    private async Task<string?> RunSubagentLoopAsync(
        List<Dictionary<string, object?>> messages,
        ToolRegistry tools,
        int maxIterations,
        string taskId,
        AgentTelemetry? telemetry = null,
        CancellationToken ct = default)
    {
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            // ── Log LLM request ──────────────────────────────────────
            _logger?.LogInformation(
                "┌─ Subagent [{TaskId}] LLM Request (iteration {Iteration}) ──\n" +
                "│ Model: {Model} | Messages: {Count}\n" +
                "└───────────────────────────────────────────────────────",
                taskId, iteration + 1, _model, messages.Count);

            var llmSw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _provider.ChatAsync(
                messages: messages,
                tools: tools.GetDefinitions(),
                model: _model,
                ct: ct);
            llmSw.Stop();

            // ── Log LLM response ─────────────────────────────────────
            _logger?.LogInformation(
                "┌─ Subagent [{TaskId}] LLM Response (iteration {Iteration}, {Duration}) ─\n" +
                "│ Finish: {FinishReason} | Tokens: {Prompt}→{Completion} ({Total} total)\n" +
                "│ Tool calls: {ToolCallCount}\n" +
                "│ Content: {Content}\n" +
                "└───────────────────────────────────────────────────────",
                taskId, iteration + 1, FormatDuration(llmSw.Elapsed),
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

            messages.Add(new()
            {
                ["role"] = MessageRoles.Assistant,
                ["content"] = response.Content ?? "",
                ["tool_calls"] = toolCallDicts,
            });

            foreach (var toolCall in response.ToolCalls)
            {
                var argsStr = JsonSerializer.Serialize(toolCall.Arguments);

                // ── Log tool request ─────────────────────────────────
                _logger?.LogInformation(
                    "┌─ Subagent [{TaskId}] Tool Request: {Name} ─────────────\n" +
                    "│ Call ID: {CallId}\n" +
                    "│ Args: {Args}\n" +
                    "└───────────────────────────────────────────────────────",
                    taskId, toolCall.Name, toolCall.Id, Truncate(argsStr, 1000));

                var toolSw = System.Diagnostics.Stopwatch.StartNew();
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

                // ── Log tool response ────────────────────────────────
                _logger?.LogInformation(
                    "┌─ Subagent [{TaskId}] Tool Response: {Name} ({Duration}) ──\n" +
                    "│ Status: {Status}\n" +
                    "│ Result ({Length} chars): {Result}\n" +
                    "└───────────────────────────────────────────────────────",
                    taskId, toolCall.Name, FormatDuration(toolSw.Elapsed),
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

                messages.Add(new()
                {
                    ["role"] = MessageRoles.Tool,
                    ["tool_call_id"] = toolCall.Id,
                    ["name"] = toolCall.Name,
                    ["content"] = result,
                });
            }
        }

        return null;
    }

    // ── Logging helpers ──────────────────────────────────────────────

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + $"... ({value.Length} chars total)";

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalMilliseconds < 1000 ? $"{ts.TotalMilliseconds:F0}ms" :
        ts.TotalSeconds < 60 ? $"{ts.TotalSeconds:F1}s" :
        $"{ts.TotalMinutes:F1}m";

    private async Task AnnounceResultAsync(
        string label, string task, string result,
        SubagentOrigin origin, string status)
    {
        var statusText = status == "ok" ? "completed successfully" : "failed";

        var announceContent = $"""
            [Subagent '{label}' {statusText}]

            Task: {task}

            Result:
            {result}

            Summarize this naturally for the user. Keep it brief (1-2 sentences). Do not mention technical details like "subagent" or task IDs.
            """;

        var msg = new InboundMessage
        {
            Channel = Channels.WellKnown.System,
            SenderId = "subagent",
            ChatId = $"{origin.Channel}:{origin.ChatId}",
            Content = announceContent,
        };

        await _bus.PublishInboundAsync(msg);
    }

    private string BuildSubagentPrompt(string task) => $"""
        # Subagent

        You are a subagent spawned by the main agent to complete a specific task.

        ## Your Task
        {task}

        ## Rules
        1. Stay focused - complete only the assigned task, nothing else
        2. Your final response will be reported back to the main agent
        3. Do not initiate conversations or take on side tasks
        4. Be concise but informative in your findings

        ## What You Can Do
        - Read and write files in the workspace
        - Execute shell commands
        - Search the web and fetch web pages
        - Complete the task thoroughly

        ## What You Cannot Do
        - Send messages directly to users (no message tool available)
        - Spawn other subagents
        - Access the main agent's conversation history

        ## Workspace
        Your workspace is at: {_workspace}

        When you have completed the task, provide a clear summary of your findings or actions.
        """;

    /// <summary>Return the number of currently running subagents.</summary>
    public int RunningCount => _runningTasks.Count;

    /// <summary>Immutable record capturing the origin of a subagent request.</summary>
    private sealed record SubagentOrigin(string Channel, string ChatId);
}
