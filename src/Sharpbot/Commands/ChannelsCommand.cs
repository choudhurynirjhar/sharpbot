using System.CommandLine;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using QRCoder;
using Sharpbot.Config;
using Spectre.Console;

namespace Sharpbot.Commands;

/// <summary>CLI command: manage and inspect chat channels.</summary>
public sealed class ChannelsCommand : Command
{
    public ChannelsCommand() : base("channels", "Manage channels.")
    {
        Subcommands.Add(new ChannelsStatusCommand());
        Subcommands.Add(new ChannelsLoginCommand());
    }
}

// ------------------------------------------------------------------
// channels status
// ------------------------------------------------------------------

/// <summary>CLI sub-command: show channel status table.</summary>
file sealed class ChannelsStatusCommand : Command
{
    public ChannelsStatusCommand() : base("status", "Show channel status.")
    {
        this.SetAction(_ => Execute());
    }

    private static void Execute()
    {
        var config = ConfigLoader.LoadConfig();

        var table = new Table().Title("Channel Status");
        table.AddColumn("[cyan]Channel[/]");
        table.AddColumn("[green]Enabled[/]");
        table.AddColumn("[yellow]Configuration[/]");

        var wa = config.Channels.WhatsApp;
        table.AddRow("WhatsApp", wa.Enabled ? "✓" : "✗", wa.BridgeUrl);

        var dc = config.Channels.Discord;
        table.AddRow("Discord", dc.Enabled ? "✓" : "✗", dc.GatewayUrl);

        var tg = config.Channels.Telegram;
        var tgConfig = !string.IsNullOrEmpty(tg.Token)
            ? $"token: {tg.Token[..Math.Min(10, tg.Token.Length)]}..."
            : "[dim]not configured[/]";
        table.AddRow("Telegram", tg.Enabled ? "✓" : "✗", tgConfig);

        var fs = config.Channels.Feishu;
        var fsConfig = !string.IsNullOrEmpty(fs.AppId)
            ? $"app_id: {fs.AppId[..Math.Min(10, fs.AppId.Length)]}..."
            : "[dim]not configured[/]";
        table.AddRow("Feishu", fs.Enabled ? "✓" : "✗", fsConfig);

        AnsiConsole.Write(table);
    }
}

// ------------------------------------------------------------------
// channels login
// ------------------------------------------------------------------

/// <summary>
/// CLI sub-command: login to a channel (e.g. WhatsApp QR code scan).
/// Connects to the WhatsApp Node.js bridge, waits for a QR code,
/// renders it in the terminal for scanning, and waits for connection.
/// </summary>
file sealed class ChannelsLoginCommand : Command
{
    private readonly Argument<string> _channelArg = new("channel")
    {
        Description = "Channel to login to (whatsapp)"
    };

    private readonly Option<int> _timeoutOption = new("--timeout", "-t")
    {
        Description = "Timeout in seconds to wait for QR / connection",
        DefaultValueFactory = _ => 120
    };

    public ChannelsLoginCommand() : base("login", "Login to a channel (e.g. scan WhatsApp QR code).")
    {
        Arguments.Add(_channelArg);
        Options.Add(_timeoutOption);

        this.SetAction(async (parseResult, cancellationToken) =>
        {
            var channel = parseResult.GetValue(_channelArg)!;
            var timeout = parseResult.GetValue(_timeoutOption);
            await ExecuteAsync(channel, timeout, cancellationToken);
        });
    }

    private static async Task ExecuteAsync(string channel, int timeoutSeconds, CancellationToken ct)
    {
        if (!channel.Equals("whatsapp", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[red]Login is only supported for WhatsApp. Got: {Markup.Escape(channel)}[/]");
            AnsiConsole.MarkupLine("[dim]Telegram and Discord use bot tokens configured in appsettings.json.[/]");
            return;
        }

        var config = ConfigLoader.LoadConfig();
        var waConfig = config.Channels.WhatsApp;

        if (string.IsNullOrEmpty(waConfig.BridgeUrl))
        {
            AnsiConsole.MarkupLine("[red]WhatsApp bridge URL not configured.[/]");
            AnsiConsole.MarkupLine($"Set [cyan]Channels.WhatsApp.BridgeUrl[/] in {Markup.Escape(ConfigLoader.GetConfigPath())}");
            return;
        }

        AnsiConsole.MarkupLine($"Connecting to WhatsApp bridge at [cyan]{waConfig.BridgeUrl}[/]...");
        AnsiConsole.MarkupLine($"Waiting up to {timeoutSeconds}s for QR code. Press Ctrl+C to cancel.\n");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(waConfig.BridgeUrl), cts.Token);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to connect to bridge:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[dim]Make sure the WhatsApp Node.js bridge is running.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[green]Connected to bridge.[/] Waiting for QR code...\n");

        var buffer = new byte[16 * 1024];
        var isAuthenticated = false;

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    AnsiConsole.MarkupLine("[yellow]Bridge closed the connection.[/]");
                    break;
                }

                var raw = Encoding.UTF8.GetString(buffer, 0, result.Count);
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(raw);
                }
                catch
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    var msgType = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

                    switch (msgType)
                    {
                        case "qr":
                            var qrData = root.TryGetProperty("qr", out var qrEl) ? qrEl.GetString() : null;
                            if (string.IsNullOrEmpty(qrData))
                            {
                                AnsiConsole.MarkupLine("[yellow]QR event received but no data. Check bridge terminal.[/]");
                                break;
                            }

                            AnsiConsole.MarkupLine("[cyan]Scan this QR code with WhatsApp on your phone:[/]\n");
                            RenderQrCode(qrData);
                            AnsiConsole.MarkupLine("\n[dim]Open WhatsApp > Settings > Linked Devices > Link a Device[/]");
                            AnsiConsole.MarkupLine("[dim]QR refreshes automatically. Waiting for scan...[/]\n");
                            break;

                        case "status":
                            var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                            if (status == "connected")
                            {
                                AnsiConsole.MarkupLine("[green]✓ WhatsApp authenticated and connected![/]");
                                isAuthenticated = true;
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[dim]Status: {status}[/]");
                            }
                            break;

                        case "error":
                            var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : "unknown";
                            AnsiConsole.MarkupLine($"[red]Bridge error: {Markup.Escape(error ?? "unknown")}[/]");
                            break;
                    }
                }

                if (isAuthenticated) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("\n[yellow]Timed out waiting for QR scan. Run the command again to retry.[/]");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[dim]Cancelled.[/]");
        }

        if (isAuthenticated)
        {
            AnsiConsole.MarkupLine("\n[green]WhatsApp login complete.[/]");
            if (!waConfig.Enabled)
                AnsiConsole.MarkupLine("[yellow]Note: WhatsApp channel is not enabled. Set Channels.WhatsApp.Enabled = true to start receiving messages.[/]");
        }
    }

    /// <summary>Render a QR code string in the terminal using Unicode block characters.</summary>
    private static void RenderQrCode(string data)
    {
        using var generator = new QRCodeGenerator();
        var qrCodeData = generator.CreateQrCode(data, QRCodeGenerator.ECCLevel.L);
        using var qrCode = new AsciiQRCode(qrCodeData);

        // AsciiQRCode returns lines with "██" for dark modules and "  " for light.
        // We use the dark-on-light representation for terminal readability.
        var lines = qrCode.GetGraphic(1, "██", "  ");
        AnsiConsole.Write(new Text(lines));
    }
}
