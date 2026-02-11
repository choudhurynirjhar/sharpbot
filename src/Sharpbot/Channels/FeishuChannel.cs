using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sharpbot.Bus;
using Sharpbot.Config;

namespace Sharpbot.Channels;

/// <summary>
/// Feishu/Lark channel implementation using REST API for sending and
/// a callback webhook for receiving messages.
/// Ported from nanobot's Python Feishu channel.
///
/// Requires:
/// - App ID and App Secret from Feishu Open Platform
/// - Bot capability enabled
/// - Event subscription enabled (im.message.receive_v1)
/// </summary>
public sealed class FeishuChannel : BaseChannel, IDisposable
{
    public override string ChannelName => "feishu";

    private const string FeishuApiBase = "https://open.feishu.cn/open-apis";

    /// <summary>Message type display mapping.</summary>
    private static readonly Dictionary<string, string> MsgTypeMap = new()
    {
        ["image"] = "[image]",
        ["audio"] = "[audio]",
        ["file"] = "[file]",
        ["sticker"] = "[sticker]",
    };

    private readonly FeishuConfig _config;
    private HttpClient? _http;
    private CancellationTokenSource? _cts;

    // Token management
    private string? _tenantAccessToken;
    private DateTime _tokenExpiresAt = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    // Message deduplication (ordered — trim when > 1000)
    private readonly ConcurrentDictionary<string, byte> _processedMessageIds = new();
    private readonly ConcurrentQueue<string> _messageIdQueue = new();
    private const int MaxDeduplicateSize = 1000;

    /// <summary>Regex for markdown tables.</summary>
    private static readonly Regex TableRegex = new(
        @"((?:^[ \t]*\|.+\|[ \t]*\n)(?:^[ \t]*\|[-:\s|]+\|[ \t]*\n)(?:^[ \t]*\|.+\|[ \t]*\n?)+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public FeishuChannel(FeishuConfig config, MessageBus bus, ILogger? logger = null)
        : base(config, bus, logger)
    {
        _config = config;
    }

    public override async Task StartAsync()
    {
        if (string.IsNullOrEmpty(_config.AppId) || string.IsNullOrEmpty(_config.AppSecret))
        {
            Logger?.LogError("Feishu app_id and app_secret not configured");
            return;
        }

        Running = true;
        _cts = new CancellationTokenSource();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Verify credentials by getting access token
        try
        {
            await RefreshAccessTokenAsync();
            Logger?.LogInformation("Feishu bot started. Configure a webhook to receive messages " +
                                   "or POST events to the /api/feishu/events endpoint");
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Failed to get Feishu access token");
            Running = false;
            return;
        }

        // Keep running until stopped
        try
        {
            while (Running && !_cts.Token.IsCancellationRequested)
                await Task.Delay(1000, _cts.Token);
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync()
    {
        Running = false;
        _cts?.Cancel();
        _http?.Dispose();
        _http = null;
        Logger?.LogInformation("Feishu bot stopped");
        await Task.CompletedTask;
    }

    public override async Task SendAsync(OutboundMessage msg)
    {
        if (_http is null)
        {
            Logger?.LogWarning("Feishu client not initialized");
            return;
        }

        try
        {
            await EnsureValidTokenAsync();

            // Determine receive_id_type based on chat_id format
            var receiveIdType = msg.ChatId.StartsWith("oc_") ? "chat_id" : "open_id";

            // Build interactive card with markdown + table support
            var elements = BuildCardElements(msg.Content);
            var card = new Dictionary<string, object>
            {
                ["config"] = new Dictionary<string, object> { ["wide_screen_mode"] = true },
                ["elements"] = elements,
            };

            var body = new Dictionary<string, object>
            {
                ["receive_id"] = msg.ChatId,
                ["msg_type"] = "interactive",
                ["content"] = JsonSerializer.Serialize(card),
            };

            var url = $"{FeishuApiBase}/im/v1/messages?receive_id_type={receiveIdType}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tenantAccessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger?.LogError("Failed to send Feishu message: {Status} {Body}",
                    response.StatusCode, responseBody);
            }
            else
            {
                Logger?.LogDebug("Feishu message sent to {ChatId}", msg.ChatId);
            }
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Error sending Feishu message");
        }
    }

    // ─── Event Handling (called from webhook endpoint) ──────────────

    /// <summary>
    /// Handle an incoming event from the Feishu webhook.
    /// Call this from an API endpoint that receives Feishu event callbacks.
    /// </summary>
    public async Task<string?> HandleEventAsync(JsonElement eventData)
    {
        // Handle URL verification challenge
        if (eventData.TryGetProperty("challenge", out var challenge))
        {
            return JsonSerializer.Serialize(new { challenge = challenge.GetString() });
        }

        // Handle v2 event schema
        if (!eventData.TryGetProperty("event", out var eventEl))
            return null;

        // Only handle im.message.receive_v1
        var headerType = eventData.TryGetProperty("header", out var header) &&
                         header.TryGetProperty("event_type", out var evType)
            ? evType.GetString() : null;

        if (headerType != "im.message.receive_v1")
            return null;

        try
        {
            var message = eventEl.GetProperty("message");
            var sender = eventEl.GetProperty("sender");

            var messageId = message.GetProperty("message_id").GetString() ?? "";

            // Deduplication
            if (!_processedMessageIds.TryAdd(messageId, 0))
                return null;

            _messageIdQueue.Enqueue(messageId);
            while (_messageIdQueue.Count > MaxDeduplicateSize && _messageIdQueue.TryDequeue(out var old))
                _processedMessageIds.TryRemove(old, out _);

            // Skip bot messages
            var senderType = sender.TryGetProperty("sender_type", out var stEl) ? stEl.GetString() : null;
            if (senderType == "bot") return null;

            var senderId = sender.TryGetProperty("sender_id", out var sidEl) &&
                           sidEl.TryGetProperty("open_id", out var oidEl)
                ? oidEl.GetString() ?? "unknown" : "unknown";

            var chatId = message.TryGetProperty("chat_id", out var cidEl) ? cidEl.GetString() ?? "" : "";
            var chatType = message.TryGetProperty("chat_type", out var ctEl) ? ctEl.GetString() ?? "" : "";
            var msgType = message.TryGetProperty("message_type", out var mtEl) ? mtEl.GetString() ?? "" : "";

            // Add reaction to indicate "seen"
            _ = Task.Run(async () => await AddReactionAsync(messageId, "THUMBSUP"));

            // Parse message content
            string content;
            if (msgType == "text")
            {
                try
                {
                    var msgContent = message.GetProperty("content").GetString() ?? "";
                    using var contentDoc = JsonDocument.Parse(msgContent);
                    content = contentDoc.RootElement.TryGetProperty("text", out var textEl)
                        ? textEl.GetString() ?? "" : msgContent;
                }
                catch
                {
                    content = message.TryGetProperty("content", out var rawEl) ? rawEl.GetString() ?? "" : "";
                }
            }
            else
            {
                content = MsgTypeMap.GetValueOrDefault(msgType, $"[{msgType}]");
            }

            if (string.IsNullOrEmpty(content)) return null;

            // Forward to message bus
            var replyTo = chatType == "group" ? chatId : senderId;
            await HandleMessageAsync(
                senderId: senderId,
                chatId: replyTo,
                content: content,
                allowList: _config.AllowFrom,
                metadata: new Dictionary<string, object>
                {
                    ["message_id"] = messageId,
                    ["chat_type"] = chatType,
                    ["msg_type"] = msgType,
                });
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Error processing Feishu message");
        }

        return null;
    }

    // ─── Token Management ──────────────────────────────────────────

    private async Task EnsureValidTokenAsync()
    {
        if (_tenantAccessToken is not null && DateTime.UtcNow < _tokenExpiresAt)
            return;

        await RefreshAccessTokenAsync();
    }

    private async Task RefreshAccessTokenAsync()
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (_http is null) throw new InvalidOperationException("HTTP client not initialized");

            var body = new Dictionary<string, string>
            {
                ["app_id"] = _config.AppId,
                ["app_secret"] = _config.AppSecret,
            };

            var response = await _http.PostAsync(
                $"{FeishuApiBase}/auth/v3/tenant_access_token/internal",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : -1;
            if (code != 0)
            {
                var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : "unknown error";
                throw new InvalidOperationException($"Feishu token error (code={code}): {msg}");
            }

            _tenantAccessToken = root.GetProperty("tenant_access_token").GetString()
                ?? throw new InvalidOperationException("No tenant_access_token in response");
            var expire = root.TryGetProperty("expire", out var expEl) ? expEl.GetInt32() : 7200;
            _tokenExpiresAt = DateTime.UtcNow.AddSeconds(expire - 300); // Refresh 5 min early

            Logger?.LogDebug("Feishu access token refreshed (expires in {Seconds}s)", expire);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ─── Reactions ─────────────────────────────────────────────────

    private async Task AddReactionAsync(string messageId, string emojiType)
    {
        if (_http is null || _tenantAccessToken is null) return;

        try
        {
            await EnsureValidTokenAsync();

            var body = new
            {
                reaction_type = new { emoji_type = emojiType },
            };

            var url = $"{FeishuApiBase}/im/v1/messages/{messageId}/reactions";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tenantAccessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                Logger?.LogWarning("Failed to add Feishu reaction: {Status}", response.StatusCode);
            else
                Logger?.LogDebug("Added {Emoji} reaction to message {Id}", emojiType, messageId);
        }
        catch (Exception e)
        {
            Logger?.LogWarning(e, "Error adding Feishu reaction");
        }
    }

    // ─── Card Building (Markdown + Tables) ─────────────────────────

    private List<Dictionary<string, object>> BuildCardElements(string content)
    {
        var elements = new List<Dictionary<string, object>>();
        var lastEnd = 0;

        foreach (Match m in TableRegex.Matches(content))
        {
            var before = content[lastEnd..m.Index].Trim();
            if (!string.IsNullOrEmpty(before))
                elements.Add(new Dictionary<string, object> { ["tag"] = "markdown", ["content"] = before });

            var table = ParseMdTable(m.Groups[1].Value);
            elements.Add(table ?? new Dictionary<string, object> { ["tag"] = "markdown", ["content"] = m.Groups[1].Value });

            lastEnd = m.Index + m.Length;
        }

        var remaining = content[lastEnd..].Trim();
        if (!string.IsNullOrEmpty(remaining))
            elements.Add(new Dictionary<string, object> { ["tag"] = "markdown", ["content"] = remaining });

        return elements.Count > 0
            ? elements
            : [new Dictionary<string, object> { ["tag"] = "markdown", ["content"] = content }];
    }

    private static Dictionary<string, object>? ParseMdTable(string tableText)
    {
        var lines = tableText.Trim().Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();

        if (lines.Count < 3) return null;

        static string[] SplitRow(string line)
            => line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();

        var headers = SplitRow(lines[0]);
        var rows = lines.Skip(2).Select(SplitRow).ToList();

        var columns = headers.Select((h, i) => new Dictionary<string, object>
        {
            ["tag"] = "column",
            ["name"] = $"c{i}",
            ["display_name"] = h,
            ["width"] = "auto",
        }).ToList();

        var rowData = rows.Select(r =>
        {
            var row = new Dictionary<string, object>();
            for (var i = 0; i < headers.Length; i++)
                row[$"c{i}"] = i < r.Length ? r[i] : "";
            return row;
        }).ToList();

        return new Dictionary<string, object>
        {
            ["tag"] = "table",
            ["page_size"] = rows.Count + 1,
            ["columns"] = columns,
            ["rows"] = rowData,
        };
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _http?.Dispose();
        _tokenLock.Dispose();
    }
}
