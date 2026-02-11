using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sharpbot.Bus;
using Sharpbot.Config;
using Sharpbot.Session;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Sharpbot.Channels;

/// <summary>
/// Telegram channel implementation using long polling.
/// Ported from nanobot's Python Telegram channel.
/// </summary>
public sealed class TelegramChannel : BaseChannel, IDisposable
{
    public override string ChannelName => "telegram";

    private readonly TelegramConfig _config;
    private readonly SessionManager? _sessionManager;
    private readonly string? _groqApiKey;
    private TelegramBotClient? _bot;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, CancellationTokenSource> _typingTasks = [];

    public TelegramChannel(
        TelegramConfig config,
        MessageBus bus,
        SessionManager? sessionManager = null,
        string? groqApiKey = null,
        ILogger? logger = null)
        : base(config, bus, logger)
    {
        _config = config;
        _sessionManager = sessionManager;
        _groqApiKey = groqApiKey;
    }

    public override async Task StartAsync()
    {
        if (string.IsNullOrEmpty(_config.Token))
        {
            Logger?.LogError("Telegram bot token not configured");
            return;
        }

        Running = true;
        _cts = new CancellationTokenSource();

        _bot = new TelegramBotClient(_config.Token, cancellationToken: _cts.Token);

        // Register event handlers
        _bot.OnMessage += OnMessage;
        _bot.OnError += OnError;

        // Register commands with Telegram
        try
        {
            await _bot.SetMyCommands([
                new BotCommand { Command = "start", Description = "Start the bot" },
                new BotCommand { Command = "reset", Description = "Reset conversation history" },
                new BotCommand { Command = "help", Description = "Show available commands" },
            ]);
            Logger?.LogDebug("Telegram bot commands registered");
        }
        catch (Exception e)
        {
            Logger?.LogWarning(e, "Failed to register Telegram bot commands");
        }

        // Drop pending updates
        try
        {
            await _bot.DropPendingUpdates();
        }
        catch (Exception e)
        {
            Logger?.LogWarning(e, "Failed to drop pending updates");
        }

        // Get bot info
        try
        {
            var me = await _bot.GetMe();
            Logger?.LogInformation("Telegram bot @{Username} connected (polling mode)", me.Username);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Failed to connect Telegram bot");
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

        // Cancel all typing indicators
        foreach (var (_, typingCts) in _typingTasks)
            typingCts.Cancel();
        _typingTasks.Clear();

        if (_bot is not null)
        {
            Logger?.LogInformation("Stopping Telegram bot...");
            _bot.OnMessage -= OnMessage;
            _bot.OnError -= OnError;
        }

        _cts?.Cancel();
        _bot = null;

        await Task.CompletedTask;
    }

    public override async Task SendAsync(OutboundMessage msg)
    {
        if (_bot is null)
        {
            Logger?.LogWarning("Telegram bot not running");
            return;
        }

        // Stop typing indicator for this chat
        StopTyping(msg.ChatId);

        try
        {
            var chatId = long.Parse(msg.ChatId);
            var htmlContent = MarkdownToTelegramHtml(msg.Content);
            await _bot.SendMessage(chatId, htmlContent, parseMode: ParseMode.Html);
        }
        catch (FormatException)
        {
            Logger?.LogError("Invalid Telegram chat_id: {ChatId}", msg.ChatId);
        }
        catch (Exception e)
        {
            // Fallback to plain text if HTML parsing fails
            Logger?.LogWarning(e, "HTML parse failed, falling back to plain text");
            try
            {
                var chatId = long.Parse(msg.ChatId);
                await _bot.SendMessage(chatId, msg.Content);
            }
            catch (Exception e2)
            {
                Logger?.LogError(e2, "Error sending Telegram message");
            }
        }
    }

    // ─── Event Handlers ────────────────────────────────────────────

    private async Task OnMessage(Message msg, UpdateType type)
    {
        if (msg.From is null) return;

        var user = msg.From;
        var chatId = msg.Chat.Id;

        // Handle commands
        if (msg.Text is not null)
        {
            if (msg.Text.StartsWith("/start"))
            {
                await OnStart(msg);
                return;
            }
            if (msg.Text.StartsWith("/reset"))
            {
                await OnReset(msg);
                return;
            }
            if (msg.Text.StartsWith("/help"))
            {
                await OnHelp(msg);
                return;
            }
        }

        // Build sender_id (numeric + username for allowlist compatibility)
        var senderId = user.Id.ToString();
        if (!string.IsNullOrEmpty(user.Username))
            senderId = $"{senderId}|{user.Username}";

        // Build content from text and/or media
        var contentParts = new List<string>();
        var mediaPaths = new List<string>();

        if (!string.IsNullOrEmpty(msg.Text))
            contentParts.Add(msg.Text);
        if (!string.IsNullOrEmpty(msg.Caption))
            contentParts.Add(msg.Caption);

        // Handle media files
        string? fileId = null;
        string mediaType = "";

        if (msg.Photo is { Length: > 0 })
        {
            fileId = msg.Photo[^1].FileId; // Largest photo
            mediaType = "image";
        }
        else if (msg.Voice is not null)
        {
            fileId = msg.Voice.FileId;
            mediaType = "voice";
        }
        else if (msg.Audio is not null)
        {
            fileId = msg.Audio.FileId;
            mediaType = "audio";
        }
        else if (msg.Document is not null)
        {
            fileId = msg.Document.FileId;
            mediaType = "file";
        }

        // Download media if present
        if (fileId is not null && _bot is not null)
        {
            try
            {
                var mediaDir = Utils.Helpers.GetMediaPath();
                Directory.CreateDirectory(mediaDir);

                var ext = GetMediaExtension(mediaType, null);
                var filePath = Path.Combine(mediaDir, $"{fileId[..Math.Min(16, fileId.Length)]}{ext}");

                await using var fs = System.IO.File.Create(filePath);
                await _bot.GetInfoAndDownloadFile(fileId, fs);

                mediaPaths.Add(filePath);
                contentParts.Add($"[{mediaType}: {filePath}]");

                Logger?.LogDebug("Downloaded {MediaType} to {Path}", mediaType, filePath);
            }
            catch (Exception e)
            {
                Logger?.LogError(e, "Failed to download media");
                contentParts.Add($"[{mediaType}: download failed]");
            }
        }

        var content = contentParts.Count > 0 ? string.Join("\n", contentParts) : "[empty message]";
        Logger?.LogDebug("Telegram message from {Sender}: {Content}", senderId, content.Length > 50 ? content[..50] + "..." : content);

        var strChatId = chatId.ToString();

        // Start typing indicator before processing
        StartTyping(strChatId);

        // Forward to the message bus
        await HandleMessageAsync(
            senderId: senderId,
            chatId: strChatId,
            content: content,
            allowList: _config.AllowFrom,
            media: mediaPaths,
            metadata: new Dictionary<string, object>
            {
                ["message_id"] = msg.MessageId,
                ["user_id"] = user.Id,
                ["username"] = user.Username ?? "",
                ["first_name"] = user.FirstName ?? "",
                ["is_group"] = msg.Chat.Type != ChatType.Private,
            });
    }

    private async Task OnError(Exception exception, HandleErrorSource source)
    {
        Logger?.LogError(exception, "Telegram polling error (source: {Source})", source);
        await Task.CompletedTask;
    }

    // ─── Commands ──────────────────────────────────────────────────

    private async Task OnStart(Message msg)
    {
        if (_bot is null || msg.From is null) return;

        await _bot.SendMessage(msg.Chat.Id,
            $"👋 Hi {msg.From.FirstName}! I'm sharpbot.\n\n" +
            "Send me a message and I'll respond!\n" +
            "Type /help to see available commands.");
    }

    private async Task OnReset(Message msg)
    {
        if (_bot is null || msg.From is null) return;

        var chatId = msg.Chat.Id.ToString();
        var sessionKey = $"{ChannelName}:{chatId}";

        if (_sessionManager is null)
        {
            Logger?.LogWarning("/reset called but session manager is not available");
            await _bot.SendMessage(msg.Chat.Id, "⚠️ Session management is not available.");
            return;
        }

        var session = _sessionManager.GetOrCreate(sessionKey);
        var msgCount = session.Messages.Count;
        session.Clear();
        _sessionManager.Save(session);

        Logger?.LogInformation("Session reset for {Key} (cleared {Count} messages)", sessionKey, msgCount);
        await _bot.SendMessage(msg.Chat.Id, "🔄 Conversation history cleared. Let's start fresh!");
    }

    private async Task OnHelp(Message msg)
    {
        if (_bot is null) return;

        const string helpText =
            "🐈 <b>sharpbot commands</b>\n\n" +
            "/start — Start the bot\n" +
            "/reset — Reset conversation history\n" +
            "/help — Show this help message\n\n" +
            "Just send me a text message to chat!";

        await _bot.SendMessage(msg.Chat.Id, helpText, parseMode: ParseMode.Html);
    }

    // ─── Typing Indicators ─────────────────────────────────────────

    private void StartTyping(string chatId)
    {
        StopTyping(chatId);

        var typingCts = new CancellationTokenSource();
        _typingTasks[chatId] = typingCts;

        _ = Task.Run(async () =>
        {
            try
            {
                while (_bot is not null && !typingCts.Token.IsCancellationRequested)
                {
                    await _bot.SendChatAction(long.Parse(chatId), ChatAction.Typing);
                    await Task.Delay(4000, typingCts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Logger?.LogDebug("Typing indicator stopped for {ChatId}: {Error}", chatId, e.Message);
            }
        }, typingCts.Token);
    }

    private void StopTyping(string chatId)
    {
        if (_typingTasks.Remove(chatId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    // ─── Markdown → Telegram HTML ──────────────────────────────────

    /// <summary>
    /// Convert markdown to Telegram-safe HTML.
    /// Ported from nanobot's _markdown_to_telegram_html.
    /// </summary>
    internal static string MarkdownToTelegramHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // 1. Extract and protect code blocks
        var codeBlocks = new List<string>();
        text = Regex.Replace(text, @"```[\w]*\n?([\s\S]*?)```", m =>
        {
            codeBlocks.Add(m.Groups[1].Value);
            return $"\x00CB{codeBlocks.Count - 1}\x00";
        });

        // 2. Extract and protect inline code
        var inlineCodes = new List<string>();
        text = Regex.Replace(text, @"`([^`]+)`", m =>
        {
            inlineCodes.Add(m.Groups[1].Value);
            return $"\x00IC{inlineCodes.Count - 1}\x00";
        });

        // 3. Headers → just the title text
        text = Regex.Replace(text, @"^#{1,6}\s+(.+)$", "$1", RegexOptions.Multiline);

        // 4. Blockquotes → just the text
        text = Regex.Replace(text, @"^>\s*(.*)$", "$1", RegexOptions.Multiline);

        // 5. Escape HTML special characters
        text = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // 6. Links [text](url)
        text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", "<a href=\"$2\">$1</a>");

        // 7. Bold **text** or __text__
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<b>$1</b>");
        text = Regex.Replace(text, @"__(.+?)__", "<b>$1</b>");

        // 8. Italic _text_ (avoid matching inside words)
        text = Regex.Replace(text, @"(?<![a-zA-Z0-9])_([^_]+)_(?![a-zA-Z0-9])", "<i>$1</i>");

        // 9. Strikethrough ~~text~~
        text = Regex.Replace(text, @"~~(.+?)~~", "<s>$1</s>");

        // 10. Bullet lists
        text = Regex.Replace(text, @"^[-*]\s+", "• ", RegexOptions.Multiline);

        // 11. Restore inline code with HTML tags
        for (var i = 0; i < inlineCodes.Count; i++)
        {
            var escaped = inlineCodes[i].Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            text = text.Replace($"\x00IC{i}\x00", $"<code>{escaped}</code>");
        }

        // 12. Restore code blocks with HTML tags
        for (var i = 0; i < codeBlocks.Count; i++)
        {
            var escaped = codeBlocks[i].Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            text = text.Replace($"\x00CB{i}\x00", $"<pre><code>{escaped}</code></pre>");
        }

        return text;
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private static string GetMediaExtension(string mediaType, string? mimeType)
    {
        if (mimeType is not null)
        {
            var extMap = new Dictionary<string, string>
            {
                ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/gif"] = ".gif",
                ["audio/ogg"] = ".ogg", ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a",
            };
            if (extMap.TryGetValue(mimeType, out var ext))
                return ext;
        }

        return mediaType switch
        {
            "image" => ".jpg",
            "voice" => ".ogg",
            "audio" => ".mp3",
            _ => "",
        };
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
    }
}
