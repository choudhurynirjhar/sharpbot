using Sharpbot.Logging;

namespace Sharpbot.Api;

/// <summary>Logs API — view recent log entries from the in-memory ring buffer.</summary>
public static class LogsApi
{
    public static void MapLogsApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/logs").WithTags("Logs");

        group.MapGet("/", GetLogs);
        group.MapDelete("/", ClearLogs);
    }

    /// <summary>Get recent log entries with optional filtering.</summary>
    private static IResult GetLogs(
        LogRingBuffer buffer,
        string? level = null,
        string? category = null,
        string? search = null,
        int limit = 200,
        long? afterId = null)
    {
        category ??= "agent";

        LogLevel? minLevel = level?.ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" => LogLevel.Critical,
            _ => null,
        };

        var entries = buffer.GetEntries(
            minLevel: minLevel,
            category: category,
            search: search,
            limit: Math.Clamp(limit, 1, 1000),
            afterId: afterId);

        return Results.Json(new
        {
            entries = entries.Select(e => new
            {
                id = e.Id,
                timestamp = e.Timestamp.ToString("o"),
                level = e.Level.ToString(),
                category = e.Category,
                message = e.Message,
                exception = e.Exception,
            }),
            count = entries.Count,
            totalEntries = buffer.TotalEntries,
            bufferSize = buffer.Count,
        });
    }

    /// <summary>Clear all buffered log entries.</summary>
    private static IResult ClearLogs(LogRingBuffer buffer)
    {
        buffer.Clear();
        return Results.Json(new { success = true, message = "Log buffer cleared" });
    }
}
