using System.Text.Json;
using System.Text.Json.Serialization;
using Sharpbot.Agent;
using Sharpbot.Channels;
using Sharpbot.Services;

namespace Sharpbot.Api;

/// <summary>Chat API — send messages and manage sessions.</summary>
public static class ChatApi
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void MapChatApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/chat").WithTags("Chat");

        group.MapPost("/", SendMessage);
        group.MapPost("/stream", StreamMessage);
        group.MapGet("/sessions", ListSessions);
        group.MapDelete("/sessions/{key}", DeleteSession);
        group.MapGet("/context-info", GetContextInfo);
    }

    /// <summary>Send a message to the agent and get a response.</summary>
    private static async Task<IResult> SendMessage(ChatRequest request, SharpbotHostedService gateway)
    {
        if (!gateway.IsReady || gateway.Agent is null)
        {
            return Results.Json(new
            {
                error = true,
                message = gateway.Error ?? "Agent is not ready. Please configure an API key in Settings.",
            }, statusCode: 503);
        }

        var sessionKey = request.SessionId ?? "web:default";

        try
        {
            var (content, telemetry) = await gateway.Agent.ProcessDirectWithTelemetryAsync(
                content: request.Message,
                sessionKey: sessionKey,
                channel: "web",
                chatId: request.SessionId ?? "default");

            return Results.Json(new ChatResponse
            {
                Message = content,
                SessionId = sessionKey,
                Timestamp = DateTime.UtcNow,
                ToolCalls = telemetry.ToolCalls.Select(tc => new ToolCallDto
                {
                    Name = tc.Name,
                    DurationMs = (int)tc.Duration.TotalMilliseconds,
                    Success = tc.Success,
                    Error = tc.Error,
                    ResultLength = tc.ResultLength,
                    Iteration = tc.Iteration,
                }).ToList(),
                Stats = new ChatStatsDto
                {
                    TotalDurationMs = (int)telemetry.TotalDuration.TotalMilliseconds,
                    Iterations = telemetry.Iterations,
                    TotalTokens = telemetry.TotalTokens,
                    PromptTokens = telemetry.TotalPromptTokens,
                    CompletionTokens = telemetry.TotalCompletionTokens,
                    Model = telemetry.Model,
                    ContextCompactions = telemetry.CompactionCount,
                },
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new
            {
                error = true,
                message = $"Error processing message: {ex.Message}",
            }, statusCode: 500);
        }
    }

    /// <summary>Send a message and stream the response as Server-Sent Events.</summary>
    private static async Task StreamMessage(ChatRequest request, SharpbotHostedService gateway, HttpContext httpContext)
    {
        var response = httpContext.Response;

        if (!gateway.IsReady || gateway.Agent is null)
        {
            response.StatusCode = 503;
            response.ContentType = "text/event-stream";
            await response.StartAsync();
            await WriteSseEvent(response, "error", new { error = gateway.Error ?? "Agent is not ready. Please configure an API key in Settings." });
            await response.Body.FlushAsync();
            return;
        }

        var sessionKey = request.SessionId ?? "web:default";

        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no"; // Disable nginx buffering
        await response.StartAsync();

        var ct = httpContext.RequestAborted;

        try
        {
            await foreach (var evt in gateway.Agent.ProcessDirectStreamingAsync(
                content: request.Message,
                sessionKey: sessionKey,
                channel: "web",
                chatId: request.SessionId ?? "default",
                ct: ct))
            {
                await WriteSseEvent(response, evt.Type, evt);
                await response.Body.FlushAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — that's fine
        }
        catch (Exception ex)
        {
            try
            {
                await WriteSseEvent(response, "error", new { error = ex.Message });
                await response.Body.FlushAsync();
            }
            catch
            {
                // Response may already be completed
            }
        }
    }

    /// <summary>Write a single SSE event to the response stream.</summary>
    private static async Task WriteSseEvent(HttpResponse response, string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data, SseJsonOptions);
        var payload = $"event: {eventType}\ndata: {json}\n\n";
        await response.WriteAsync(payload);
    }

    /// <summary>List all chat sessions.</summary>
    private static IResult ListSessions(SharpbotHostedService gateway)
    {
        var sessions = gateway.SessionManager.ListSessions();
        return Results.Json(sessions);
    }

    /// <summary>Get context info (token estimation) for the current or specified session.</summary>
    private static IResult GetContextInfo(SharpbotHostedService gateway, string? sessionId = null)
    {
        var key = sessionId ?? "web:default";
        var session = gateway.SessionManager.GetOrCreate(key);
        var messages = session.Messages;
        var estimatedTokens = Agent.ContextCompactor.EstimateTokens(messages);

        var model = gateway.Config?.Agents.Defaults.Model ?? "unknown";
        var contextLimit = gateway.Config?.Agents.Defaults.MaxContextTokens
            ?? Agent.ContextCompactor.GetContextLimit(model);
        var threshold = (int)(contextLimit * 0.80);

        return Results.Json(new
        {
            sessionKey = key,
            messageCount = messages.Count,
            estimatedTokens,
            contextLimit,
            compactionThreshold = threshold,
            willCompact = estimatedTokens > threshold,
            model,
            headroom = contextLimit - estimatedTokens,
        });
    }

    /// <summary>Delete a chat session.</summary>
    private static IResult DeleteSession(string key, SharpbotHostedService gateway)
    {
        // URL-decode and restore colon
        var decodedKey = Uri.UnescapeDataString(key).Replace('_', ':');
        var deleted = gateway.SessionManager.Delete(decodedKey);
        return deleted
            ? Results.Json(new { success = true, message = $"Session '{decodedKey}' deleted." })
            : Results.NotFound(new { error = true, message = $"Session '{decodedKey}' not found." });
    }
}

public record ChatRequest
{
    public string Message { get; init; } = "";
    public string? SessionId { get; init; }
}

public record ChatResponse
{
    public string Message { get; init; } = "";
    public string SessionId { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public List<ToolCallDto> ToolCalls { get; init; } = [];
    public ChatStatsDto? Stats { get; init; }
}

public record ToolCallDto
{
    public string Name { get; init; } = "";
    public int DurationMs { get; init; }
    public bool Success { get; init; } = true;
    public string? Error { get; init; }
    public int ResultLength { get; init; }
    public int Iteration { get; init; }
}

public record ChatStatsDto
{
    public int TotalDurationMs { get; init; }
    public int Iterations { get; init; }
    public int TotalTokens { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public string Model { get; init; } = "";
    public int ContextCompactions { get; init; }
}
