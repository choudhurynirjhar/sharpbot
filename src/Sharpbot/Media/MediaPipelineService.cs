using System.Collections.Concurrent;
using Sharpbot.Config;

namespace Sharpbot.Media;

public enum MediaLifecycleState
{
    Received,
    Validated,
    Materialized,
    Processed,
    Quarantined,
    Rejected,
    Failed,
    Expired,
}

public sealed record MediaAsset
{
    public required string Id { get; init; }
    public required string Channel { get; init; }
    public required string ChatId { get; init; }
    public required string MimeType { get; init; }
    public string FileName { get; init; } = "";
    public long SizeBytes { get; init; }
    public string SourceType { get; init; } = "external";
    public string SourceRef { get; init; } = "";
    public string? LocalPath { get; init; }
    public required MediaLifecycleState State { get; init; }
    public string PolicyDecision { get; init; } = "allow";
    public string? PolicyReason { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed record MediaPipelineStats
{
    public int TotalAssets { get; init; }
    public int ActiveAssets { get; init; }
    public int ExpiredAssets { get; init; }
    public Dictionary<string, int> ByState { get; init; } = [];
    public Dictionary<string, int> ByDecision { get; init; } = [];
}

public sealed record MediaAuditEvent
{
    public required string AssetId { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required string EventType { get; init; }
    public required string Message { get; init; }
    public string? Actor { get; init; }
}

public sealed record MediaIngestRequest
{
    public required string Channel { get; init; }
    public required string ChatId { get; init; }
    public string MimeType { get; init; } = "";
    public string FileName { get; init; } = "";
    public long SizeBytes { get; init; }
    public string SourceType { get; init; } = "external";
    public string SourceRef { get; init; } = "";
    public string? LocalPath { get; init; }
    public int ItemCountInMessage { get; init; } = 1;
    public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>
/// Media ingestion policy gate and registry for normalized media contracts.
/// This provides enterprise controls: mime/size/count policy, lifecycle tracking, and audit events.
/// </summary>
public sealed class MediaPipelineService
{
    private readonly MediaToolConfig _config;
    private readonly IOcrProcessor _ocrProcessor;
    private readonly ITranscriptionProcessor _transcriptionProcessor;
    private readonly ConcurrentDictionary<string, MediaAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<MediaAuditEvent>> _audit = new(StringComparer.OrdinalIgnoreCase);

    public MediaPipelineService(
        MediaToolConfig config,
        IOcrProcessor ocrProcessor,
        ITranscriptionProcessor transcriptionProcessor)
    {
        _config = config;
        _ocrProcessor = ocrProcessor;
        _transcriptionProcessor = transcriptionProcessor;
    }

    public MediaAsset RegisterInbound(MediaIngestRequest request, string actor = "system")
    {
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var mime = request.MimeType?.Trim().ToLowerInvariant() ?? "";
        var (decision, reason) = EvaluatePolicy(request, mime);

        var state = decision switch
        {
            "reject" => MediaLifecycleState.Rejected,
            "quarantine" => MediaLifecycleState.Quarantined,
            _ => string.IsNullOrWhiteSpace(request.LocalPath) ? MediaLifecycleState.Validated : MediaLifecycleState.Materialized,
        };

        var asset = new MediaAsset
        {
            Id = id,
            Channel = request.Channel,
            ChatId = request.ChatId,
            MimeType = mime,
            FileName = request.FileName,
            SizeBytes = request.SizeBytes,
            SourceType = request.SourceType,
            SourceRef = request.SourceRef,
            LocalPath = request.LocalPath,
            State = state,
            PolicyDecision = decision,
            PolicyReason = reason,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(Math.Max(1, _config.TempTtlMinutes)),
            Metadata = request.Metadata,
        };

        _assets[id] = asset;
        EmitAudit(id, "received", $"Media asset registered: {asset.FileName}", actor, now);
        EmitAudit(id, "policy", $"Policy decision={decision}; reason={reason ?? "n/a"}", actor, now);
        EmitAudit(id, "state", $"Lifecycle state={state}", actor, now);

        if (decision != "allow")
            return asset;

        if (!string.IsNullOrWhiteSpace(asset.LocalPath))
            asset = TryRunProcessors(asset, actor);

        return asset;
    }

    public MediaAsset? GetById(string id) => _assets.TryGetValue(id, out var asset) ? asset : null;

    public List<MediaAsset> ListRecent(int limit = 100)
    {
        return _assets.Values
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToList();
    }

    public List<MediaAuditEvent> GetAudit(string id)
    {
        if (!_audit.TryGetValue(id, out var events))
            return [];
        return [.. events.OrderBy(e => e.TimestampUtc)];
    }

    public int CleanupExpired(string actor = "cleanup-worker")
    {
        var now = DateTime.UtcNow;
        var removed = 0;
        foreach (var item in _assets)
        {
            if (item.Value.ExpiresAtUtc > now)
                continue;

            if (_assets.TryRemove(item.Key, out _))
            {
                removed++;
                EmitAudit(item.Key, "expired", "Media asset expired and removed", actor, now);
            }
        }
        return removed;
    }

    public MediaPipelineStats GetStats()
    {
        var now = DateTime.UtcNow;
        var assets = _assets.Values.ToList();

        var byState = assets
            .GroupBy(a => a.State.ToString())
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var byDecision = assets
            .GroupBy(a => a.PolicyDecision)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new MediaPipelineStats
        {
            TotalAssets = assets.Count,
            ActiveAssets = assets.Count(a => a.ExpiresAtUtc > now),
            ExpiredAssets = assets.Count(a => a.ExpiresAtUtc <= now),
            ByState = byState,
            ByDecision = byDecision,
        };
    }

    private (string Decision, string? Reason) EvaluatePolicy(MediaIngestRequest request, string mime)
    {
        if (!_config.Enabled)
            return ("allow", "media pipeline disabled");

        if (request.ItemCountInMessage > Math.Max(1, _config.MaxItemsPerMessage))
            return ("reject", $"item count {request.ItemCountInMessage} exceeds max {_config.MaxItemsPerMessage}");

        if (request.SizeBytes > Math.Max(1, _config.MaxBytesPerItem))
            return _config.RejectOverLimit
                ? ("reject", $"size {request.SizeBytes} exceeds max {_config.MaxBytesPerItem}")
                : ("quarantine", $"size {request.SizeBytes} exceeds max {_config.MaxBytesPerItem}");

        if (IsMimeAllowed(mime))
            return ("allow", null);

        return _config.QuarantineUnknownMime
            ? ("quarantine", $"mime '{mime}' not in allowlist")
            : ("allow", "unknown mime accepted by policy");
    }

    private MediaAsset TryRunProcessors(MediaAsset asset, string actor)
    {
        var state = asset.State;
        var metadata = new Dictionary<string, string>(asset.Metadata, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        try
        {
            // OCR hook: image/* and application/pdf (provider may still reject unsupported content).
            if (_config.EnableOcr && (asset.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(asset.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)))
            {
                var ocr = RunWithTimeout("ocr", _config.ProcessingTimeoutSec, () =>
                {
                    return _ocrProcessor.ExtractTextAsync(asset);
                });
                metadata["ocr_status"] = "ok";
                metadata["ocr_provider"] = ocr.Provider;
                metadata["ocr_model"] = ocr.Model;
                metadata["ocr_text"] = TrimMetadataValue(ocr.Text);
                if (!string.IsNullOrWhiteSpace(ocr.Language))
                    metadata["ocr_language"] = ocr.Language;
                EmitAudit(asset.Id, "processor", $"OCR hook executed via {ocr.Provider}:{ocr.Model}", actor, now);
            }

            // STT hook: audio/* and video/*.
            if (_config.EnableTranscription && (asset.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || asset.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
            {
                var stt = RunWithTimeout("transcription", _config.ProcessingTimeoutSec, () =>
                {
                    return _transcriptionProcessor.TranscribeAsync(asset);
                });
                metadata["transcription_status"] = "ok";
                metadata["transcription_provider"] = stt.Provider;
                metadata["transcription_model"] = stt.Model;
                metadata["transcription_text"] = TrimMetadataValue(stt.Text);
                if (!string.IsNullOrWhiteSpace(stt.Language))
                    metadata["transcription_language"] = stt.Language;
                metadata["transcription_duration_sec"] = stt.DurationSec.ToString("F2");
                EmitAudit(asset.Id, "processor", $"Transcription hook executed via {stt.Provider}:{stt.Model}", actor, now);
            }

            state = MediaLifecycleState.Processed;
            var updated = asset with
            {
                State = state,
                Metadata = metadata,
            };
            _assets[asset.Id] = updated;
            EmitAudit(asset.Id, "state", $"Lifecycle state={state}", actor, DateTime.UtcNow);
            return updated;
        }
        catch (TimeoutException tex)
        {
            state = MediaLifecycleState.Failed;
            metadata["failure_code"] = "MEDIA_PROCESSING_TIMEOUT";
            metadata["failure_reason"] = tex.Message;
            var updated = asset with
            {
                State = state,
                Metadata = metadata,
            };
            _assets[asset.Id] = updated;
            EmitAudit(asset.Id, "failure", "MEDIA_PROCESSING_TIMEOUT", actor, DateTime.UtcNow);
            EmitAudit(asset.Id, "state", $"Lifecycle state={state}", actor, DateTime.UtcNow);
            return updated;
        }
        catch (MediaProcessingException ex)
        {
            state = MediaLifecycleState.Failed;
            metadata["failure_code"] = ex.Code;
            metadata["failure_reason"] = ex.Message;
            var updated = asset with
            {
                State = state,
                Metadata = metadata,
            };
            _assets[asset.Id] = updated;
            EmitAudit(asset.Id, "failure", $"{ex.Code}: {ex.Message}", actor, DateTime.UtcNow);
            EmitAudit(asset.Id, "state", $"Lifecycle state={state}", actor, DateTime.UtcNow);
            return updated;
        }
        catch (Exception ex)
        {
            state = MediaLifecycleState.Failed;
            metadata["failure_code"] = "MEDIA_PROCESSING_FAILED";
            metadata["failure_reason"] = ex.Message;
            var updated = asset with
            {
                State = state,
                Metadata = metadata,
            };
            _assets[asset.Id] = updated;
            EmitAudit(asset.Id, "failure", $"MEDIA_PROCESSING_FAILED: {ex.Message}", actor, DateTime.UtcNow);
            EmitAudit(asset.Id, "state", $"Lifecycle state={state}", actor, DateTime.UtcNow);
            return updated;
        }
    }

    private static T RunWithTimeout<T>(string stageName, int timeoutSec, Func<Task<T>> action)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSec));
        var task = Task.Run(async () => await action());
        if (!task.Wait(timeout))
            throw new TimeoutException($"{stageName} exceeded timeout {timeout.TotalSeconds:F0}s");
        return task.GetAwaiter().GetResult();
    }

    private static string TrimMetadataValue(string value, int maxLen = 3000)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var normalized = value.Trim();
        if (normalized.Length <= maxLen)
            return normalized;
        return normalized[..maxLen] + "...";
    }

    private bool IsMimeAllowed(string mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
            return false;

        foreach (var allowed in _config.AllowedMimeTypes)
        {
            if (string.IsNullOrWhiteSpace(allowed))
                continue;

            var pattern = allowed.Trim().ToLowerInvariant();
            if (pattern.EndsWith('/'))
            {
                if (mime.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (string.Equals(mime, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void EmitAudit(string id, string eventType, string message, string actor, DateTime now)
    {
        if (!_config.AuditEvents)
            return;

        var list = _audit.GetOrAdd(id, _ => []);
        lock (list)
        {
            list.Add(new MediaAuditEvent
            {
                AssetId = id,
                TimestampUtc = now,
                EventType = eventType,
                Message = message,
                Actor = actor,
            });
        }
    }
}
