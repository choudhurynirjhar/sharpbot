using Sharpbot.Telemetry;

namespace Sharpbot.Api;

/// <summary>Usage tracking API — view token consumption, tool usage, and history.</summary>
public static class UsageApi
{
    public static void MapUsageApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/usage").WithTags("Usage");

        group.MapGet("/", GetSummary);
        group.MapGet("/history", GetHistory);
        group.MapDelete("/", ClearUsage);
    }

    /// <summary>Get aggregated usage summary with breakdowns by model, tool, channel, and day.</summary>
    private static IResult GetSummary(UsageStore store, string? from = null, string? to = null)
    {
        DateTime? fromDate = null, toDate = null;
        if (DateTime.TryParse(from, out var f)) fromDate = f;
        if (DateTime.TryParse(to, out var t)) toDate = t;

        var summary = store.GetSummary(fromDate, toDate);
        return Results.Json(summary);
    }

    /// <summary>Get recent usage history entries.</summary>
    private static IResult GetHistory(UsageStore store, int? limit = 50, string? from = null, string? to = null)
    {
        DateTime? fromDate = null, toDate = null;
        if (DateTime.TryParse(from, out var f)) fromDate = f;
        if (DateTime.TryParse(to, out var t)) toDate = t;

        var entries = store.GetEntries(fromDate, toDate, limit);
        return Results.Json(new
        {
            entries,
            total = store.Count,
        });
    }

    /// <summary>Clear all usage data.</summary>
    private static IResult ClearUsage(UsageStore store)
    {
        store.Clear();
        return Results.Json(new { success = true, message = "Usage data cleared." });
    }
}
