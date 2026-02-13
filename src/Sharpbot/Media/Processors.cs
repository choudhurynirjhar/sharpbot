using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sharpbot.Config;

namespace Sharpbot.Media;

public sealed class MediaProcessingException : Exception
{
    public string Code { get; }

    public MediaProcessingException(string code, string message) : base(message)
    {
        Code = code;
    }
}

public sealed record OcrResult
{
    public string Text { get; init; } = "";
    public string Language { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
}

public sealed record TranscriptionResult
{
    public string Text { get; init; } = "";
    public string Language { get; init; } = "";
    public double DurationSec { get; init; }
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
}

public interface IOcrProcessor
{
    Task<OcrResult> ExtractTextAsync(MediaAsset asset, CancellationToken ct = default);
}

public interface ITranscriptionProcessor
{
    Task<TranscriptionResult> TranscribeAsync(MediaAsset asset, CancellationToken ct = default);
}

public static class MediaProcessorFactory
{
    public static IOcrProcessor CreateOcrProcessor(SharpbotConfig config, ILogger logger)
    {
        if (!config.Tools.Media.EnableOcr)
            return new NoopOcrProcessor();

        var provider = (config.Tools.Media.OcrProvider ?? "").Trim().ToLowerInvariant();
        if (provider == "openai")
        {
            if (string.IsNullOrWhiteSpace(config.Providers.OpenAI.ApiKey))
                return new NoopOcrProcessor();
            return new OpenAiOcrProcessor(config, logger);
        }

        return new NoopOcrProcessor();
    }

    public static ITranscriptionProcessor CreateTranscriptionProcessor(SharpbotConfig config, ILogger logger)
    {
        if (!config.Tools.Media.EnableTranscription)
            return new NoopTranscriptionProcessor();

        var provider = (config.Tools.Media.TranscriptionProvider ?? "").Trim().ToLowerInvariant();
        if (provider == "openai")
        {
            if (string.IsNullOrWhiteSpace(config.Providers.OpenAI.ApiKey))
                return new NoopTranscriptionProcessor();
            return new OpenAiTranscriptionProcessor(config, logger);
        }

        return new NoopTranscriptionProcessor();
    }
}

internal sealed class NoopOcrProcessor : IOcrProcessor
{
    public Task<OcrResult> ExtractTextAsync(MediaAsset asset, CancellationToken ct = default) =>
        Task.FromResult(new OcrResult());
}

internal sealed class NoopTranscriptionProcessor : ITranscriptionProcessor
{
    public Task<TranscriptionResult> TranscribeAsync(MediaAsset asset, CancellationToken ct = default) =>
        Task.FromResult(new TranscriptionResult());
}

internal sealed class OpenAiOcrProcessor : IOcrProcessor
{
    private readonly string _apiKey;
    private readonly string _apiBase;
    private readonly string _model;
    private readonly ILogger _logger;
    private readonly HttpClient _http = new();

    public OpenAiOcrProcessor(SharpbotConfig config, ILogger logger)
    {
        _apiKey = config.Providers.OpenAI.ApiKey;
        _apiBase = ResolveApiBase(config.Tools.Media.OcrApiBase, config.Providers.OpenAI.ApiBase);
        _model = string.IsNullOrWhiteSpace(config.Tools.Media.OcrModel) ? "gpt-4o-mini" : config.Tools.Media.OcrModel;
        _logger = logger;
    }

    public async Task<OcrResult> ExtractTextAsync(MediaAsset asset, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(asset.LocalPath) || !File.Exists(asset.LocalPath))
            throw new MediaProcessingException("MEDIA_FILE_NOT_FOUND", "OCR file path not found.");
        if (!asset.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new MediaProcessingException("MEDIA_OCR_UNSUPPORTED_MIME", $"OCR unsupported MIME: {asset.MimeType}");

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(asset.LocalPath, ct);
        }
        catch (Exception ex)
        {
            throw new MediaProcessingException("MEDIA_FILE_READ_FAILED", ex.Message);
        }

        var dataUrl = $"data:{asset.MimeType};base64,{Convert.ToBase64String(bytes)}";
        var payload = new
        {
            model = _model,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "Extract all visible text from this image. Return plain text only." },
                        new { type = "image_url", image_url = new { url = dataUrl } },
                    }
                }
            },
            temperature = 0,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new MediaProcessingException("MEDIA_OCR_PROVIDER_ERROR", $"OpenAI OCR failed: {(int)resp.StatusCode} {body}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return new OcrResult
            {
                Text = text.Trim(),
                Language = "",
                Provider = "openai",
                Model = _model,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse OCR response");
            throw new MediaProcessingException("MEDIA_OCR_PARSE_ERROR", ex.Message);
        }
    }

    private static string ResolveApiBase(string? preferred, string? providerBase)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(providerBase)) return providerBase.Trim().TrimEnd('/');
        return "https://api.openai.com/v1";
    }
}

internal sealed class OpenAiTranscriptionProcessor : ITranscriptionProcessor
{
    private readonly string _apiKey;
    private readonly string _apiBase;
    private readonly string _model;
    private readonly string _language;
    private readonly ILogger _logger;
    private readonly HttpClient _http = new();

    public OpenAiTranscriptionProcessor(SharpbotConfig config, ILogger logger)
    {
        _apiKey = config.Providers.OpenAI.ApiKey;
        _apiBase = ResolveApiBase(config.Tools.Media.TranscriptionApiBase, config.Providers.OpenAI.ApiBase);
        _model = string.IsNullOrWhiteSpace(config.Tools.Media.TranscriptionModel) ? "gpt-4o-mini-transcribe" : config.Tools.Media.TranscriptionModel;
        _language = (config.Tools.Media.DefaultLanguage ?? "").Trim();
        _logger = logger;
    }

    public async Task<TranscriptionResult> TranscribeAsync(MediaAsset asset, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(asset.LocalPath) || !File.Exists(asset.LocalPath))
            throw new MediaProcessingException("MEDIA_FILE_NOT_FOUND", "Transcription file path not found.");
        if (!(asset.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
              asset.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
            throw new MediaProcessingException("MEDIA_STT_UNSUPPORTED_MIME", $"STT unsupported MIME: {asset.MimeType}");

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(_model), "model");
            form.Add(new StringContent("verbose_json"), "response_format");
            if (!string.IsNullOrWhiteSpace(_language))
                form.Add(new StringContent(_language), "language");

            var fileStream = File.OpenRead(asset.LocalPath);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(asset.MimeType);
            form.Add(fileContent, "file", asset.FileName);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/audio/transcriptions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = form;

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new MediaProcessingException("MEDIA_STT_PROVIDER_ERROR", $"OpenAI STT failed: {(int)resp.StatusCode} {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new TranscriptionResult
            {
                Text = root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
                Language = root.TryGetProperty("language", out var lang) ? lang.GetString() ?? "" : "",
                DurationSec = root.TryGetProperty("duration", out var dur) && dur.TryGetDouble(out var d) ? d : 0,
                Provider = "openai",
                Model = _model,
            };
        }
        catch (MediaProcessingException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcription request failed");
            throw new MediaProcessingException("MEDIA_STT_PROVIDER_ERROR", ex.Message);
        }
    }

    private static string ResolveApiBase(string? preferred, string? providerBase)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(providerBase)) return providerBase.Trim().TrimEnd('/');
        return "https://api.openai.com/v1";
    }
}
