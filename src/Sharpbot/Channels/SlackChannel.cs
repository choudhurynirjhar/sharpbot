using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sharpbot.Bus;
using Sharpbot.Config;
using Sharpbot.Media;

namespace Sharpbot.Channels;

/// <summary>
/// Slack channel implementation supporting Socket Mode (WebSocket) for receiving
/// and REST API for sending messages. Inspired by OpenClaw's Slack integration.
///
/// Socket Mode: uses app-level token to open a WebSocket connection — no public URL required.
/// HTTP Mode: uses Slack Events API with signing secret verification.
/// </summary>
public sealed class SlackChannel : BaseChannel, IDisposable
{
    public override string ChannelName => "slack";

    private const string SlackApiBase = "https://slack.com/api";
    private const int MaxRetries = 3;
    private const int RetryBaseDelayMs = 500;

    private readonly SlackConfig _config;
    private readonly MediaPipelineService? _mediaPipeline;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private HttpClient? _http;
    private string? _botUserId;

    // Track thread context: chatId -> thread_ts for reply threading
    private readonly Dictionary<string, string> _threadMap = [];

    public SlackChannel(
        SlackConfig config,
        MessageBus bus,
        MediaPipelineService? mediaPipeline = null,
        ILogger? logger = null)
        : base(config, bus, logger)
    {
        _config = config;
        _mediaPipeline = mediaPipeline;
    }

    // ─── Lifecycle ──────────────────────────────────────────────────

    public override async Task StartAsync()
    {
        if (string.IsNullOrEmpty(_config.BotToken))
        {
            Logger?.LogError("Slack bot token not configured");
            return;
        }

        Running = true;
        _cts = new CancellationTokenSource();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Verify the bot token and get our user ID
        await AuthTestAsync();

        if (_config.Mode.Equals("socket", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(_config.AppToken))
            {
                Logger?.LogError("Slack app token (xapp-...) required for Socket Mode");
                Running = false;
                return;
            }

            await RunSocketModeAsync();
        }
        else
        {
            // HTTP mode — the webhook endpoint is registered separately in Program.cs
            // We just stay alive and wait for events to be pushed via HandleHttpEventAsync
            Logger?.LogInformation("Slack channel running in HTTP Events API mode on {Path}", _config.WebhookPath);
            try
            {
                await Task.Delay(Timeout.Infinite, _cts.Token);
            }
            catch (OperationCanceledException) { }
        }
    }

    public override async Task StopAsync()
    {
        Running = false;
        _cts?.Cancel();
        DisposeWebSocket();

        if (_http is not null)
        {
            _http.Dispose();
            _http = null;
        }

        await Task.CompletedTask;
    }

    // ─── Sending Messages ───────────────────────────────────────────

    public override async Task SendAsync(OutboundMessage msg)
    {
        if (_http is null)
        {
            Logger?.LogWarning("Slack HTTP client not initialized");
            return;
        }

        var channelId = msg.ChatId;
        string? threadTs = null;

        // Determine thread context
        if (_config.ReplyInThread.Equals("always", StringComparison.OrdinalIgnoreCase))
        {
            // Use the message's ReplyTo as thread_ts, or check our thread map
            if (!string.IsNullOrEmpty(msg.ReplyTo))
                threadTs = msg.ReplyTo;
            else if (msg.Metadata.TryGetValue("thread_ts", out var ts))
                threadTs = ts?.ToString();
            else if (_threadMap.TryGetValue(channelId, out var cached))
                threadTs = cached;
        }

        // Chunk long messages (Slack limit ~4000 chars)
        var chunks = ChunkText(msg.Content, _config.TextChunkLimit);

        foreach (var chunk in chunks)
        {
            await PostMessageWithRetryAsync(channelId, chunk, threadTs);
        }
    }

    /// <summary>Post a message to Slack with exponential backoff retry.</summary>
    private async Task<JsonElement?> PostMessageWithRetryAsync(string channel, string text, string? threadTs = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["channel"] = channel,
            ["text"] = text,
        };
        if (!string.IsNullOrEmpty(threadTs))
            payload["thread_ts"] = threadTs;

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var result = await SlackApiCallAsync("chat.postMessage", payload);
                if (result is not null &&
                    result.Value.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    return result;
                }

                var error = result?.TryGetProperty("error", out var errEl) == true
                    ? errEl.GetString() : "unknown";
                Logger?.LogWarning("Slack chat.postMessage failed: {Error}", error);

                if (error == "ratelimited")
                {
                    var delay = (int)(RetryBaseDelayMs * Math.Pow(2, attempt));
                    await Task.Delay(delay);
                    continue;
                }

                return result;
            }
            catch (Exception e) when (attempt < MaxRetries - 1)
            {
                Logger?.LogWarning(e, "Slack send attempt {Attempt} failed, retrying...", attempt + 1);
                await Task.Delay((int)(RetryBaseDelayMs * Math.Pow(2, attempt)));
            }
        }

        return null;
    }

    // ─── Socket Mode ────────────────────────────────────────────────

    /// <summary>Run the Socket Mode WebSocket loop with automatic reconnection.</summary>
    private async Task RunSocketModeAsync()
    {
        while (Running && !(_cts?.Token.IsCancellationRequested ?? true))
        {
            try
            {
                var wssUrl = await OpenSocketConnectionAsync();
                if (wssUrl is null)
                {
                    Logger?.LogError("Failed to obtain Slack Socket Mode WebSocket URL");
                    await Task.Delay(5000, _cts!.Token);
                    continue;
                }

                Logger?.LogInformation("Connecting to Slack Socket Mode...");
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri(wssUrl), _cts!.Token);
                Logger?.LogInformation("Slack Socket Mode connected");

                await SocketModeLoopAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                Logger?.LogWarning(e, "Slack Socket Mode error");
                if (Running)
                {
                    Logger?.LogInformation("Reconnecting to Slack Socket Mode in 5 seconds...");
                    try { await Task.Delay(5000, _cts!.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                DisposeWebSocket();
            }
        }
    }

    /// <summary>Call apps.connections.open to get a WebSocket URL for Socket Mode.</summary>
    private async Task<string?> OpenSocketConnectionAsync()
    {
        if (_http is null) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{SlackApiBase}/apps.connections.open");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AppToken);
            request.Content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");

            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean() &&
                root.TryGetProperty("url", out var url))
            {
                return url.GetString();
            }

            var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : "unknown";
            Logger?.LogError("apps.connections.open failed: {Error}", error);
            return null;
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Failed to open Slack Socket Mode connection");
            return null;
        }
    }

    /// <summary>Main Socket Mode WebSocket receive loop.</summary>
    private async Task SocketModeLoopAsync()
    {
        if (_ws is null || _cts is null) return;

        var buffer = new byte[65536]; // Slack envelopes can be large

        while (Running && _ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                var message = await ReceiveFullMessageAsync(buffer);
                if (message is null) break;

                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                // Socket Mode envelopes have: type, envelope_id, payload, accepts_response_payload
                var envelopeType = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                var envelopeId = root.TryGetProperty("envelope_id", out var envEl) ? envEl.GetString() : null;

                // Acknowledge the envelope immediately (Slack requires this within 3 seconds)
                if (!string.IsNullOrEmpty(envelopeId))
                    await AcknowledgeEnvelopeAsync(envelopeId);

                switch (envelopeType)
                {
                    case "hello":
                        Logger?.LogInformation("Slack Socket Mode handshake complete");
                        break;

                    case "disconnect":
                        Logger?.LogInformation("Slack Socket Mode disconnect requested, reconnecting...");
                        return; // Break out to trigger reconnect

                    case "events_api":
                        if (root.TryGetProperty("payload", out var payload))
                            await HandleEventPayloadAsync(payload);
                        break;

                    case "slash_commands":
                        Logger?.LogDebug("Slack slash command received (not handled)");
                        break;

                    case "interactive":
                        Logger?.LogDebug("Slack interactive event received (not handled)");
                        break;

                    default:
                        Logger?.LogDebug("Unknown Slack envelope type: {Type}", envelopeType);
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch (Exception e)
            {
                Logger?.LogWarning(e, "Error in Slack Socket Mode loop");
            }
        }
    }

    /// <summary>Acknowledge a Socket Mode envelope by sending back its envelope_id.</summary>
    private async Task AcknowledgeEnvelopeAsync(string envelopeId)
    {
        if (_ws is null || _ws.State != WebSocketState.Open || _cts is null) return;

        var ack = JsonSerializer.Serialize(new { envelope_id = envelopeId });
        var bytes = Encoding.UTF8.GetBytes(ack);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (Exception e)
        {
            Logger?.LogWarning(e, "Failed to acknowledge Slack envelope {EnvelopeId}", envelopeId);
        }
    }

    /// <summary>Receive a complete WebSocket message (may span multiple frames).</summary>
    private async Task<string?> ReceiveFullMessageAsync(byte[] buffer)
    {
        if (_ws is null || _cts is null) return null;

        using var ms = new MemoryStream();
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

    // ─── HTTP Events API Mode ───────────────────────────────────────

    /// <summary>Handle an incoming HTTP event from Slack Events API. Called by the webhook endpoint.</summary>
    public async Task<object?> HandleHttpEventAsync(string body, string? signature, string? timestamp)
    {
        // Verify signature
        if (!string.IsNullOrEmpty(_config.SigningSecret))
        {
            if (!VerifySlackSignature(body, signature, timestamp))
            {
                Logger?.LogWarning("Slack signature verification failed");
                return new { error = "invalid_signature" };
            }
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

        // URL verification challenge
        if (type == "url_verification")
        {
            var challenge = root.TryGetProperty("challenge", out var ch) ? ch.GetString() : "";
            return new { challenge };
        }

        // Event callback
        if (type == "event_callback")
        {
            await HandleEventPayloadAsync(root);
            return new { ok = true };
        }

        return new { ok = true };
    }

    /// <summary>Verify Slack request signature using HMAC-SHA256.</summary>
    private bool VerifySlackSignature(string body, string? signature, string? timestamp)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp))
            return false;

        // Check timestamp isn't too old (5 minutes)
        if (long.TryParse(timestamp, out var ts))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - ts) > 300) return false;
        }

        var baseString = $"v0:{timestamp}:{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.SigningSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString));
        var computed = "v0=" + Convert.ToHexStringLower(hash);

        return string.Equals(computed, signature, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Event Processing ───────────────────────────────────────────

    /// <summary>Process an event payload from either Socket Mode or HTTP Events API.</summary>
    private async Task HandleEventPayloadAsync(JsonElement payload)
    {
        // Socket Mode wraps in payload.event; HTTP Events API has event at top level
        JsonElement eventEl;
        if (payload.TryGetProperty("event", out var ev))
            eventEl = ev;
        else
            eventEl = payload;

        var eventType = eventEl.TryGetProperty("type", out var etEl) ? etEl.GetString() : null;

        switch (eventType)
        {
            case "message":
                await HandleMessageEventAsync(eventEl);
                break;

            case "app_mention":
                await HandleMessageEventAsync(eventEl);
                break;

            default:
                Logger?.LogDebug("Unhandled Slack event type: {Type}", eventType);
                break;
        }
    }

    /// <summary>Handle a Slack message or app_mention event.</summary>
    private async Task HandleMessageEventAsync(JsonElement ev)
    {
        // Skip bot messages, message_changed, etc.
        var subtype = ev.TryGetProperty("subtype", out var stEl) ? stEl.GetString() : null;
        if (subtype is not null && subtype != "file_share") return;

        // Skip messages from our own bot
        var botId = ev.TryGetProperty("bot_id", out var biEl) ? biEl.GetString() : null;
        if (!string.IsNullOrEmpty(botId)) return;

        var userId = ev.TryGetProperty("user", out var uEl) ? uEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(userId)) return;

        // Skip our own messages
        if (userId == _botUserId) return;

        var channelId = ev.TryGetProperty("channel", out var chEl) ? chEl.GetString() ?? "" : "";
        var text = ev.TryGetProperty("text", out var txtEl) ? txtEl.GetString() ?? "" : "";
        var messageTs = ev.TryGetProperty("ts", out var tsEl) ? tsEl.GetString() ?? "" : "";
        var threadTs = ev.TryGetProperty("thread_ts", out var ttEl) ? ttEl.GetString() : null;

        if (string.IsNullOrEmpty(channelId)) return;

        // Strip bot mention from text (e.g., <@U12345> hello → hello)
        if (!string.IsNullOrEmpty(_botUserId))
            text = System.Text.RegularExpressions.Regex.Replace(text, $@"<@{_botUserId}>\s*", "").Trim();

        if (string.IsNullOrEmpty(text)) return;

        // Build content parts
        var contentParts = new List<string> { text };
        var mediaPaths = new List<string>();
        var mediaAssetIds = new List<string>();

        // Handle file attachments
        if (ev.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            var mediaDir = Utils.Helpers.GetMediaPath();
            foreach (var file in files.EnumerateArray())
            {
                var urlPrivate = file.TryGetProperty("url_private_download", out var urlEl)
                    ? urlEl.GetString()
                    : file.TryGetProperty("url_private", out var urlEl2) ? urlEl2.GetString() : null;
                var filename = file.TryGetProperty("name", out var fnEl) ? fnEl.GetString() ?? "file" : "file";
                var fileId = file.TryGetProperty("id", out var fidEl) ? fidEl.GetString() ?? "file" : "file";
                var mimeType = file.TryGetProperty("mimetype", out var mtEl) ? mtEl.GetString() ?? "" : "";
                var sizeBytes = file.TryGetProperty("size", out var szEl) && szEl.TryGetInt64(out var parsedSize)
                    ? parsedSize : 0;

                if (urlPrivate is null || _http is null) continue;

                try
                {
                    Directory.CreateDirectory(mediaDir);
                    var safeName = filename.Replace('/', '_').Replace('\\', '_');
                    var filePath = Path.Combine(mediaDir, $"{fileId}_{safeName}");

                    using var req = new HttpRequestMessage(HttpMethod.Get, urlPrivate);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.BotToken);
                    var resp = await _http.SendAsync(req);
                    resp.EnsureSuccessStatusCode();
                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(filePath, bytes);

                    var effectiveSize = sizeBytes > 0 ? sizeBytes : bytes.LongLength;
                    var asset = _mediaPipeline?.RegisterInbound(new MediaIngestRequest
                    {
                        Channel = ChannelName,
                        ChatId = channelId,
                        MimeType = mimeType,
                        FileName = filename,
                        SizeBytes = effectiveSize,
                        SourceType = "slack",
                        SourceRef = fileId,
                        LocalPath = filePath,
                        ItemCountInMessage = files.GetArrayLength(),
                        Metadata = new Dictionary<string, string>
                        {
                            ["message_ts"] = messageTs,
                            ["thread_ts"] = threadTs ?? messageTs,
                            ["slack_file_id"] = fileId,
                        },
                    }, actor: "channel/slack");

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
                    Logger?.LogWarning(e, "Failed to download Slack file attachment");
                    var asset = _mediaPipeline?.RegisterInbound(new MediaIngestRequest
                    {
                        Channel = ChannelName,
                        ChatId = channelId,
                        MimeType = mimeType,
                        FileName = filename,
                        SizeBytes = sizeBytes,
                        SourceType = "slack",
                        SourceRef = fileId,
                        ItemCountInMessage = files.GetArrayLength(),
                        Metadata = new Dictionary<string, string>
                        {
                            ["message_ts"] = messageTs,
                            ["thread_ts"] = threadTs ?? messageTs,
                            ["slack_file_id"] = fileId,
                            ["download_status"] = "failed",
                            ["failure_code"] = "MEDIA_DOWNLOAD_FAILED",
                        },
                    }, actor: "channel/slack");

                    if (asset is not null)
                    {
                        mediaAssetIds.Add(asset.Id);
                        contentParts.Add($"[attachment: {filename} - download failed] [media_asset_id: {asset.Id}]");
                    }
                    else
                    {
                        contentParts.Add($"[attachment: {filename} - download failed]");
                    }
                }
            }
        }

        // Determine reply-to thread
        var replyToTs = threadTs ?? messageTs;

        // Cache thread context for outbound replies
        _threadMap[channelId] = replyToTs;

        // Build metadata
        var metadata = new Dictionary<string, object>
        {
            ["message_ts"] = messageTs,
            ["thread_ts"] = replyToTs,
        };
        if (!string.IsNullOrEmpty(threadTs))
            metadata["is_thread_reply"] = true;
        if (mediaAssetIds.Count > 0)
            metadata["media_asset_ids"] = mediaAssetIds;

        // Resolve user display name
        var displayName = await GetUserDisplayNameAsync(userId);
        if (!string.IsNullOrEmpty(displayName))
            metadata["sender_name"] = displayName;

        await HandleMessageAsync(
            senderId: userId,
            chatId: channelId,
            content: string.Join("\n", contentParts),
            allowList: _config.AllowFrom,
            media: mediaPaths,
            metadata: metadata);
    }

    // ─── Slack API Helpers ──────────────────────────────────────────

    /// <summary>Call a Slack Web API method with JSON body.</summary>
    private async Task<JsonElement?> SlackApiCallAsync(string method, object? body = null)
    {
        if (_http is null) return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SlackApiBase}/{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.BotToken);

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.Clone();
    }

    /// <summary>Verify the bot token and cache our user ID.</summary>
    private async Task AuthTestAsync()
    {
        try
        {
            var result = await SlackApiCallAsync("auth.test");
            if (result is not null &&
                result.Value.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                _botUserId = result.Value.TryGetProperty("user_id", out var uid) ? uid.GetString() : null;
                var team = result.Value.TryGetProperty("team", out var t) ? t.GetString() : "unknown";
                Logger?.LogInformation("Slack auth verified — bot user {UserId} on team {Team}", _botUserId, team);
            }
            else
            {
                var error = result?.TryGetProperty("error", out var errEl) == true ? errEl.GetString() : "unknown";
                Logger?.LogError("Slack auth.test failed: {Error}", error);
            }
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Slack auth.test failed");
        }
    }

    /// <summary>Get a user's display name.</summary>
    private async Task<string?> GetUserDisplayNameAsync(string userId)
    {
        try
        {
            var result = await SlackApiCallAsync("users.info", new { user = userId });
            if (result is not null &&
                result.Value.TryGetProperty("ok", out var ok) && ok.GetBoolean() &&
                result.Value.TryGetProperty("user", out var user))
            {
                if (user.TryGetProperty("profile", out var profile))
                {
                    var displayName = profile.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
                    if (!string.IsNullOrEmpty(displayName)) return displayName;

                    var realName = profile.TryGetProperty("real_name", out var rn) ? rn.GetString() : null;
                    if (!string.IsNullOrEmpty(realName)) return realName;
                }

                return user.TryGetProperty("name", out var name) ? name.GetString() : null;
            }
        }
        catch (Exception e)
        {
            Logger?.LogDebug(e, "Failed to get Slack user info for {UserId}", userId);
        }

        return null;
    }

    // ─── Utilities ──────────────────────────────────────────────────

    /// <summary>Split text into chunks respecting the Slack message size limit.</summary>
    private static List<string> ChunkText(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return [""];
        if (text.Length <= maxLen) return [text];

        var chunks = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxLen)
            {
                chunks.Add(remaining.ToString());
                break;
            }

            // Try to break at a newline
            var slice = remaining[..maxLen];
            var breakAt = slice.LastIndexOf('\n');
            if (breakAt < maxLen / 2) breakAt = maxLen; // no good break point

            chunks.Add(remaining[..breakAt].ToString());
            remaining = remaining[breakAt..].TrimStart('\n');
        }

        return chunks;
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
        _cts?.Cancel();
        _cts?.Dispose();
        DisposeWebSocket();
        _http?.Dispose();
    }
}
