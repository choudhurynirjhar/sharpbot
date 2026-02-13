using System.Text.Json;
using Sharpbot.Config;
using Sharpbot.Media;

namespace Sharpbot.Api;

/// <summary>Media API — normalized media contract, policy gate, and audit inspection.</summary>
public static class MediaApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void MapMediaApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/media").WithTags("Media");
        group.MapGet("/config", GetConfig);
        group.MapPost("/ingest", Ingest);
        group.MapGet("/assets", ListAssets);
        group.MapGet("/stats", GetStats);
        group.MapGet("/assets/{id}", GetAsset);
        group.MapGet("/assets/{id}/audit", GetAssetAudit);
        group.MapPost("/cleanup", CleanupExpired);
    }

    private static IResult GetConfig(SharpbotConfig config)
    {
        return Results.Json(new
        {
            enabled = config.Tools.Media.Enabled,
            allowedMimeTypes = config.Tools.Media.AllowedMimeTypes,
            maxBytesPerItem = config.Tools.Media.MaxBytesPerItem,
            maxItemsPerMessage = config.Tools.Media.MaxItemsPerMessage,
            tempTtlMinutes = config.Tools.Media.TempTtlMinutes,
            quarantineUnknownMime = config.Tools.Media.QuarantineUnknownMime,
            rejectOverLimit = config.Tools.Media.RejectOverLimit,
            downloadTimeoutSec = config.Tools.Media.DownloadTimeoutSec,
            processingTimeoutSec = config.Tools.Media.ProcessingTimeoutSec,
            enableOcr = config.Tools.Media.EnableOcr,
            enableTranscription = config.Tools.Media.EnableTranscription,
            auditEvents = config.Tools.Media.AuditEvents,
        }, JsonOptions);
    }

    private static async Task<IResult> Ingest(HttpRequest request, MediaPipelineService mediaPipeline)
    {
        try
        {
            var payload = await JsonSerializer.DeserializeAsync<MediaIngestRequest>(request.Body, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Channel) || string.IsNullOrWhiteSpace(payload.ChatId))
                return Results.BadRequest(new { error = true, message = "channel and chatId are required." });

            var asset = mediaPipeline.RegisterInbound(payload, actor: "api/media/ingest");
            return Results.Json(new
            {
                success = true,
                asset = ToDto(asset),
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = true, message = ex.Message }, statusCode: 500);
        }
    }

    private static IResult ListAssets(MediaPipelineService mediaPipeline, int limit = 50)
    {
        var assets = mediaPipeline.ListRecent(limit);
        return Results.Json(new
        {
            count = assets.Count,
            assets = assets.Select(ToDto).ToList(),
        }, JsonOptions);
    }

    private static IResult GetStats(MediaPipelineService mediaPipeline)
    {
        return Results.Json(mediaPipeline.GetStats(), JsonOptions);
    }

    private static IResult GetAsset(string id, MediaPipelineService mediaPipeline)
    {
        var asset = mediaPipeline.GetById(id);
        if (asset is null)
            return Results.NotFound(new { error = true, message = $"Asset '{id}' not found." });
        return Results.Json(ToDto(asset), JsonOptions);
    }

    private static IResult GetAssetAudit(string id, MediaPipelineService mediaPipeline)
    {
        var events = mediaPipeline.GetAudit(id);
        return Results.Json(new
        {
            id,
            count = events.Count,
            events,
        }, JsonOptions);
    }

    private static IResult CleanupExpired(MediaPipelineService mediaPipeline)
    {
        var removed = mediaPipeline.CleanupExpired();
        return Results.Json(new
        {
            success = true,
            removed,
        }, JsonOptions);
    }

    private static object ToDto(MediaAsset asset) => new
    {
        id = asset.Id,
        channel = asset.Channel,
        chatId = asset.ChatId,
        mimeType = asset.MimeType,
        fileName = asset.FileName,
        sizeBytes = asset.SizeBytes,
        sourceType = asset.SourceType,
        sourceRef = asset.SourceRef,
        localPath = asset.LocalPath,
        state = asset.State.ToString(),
        policyDecision = asset.PolicyDecision,
        policyReason = asset.PolicyReason,
        createdAtUtc = asset.CreatedAtUtc,
        expiresAtUtc = asset.ExpiresAtUtc,
        metadata = asset.Metadata,
    };
}
