using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sharpbot.Bus;
using Sharpbot.Config;
using Sharpbot.Media;

namespace Sharpbot.Channels;

/// <summary>
/// WhatsApp channel that connects to a Node.js bridge.
/// The bridge uses @whiskeysockets/baileys to handle the WhatsApp Web protocol.
/// Communication between .NET and Node.js is via WebSocket.
/// Ported from nanobot's Python WhatsApp channel.
/// </summary>
public sealed class WhatsAppChannel : BaseChannel, IDisposable
{
    public override string ChannelName => "whatsapp";

    private readonly WhatsAppConfig _config;
    private readonly MediaPipelineService? _mediaPipeline;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _connected;

    public WhatsAppChannel(
        WhatsAppConfig config,
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
        var bridgeUrl = _config.BridgeUrl;
        Logger?.LogInformation("Connecting to WhatsApp bridge at {Url}...", bridgeUrl);

        Running = true;
        _cts = new CancellationTokenSource();

        while (Running && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri(bridgeUrl), _cts.Token);
                _connected = true;
                Logger?.LogInformation("Connected to WhatsApp bridge");

                // Listen for messages
                await ReceiveLoopAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _connected = false;
                Logger?.LogWarning(e, "WhatsApp bridge connection error");

                if (Running)
                {
                    Logger?.LogInformation("Reconnecting to WhatsApp bridge in 5 seconds...");
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
        _connected = false;
        _cts?.Cancel();
        DisposeWebSocket();
        await Task.CompletedTask;
    }

    public override async Task SendAsync(OutboundMessage msg)
    {
        if (_ws is null || !_connected)
        {
            Logger?.LogWarning("WhatsApp bridge not connected");
            return;
        }

        try
        {
            var payload = new
            {
                type = "send",
                to = msg.ChatId,
                text = msg.Content,
            };
            await SendJsonAsync(payload);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Error sending WhatsApp message");
        }
    }

    // ─── Receive Loop ──────────────────────────────────────────────

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];

        while (Running && _ws?.State == WebSocketState.Open && !(_cts?.Token.IsCancellationRequested ?? true))
        {
            try
            {
                var message = await ReceiveFullMessageAsync(buffer);
                if (message is null) break;

                await HandleBridgeMessageAsync(message);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch (Exception e)
            {
                Logger?.LogError(e, "Error handling WhatsApp bridge message");
            }
        }
    }

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

    // ─── Bridge Message Handling ───────────────────────────────────

    private async Task HandleBridgeMessageAsync(string raw)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            Logger?.LogWarning("Invalid JSON from WhatsApp bridge: {Raw}", raw.Length > 100 ? raw[..100] : raw);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var msgType = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

            switch (msgType)
            {
                case "message":
                    await HandleIncomingMessageAsync(root);
                    break;

                case "status":
                    var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                    Logger?.LogInformation("WhatsApp status: {Status}", status);
                    _connected = status == "connected";
                    break;

                case "qr":
                    Logger?.LogInformation("Scan QR code in the WhatsApp bridge terminal to connect");
                    break;

                case "error":
                    var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : "unknown";
                    Logger?.LogError("WhatsApp bridge error: {Error}", error);
                    break;
            }
        }
    }

    private async Task HandleIncomingMessageAsync(JsonElement data)
    {
        // Phone number (deprecated format): <phone>@s.whatsapp.net
        var pn = data.TryGetProperty("pn", out var pnEl) ? pnEl.GetString() ?? "" : "";
        // New LID format
        var sender = data.TryGetProperty("sender", out var senderEl) ? senderEl.GetString() ?? "" : "";
        var content = data.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";

        // Extract the user ID
        var userId = !string.IsNullOrEmpty(pn) ? pn : sender;
        var senderId = userId.Contains('@') ? userId.Split('@')[0] : userId;
        var messageId = data.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

        // Voice messages not directly supported from bridge yet
        var mediaAssetIds = new List<string>();
        if (content == "[Voice Message]")
        {
            Logger?.LogInformation("Voice message received from {Sender}, transcription not available via bridge", senderId);
            content = "[Voice Message: Transcription not available for WhatsApp yet]";
            var asset = _mediaPipeline?.RegisterInbound(new MediaIngestRequest
            {
                Channel = ChannelName,
                ChatId = sender,
                MimeType = "audio/ogg",
                FileName = "voice-note.ogg",
                SizeBytes = 0,
                SourceType = "whatsapp",
                SourceRef = messageId ?? "",
                ItemCountInMessage = 1,
                Metadata = new Dictionary<string, string>
                {
                    ["sender_id"] = senderId,
                    ["download_status"] = "not_supported_bridge",
                    ["failure_code"] = "MEDIA_SOURCE_UNSUPPORTED",
                },
            }, actor: "channel/whatsapp");
            if (asset is not null)
            {
                mediaAssetIds.Add(asset.Id);
                content += $" [media_asset_id: {asset.Id}]";
            }
        }

        var timestamp = data.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number
            ? (object)tsEl.GetInt64() : null;
        var isGroup = data.TryGetProperty("isGroup", out var groupEl) && groupEl.ValueKind == JsonValueKind.True;

        var metadata = new Dictionary<string, object>();
        if (messageId is not null) metadata["message_id"] = messageId;
        if (timestamp is not null) metadata["timestamp"] = timestamp;
        metadata["is_group"] = isGroup;
        if (mediaAssetIds.Count > 0) metadata["media_asset_ids"] = mediaAssetIds;

        await HandleMessageAsync(
            senderId: senderId,
            chatId: sender, // Use full LID for replies
            content: content,
            allowList: _config.AllowFrom,
            metadata: metadata);
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
        _cts?.Cancel();
        _cts?.Dispose();
        DisposeWebSocket();
    }
}
