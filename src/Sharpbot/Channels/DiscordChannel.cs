using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sharpbot.Bus;
using Sharpbot.Config;
using Sharpbot.Media;

namespace Sharpbot.Channels;

/// <summary>
/// Discord channel implementation using Gateway WebSocket for receiving
/// and REST API for sending messages. Ported from nanobot's Python Discord channel.
/// </summary>
public sealed class DiscordChannel : BaseChannel, IDisposable
{
    public override string ChannelName => "discord";

    private const string DiscordApiBase = "https://discord.com/api/v10";
    private const int MaxAttachmentBytes = 20 * 1024 * 1024; // 20 MB

    private readonly DiscordConfig _config;
    private readonly MediaPipelineService? _mediaPipeline;
    private ClientWebSocket? _ws;
    private int? _seq;
    private CancellationTokenSource? _cts;
    private Task? _heartbeatTask;
    private readonly Dictionary<string, CancellationTokenSource> _typingTasks = [];
    private HttpClient? _http;

    public DiscordChannel(
        DiscordConfig config,
        MessageBus bus,
        MediaPipelineService? mediaPipeline = null,
        ILogger? logger = null)
        : base(config, bus, logger)
    {
        _config = config;
        _mediaPipeline = mediaPipeline;
    }

    public override async Task StartAsync()
    {
        if (string.IsNullOrEmpty(_config.Token))
        {
            Logger?.LogError("Discord bot token not configured");
            return;
        }

        Running = true;
        _cts = new CancellationTokenSource();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        while (Running && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                Logger?.LogInformation("Connecting to Discord gateway...");
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri(_config.GatewayUrl), _cts.Token);
                await GatewayLoopAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                Logger?.LogWarning(e, "Discord gateway error");
                if (Running)
                {
                    Logger?.LogInformation("Reconnecting to Discord gateway in 5 seconds...");
                    try { await Task.Delay(5000, _cts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                DisposeWebSocket();
            }
        }
    }

    public override async Task StopAsync()
    {
        Running = false;
        _heartbeatTask = null;

        foreach (var (_, cts) in _typingTasks)
            cts.Cancel();
        _typingTasks.Clear();

        _cts?.Cancel();
        DisposeWebSocket();

        if (_http is not null)
        {
            _http.Dispose();
            _http = null;
        }

        await Task.CompletedTask;
    }

    public override async Task SendAsync(OutboundMessage msg)
    {
        if (_http is null)
        {
            Logger?.LogWarning("Discord HTTP client not initialized");
            return;
        }

        var url = $"{DiscordApiBase}/channels/{msg.ChatId}/messages";
        var payload = new Dictionary<string, object> { ["content"] = msg.Content };

        if (msg.ReplyTo is not null)
        {
            payload["message_reference"] = new Dictionary<string, object> { ["message_id"] = msg.ReplyTo };
            payload["allowed_mentions"] = new Dictionary<string, object> { ["replied_user"] = false };
        }

        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _config.Token);
                    request.Content = new StringContent(
                        JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                    var response = await _http.SendAsync(request);

                    if ((int)response.StatusCode == 429)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(body);
                        var retryAfter = doc.RootElement.TryGetProperty("retry_after", out var ra)
                            ? ra.GetDouble() : 1.0;
                        Logger?.LogWarning("Discord rate limited, retrying in {Seconds}s", retryAfter);
                        await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                    return;
                }
                catch (Exception e) when (attempt < 2)
                {
                    Logger?.LogWarning(e, "Discord send attempt {Attempt} failed, retrying...", attempt + 1);
                    await Task.Delay(1000);
                }
            }
        }
        finally
        {
            StopTyping(msg.ChatId);
        }
    }

    // ─── Gateway ───────────────────────────────────────────────────

    private async Task GatewayLoopAsync()
    {
        if (_ws is null) return;

        var buffer = new byte[16384];

        while (Running && _ws.State == WebSocketState.Open && !(_cts?.Token.IsCancellationRequested ?? true))
        {
            try
            {
                var result = await ReceiveFullMessageAsync(buffer);
                if (result is null) break;

                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;

                var op = root.TryGetProperty("op", out var opEl) ? opEl.GetInt32() : -1;
                var eventType = root.TryGetProperty("t", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : null;
                var seq = root.TryGetProperty("s", out var sEl) && sEl.ValueKind == JsonValueKind.Number ? sEl.GetInt32() : (int?)null;
                var payload = root.TryGetProperty("d", out var dEl) ? dEl : default;

                if (seq is not null)
                    _seq = seq;

                switch (op)
                {
                    case 10: // HELLO
                        var intervalMs = payload.TryGetProperty("heartbeat_interval", out var hbi) ? hbi.GetInt32() : 45000;
                        StartHeartbeat(intervalMs);
                        await IdentifyAsync();
                        break;
                    case 0 when eventType == "READY":
                        Logger?.LogInformation("Discord gateway READY");
                        break;
                    case 0 when eventType == "MESSAGE_CREATE":
                        await HandleMessageCreateAsync(payload);
                        break;
                    case 7: // RECONNECT
                        Logger?.LogInformation("Discord gateway requested reconnect");
                        return;
                    case 9: // INVALID_SESSION
                        Logger?.LogWarning("Discord gateway invalid session");
                        return;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch (Exception e)
            {
                Logger?.LogWarning(e, "Error in Discord gateway loop");
            }
        }
    }

    /// <summary>Receive a complete WebSocket message (may span multiple frames).</summary>
    private async Task<string?> ReceiveFullMessageAsync(byte[] buffer)
    {
        if (_ws is null || _cts is null) return null;

        var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(buffer, _cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task IdentifyAsync()
    {
        var identify = new
        {
            op = 2,
            d = new
            {
                token = _config.Token,
                intents = _config.Intents,
                properties = new
                {
                    os = "sharpbot",
                    browser = "sharpbot",
                    device = "sharpbot",
                },
            },
        };

        await SendJsonAsync(identify);
    }

    private void StartHeartbeat(int intervalMs)
    {
        // Cancel any existing heartbeat
        _heartbeatTask = Task.Run(async () =>
        {
            while (Running && _ws?.State == WebSocketState.Open && !(_cts?.Token.IsCancellationRequested ?? true))
            {
                var payload = new { op = 1, d = _seq };
                try
                {
                    await SendJsonAsync(payload);
                }
                catch (Exception e)
                {
                    Logger?.LogWarning(e, "Discord heartbeat failed");
                    break;
                }

                try { await Task.Delay(intervalMs, _cts!.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    private async Task HandleMessageCreateAsync(JsonElement payload)
    {
        // Skip bot messages
        if (payload.TryGetProperty("author", out var author) &&
            author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True)
            return;

        var senderId = author.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var channelId = payload.TryGetProperty("channel_id", out var chEl) ? chEl.GetString() ?? "" : "";
        var content = payload.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(senderId) || string.IsNullOrEmpty(channelId))
            return;

        if (!IsAllowed(senderId, _config.AllowFrom))
            return;

        var contentParts = new List<string>();
        if (!string.IsNullOrEmpty(content))
            contentParts.Add(content);

        var mediaPaths = new List<string>();
        var mediaAssetIds = new List<string>();
        var mediaDir = Utils.Helpers.GetMediaPath();

        // Handle attachments
        if (payload.TryGetProperty("attachments", out var attachments) &&
            attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                var url = attachment.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                var filename = attachment.TryGetProperty("filename", out var fnEl) ? fnEl.GetString() ?? "attachment" : "attachment";
                var size = attachment.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
                var attachId = attachment.TryGetProperty("id", out var aidEl) ? aidEl.GetString() ?? "file" : "file";
                var mimeType = attachment.TryGetProperty("content_type", out var mtEl) ? mtEl.GetString() ?? "" : "";

                if (url is null || _http is null) continue;

                if (size > MaxAttachmentBytes)
                {
                    var overLimitAsset = _mediaPipeline?.RegisterInbound(new MediaIngestRequest
                    {
                        Channel = ChannelName,
                        ChatId = channelId,
                        MimeType = mimeType,
                        FileName = filename,
                        SizeBytes = size,
                        SourceType = "discord",
                        SourceRef = attachId,
                        ItemCountInMessage = attachments.GetArrayLength(),
                        Metadata = new Dictionary<string, string>
                        {
                            ["message_id"] = payload.TryGetProperty("id", out var pId) ? pId.GetString() ?? "" : "",
                            ["download_status"] = "skipped_too_large",
                            ["failure_code"] = "MEDIA_ATTACHMENT_TOO_LARGE",
                        },
                    }, actor: "channel/discord");
                    if (overLimitAsset is not null)
                        mediaAssetIds.Add(overLimitAsset.Id);
                    contentParts.Add($"[attachment: {filename} - too large]");
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(mediaDir);
                    var safeName = filename.Replace('/', '_').Replace('\\', '_');
                    var filePath = Path.Combine(mediaDir, $"{attachId}_{safeName}");
                    var resp = await _http.GetAsync(url);
                    resp.EnsureSuccessStatusCode();
                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                    await System.IO.File.WriteAllBytesAsync(filePath, bytes);

                    var asset = _mediaPipeline?.RegisterInbound(new MediaIngestRequest
                    {
                        Channel = ChannelName,
                        ChatId = channelId,
                        MimeType = string.IsNullOrWhiteSpace(mimeType)
                            ? (resp.Content.Headers.ContentType?.MediaType ?? "")
                            : mimeType,
                        FileName = filename,
                        SizeBytes = size > 0 ? size : bytes.LongLength,
                        SourceType = "discord",
                        SourceRef = attachId,
                        LocalPath = filePath,
                        ItemCountInMessage = attachments.GetArrayLength(),
                        Metadata = new Dictionary<string, string>
                        {
                            ["message_id"] = payload.TryGetProperty("id", out var pId) ? pId.GetString() ?? "" : "",
                        },
                    }, actor: "channel/discord");

                    mediaPaths.Add(filePath);
                    if (asset is not null)
                    {
                        mediaAssetIds.Add(asset.Id);
                        contentParts.Add($"[attachment: {filePath}] [media_asset_id: {asset.Id}]");
                    }
                    else
                    {
                        contentParts.Add($"[attachment: {filePath}]");
                    }
                }
                catch (Exception e)
                {
                    Logger?.LogWarning(e, "Failed to download Discord attachment");
                    var failedAsset = _mediaPipeline?.RegisterInbound(new MediaIngestRequest
                    {
                        Channel = ChannelName,
                        ChatId = channelId,
                        MimeType = mimeType,
                        FileName = filename,
                        SizeBytes = size,
                        SourceType = "discord",
                        SourceRef = attachId,
                        ItemCountInMessage = attachments.GetArrayLength(),
                        Metadata = new Dictionary<string, string>
                        {
                            ["message_id"] = payload.TryGetProperty("id", out var pId) ? pId.GetString() ?? "" : "",
                            ["download_status"] = "failed",
                            ["failure_code"] = "MEDIA_DOWNLOAD_FAILED",
                        },
                    }, actor: "channel/discord");
                    if (failedAsset is not null)
                    {
                        mediaAssetIds.Add(failedAsset.Id);
                        contentParts.Add($"[attachment: {filename} - download failed] [media_asset_id: {failedAsset.Id}]");
                    }
                    else
                    {
                        contentParts.Add($"[attachment: {filename} - download failed]");
                    }
                }
            }
        }

        // Extract reply_to
        string? replyTo = null;
        if (payload.TryGetProperty("referenced_message", out var refMsg) &&
            refMsg.ValueKind == JsonValueKind.Object)
        {
            replyTo = refMsg.TryGetProperty("id", out var refIdEl) ? refIdEl.GetString() : null;
        }

        StartTyping(channelId);

        var messageId = payload.TryGetProperty("id", out var msgIdEl) ? msgIdEl.GetString() ?? "" : "";
        var guildId = payload.TryGetProperty("guild_id", out var guildEl) && guildEl.ValueKind == JsonValueKind.String
            ? (object?)guildEl.GetString() : null;

        var metadata = new Dictionary<string, object>
        {
            ["message_id"] = messageId,
        };
        if (mediaAssetIds.Count > 0) metadata["media_asset_ids"] = mediaAssetIds;
        if (guildId is not null) metadata["guild_id"] = guildId;
        if (replyTo is not null) metadata["reply_to"] = replyTo;

        await HandleMessageAsync(
            senderId: senderId,
            chatId: channelId,
            content: contentParts.Count > 0 ? string.Join("\n", contentParts) : "[empty message]",
            allowList: _config.AllowFrom,
            media: mediaPaths,
            metadata: metadata);
    }

    // ─── Typing Indicators ─────────────────────────────────────────

    private void StartTyping(string channelId)
    {
        StopTyping(channelId);

        var typingCts = new CancellationTokenSource();
        _typingTasks[channelId] = typingCts;

        _ = Task.Run(async () =>
        {
            var url = $"{DiscordApiBase}/channels/{channelId}/typing";
            while (Running && !typingCts.Token.IsCancellationRequested && _http is not null)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _config.Token);
                    await _http.SendAsync(request);
                }
                catch { /* ignore typing errors */ }

                try { await Task.Delay(8000, typingCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }, typingCts.Token);
    }

    private void StopTyping(string channelId)
    {
        if (_typingTasks.Remove(channelId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private async Task SendJsonAsync(object payload)
    {
        if (_ws is null || _ws.State != WebSocketState.Open || _cts is null) return;

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
    }

    private void DisposeWebSocket()
    {
        if (_ws is null) return;
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None)
                    .GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
        _ws.Dispose();
        _ws = null;
    }

    public void Dispose()
    {
        foreach (var (_, cts) in _typingTasks)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _typingTasks.Clear();
        _cts?.Cancel();
        _cts?.Dispose();
        DisposeWebSocket();
        _http?.Dispose();
    }
}
