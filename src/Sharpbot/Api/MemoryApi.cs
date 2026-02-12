using Sharpbot.Agent;
using Sharpbot.Services;

namespace Sharpbot.Api;

/// <summary>Semantic memory API — search, index, and inspect memory embeddings.</summary>
public static class MemoryApi
{
    public static void MapMemoryApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/memory").WithTags("Memory");

        group.MapGet("/stats", GetStats);
        group.MapPost("/search", SearchMemory);
        group.MapPost("/index", IndexMemory);
    }

    /// <summary>Get semantic memory statistics.</summary>
    private static IResult GetStats(SharpbotHostedService gateway)
    {
        if (!gateway.IsReady || gateway.Agent is null)
            return Results.Json(new { error = true, message = "Agent not ready" }, statusCode: 503);

        var store = gateway.SemanticMemory;
        if (store is null)
            return Results.Json(new { enabled = false, message = "Semantic memory is not enabled" });

        var stats = store.GetStats();
        return Results.Json(new
        {
            enabled = true,
            totalChunks = stats.TotalChunks,
            dimensions = stats.Dimensions,
            distinctSources = stats.DistinctSources,
        });
    }

    /// <summary>Search semantic memory.</summary>
    private static async Task<IResult> SearchMemory(MemorySearchRequest request, SharpbotHostedService gateway)
    {
        if (!gateway.IsReady || gateway.Agent is null)
            return Results.Json(new { error = true, message = "Agent not ready" }, statusCode: 503);

        var store = gateway.SemanticMemory;
        if (store is null)
            return Results.Json(new { error = true, message = "Semantic memory is not enabled" }, statusCode: 400);

        var results = await store.SearchAsync(
            request.Query,
            request.TopK ?? 5,
            request.MinScore ?? 0.5f);

        return Results.Json(new
        {
            query = request.Query,
            count = results.Count,
            results = results.Select(r => new
            {
                id = r.Id,
                content = r.Content,
                source = r.Source,
                sourceId = r.SourceId,
                score = Math.Round(r.Score, 4),
                createdAt = r.CreatedAt,
            }),
        });
    }

    /// <summary>Index content into semantic memory.</summary>
    private static async Task<IResult> IndexMemory(MemoryIndexRequest request, SharpbotHostedService gateway)
    {
        if (!gateway.IsReady || gateway.Agent is null)
            return Results.Json(new { error = true, message = "Agent not ready" }, statusCode: 503);

        var store = gateway.SemanticMemory;
        if (store is null)
            return Results.Json(new { error = true, message = "Semantic memory is not enabled" }, statusCode: 400);

        await store.IndexAsync(request.Content, request.Source ?? "manual", request.SourceId);

        var stats = store.GetStats();
        return Results.Json(new
        {
            success = true,
            message = "Content indexed successfully",
            totalChunks = stats.TotalChunks,
        });
    }
}

public record MemorySearchRequest
{
    public string Query { get; init; } = "";
    public int? TopK { get; init; }
    public float? MinScore { get; init; }
}

public record MemoryIndexRequest
{
    public string Content { get; init; } = "";
    public string? Source { get; init; }
    public string? SourceId { get; init; }
}
