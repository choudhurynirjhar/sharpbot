using System.CommandLine;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Sharpbot.Commands;

/// <summary>
/// CLI command: one-time OAuth2 authorization for the Gmail plugin.
/// Opens a browser for Google login, captures the callback, and saves the refresh token.
/// </summary>
public sealed class GmailAuthCommand : Command
{
    public GmailAuthCommand() : base("gmail-auth",
        "Authorize Sharpbot to access Gmail (one-time OAuth2 setup). Opens a browser for Google login.")
    {
        this.SetAction(async (_, ct) => await ExecuteAsync(ct));
    }

    private static async Task ExecuteAsync(CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"\n{SharpbotInfo.Logo} [bold]Gmail OAuth2 Authorization[/]\n");

        var clientId = Environment.GetEnvironmentVariable("GMAIL_CLIENT_ID") ?? "";
        var clientSecret = Environment.GetEnvironmentVariable("GMAIL_CLIENT_SECRET") ?? "";
        var tokenPath = Environment.GetEnvironmentVariable("GMAIL_TOKEN_PATH") ?? "";

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] GMAIL_CLIENT_ID and GMAIL_CLIENT_SECRET environment variables are required.\n");
            AnsiConsole.MarkupLine("[dim]Steps to set up:[/]");
            AnsiConsole.MarkupLine("  1. Go to https://console.cloud.google.com");
            AnsiConsole.MarkupLine("  2. Create a project (or use an existing one)");
            AnsiConsole.MarkupLine("  3. Enable the Gmail API");
            AnsiConsole.MarkupLine("  4. Create OAuth2 credentials (type: Desktop app)");
            AnsiConsole.MarkupLine("  5. Set environment variables:");
            AnsiConsole.MarkupLine("     [cyan]GMAIL_CLIENT_ID=your-client-id[/]");
            AnsiConsole.MarkupLine("     [cyan]GMAIL_CLIENT_SECRET=your-client-secret[/]");
            AnsiConsole.MarkupLine("  6. Run this command again: [cyan]sharpbot gmail-auth[/]");
            return;
        }

        // Default token path
        if (string.IsNullOrEmpty(tokenPath))
        {
            var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins", "gmail", "data");
            tokenPath = Path.Combine(pluginDir, "gmail-token.json");
        }

        AnsiConsole.MarkupLine($"[dim]Client ID: {clientId[..Math.Min(12, clientId.Length)]}...[/]");
        AnsiConsole.MarkupLine($"[dim]Token will be saved to: {Markup.Escape(tokenPath)}[/]");
        AnsiConsole.WriteLine();

        // Create a console logger for the auth flow
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        var logger = loggerFactory.CreateLogger("gmail-auth");

        // Dynamically load the GmailAuthManager from the plugin assembly
        // Since this is a CLI command in the core app, we need to reference the plugin types.
        // We'll use the same approach: inline the minimal OAuth flow here.
        var auth = new GmailAuthHelper(clientId, clientSecret, tokenPath, logger);

        AnsiConsole.MarkupLine("[yellow]Opening browser for Google authorization...[/]");
        AnsiConsole.MarkupLine("[dim]If the browser doesn't open automatically, check the URL in the log output above.[/]\n");

        var success = await auth.AuthorizeInteractiveAsync(ct);

        if (success)
        {
            AnsiConsole.MarkupLine("\n[green]✓ Gmail authorization successful![/]");
            AnsiConsole.MarkupLine($"[green]✓ Refresh token saved to {tokenPath}[/]");
            AnsiConsole.MarkupLine("\n[dim]The Gmail plugin will now auto-refresh tokens on each startup.[/]");
            AnsiConsole.MarkupLine("[dim]You only need to run this command again if you revoke access.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[red]✗ Gmail authorization failed.[/]");
            AnsiConsole.MarkupLine("[dim]Check the error messages above and try again.[/]");
        }
    }
}

/// <summary>
/// Minimal OAuth2 helper embedded in the CLI command so we don't need to load the plugin DLL.
/// Duplicates the core auth logic from GmailAuthManager for the one-time setup flow.
/// </summary>
internal sealed class GmailAuthHelper
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string DefaultScope = "https://www.googleapis.com/auth/gmail.modify";
    private const int CallbackPort = 8765;

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _tokenPath;
    private readonly ILogger _logger;
    private readonly HttpClient _http = new();

    public GmailAuthHelper(string clientId, string clientSecret, string tokenPath, ILogger logger)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _tokenPath = tokenPath;
        _logger = logger;
    }

    public async Task<bool> AuthorizeInteractiveAsync(CancellationToken ct)
    {
        var redirectUri = $"http://localhost:{CallbackPort}/callback";
        var state = Guid.NewGuid().ToString("N");

        var authUrl = $"{AuthEndpoint}" +
            $"?client_id={Uri.EscapeDataString(_clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&response_type=code" +
            $"&scope={Uri.EscapeDataString(DefaultScope)}" +
            $"&access_type=offline" +
            $"&prompt=consent" +
            $"&state={state}";

        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{CallbackPort}/");
        listener.Start();

        _logger.LogInformation("Authorization URL: {Url}", authUrl);
        OpenBrowser(authUrl);

        string? authCode = null;
        string? error = null;

        var contextTask = listener.GetContextAsync();
        var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(5), ct));

        if (completed != contextTask)
        {
            _logger.LogError("Authorization timed out");
            listener.Stop();
            return false;
        }

        var context = await contextTask;
        var query = context.Request.QueryString;
        authCode = query["code"];
        error = query["error"];
        var returnedState = query["state"];

        var responseHtml = error == null
            ? "<html><body style='font-family:sans-serif;text-align:center;padding:60px'>" +
              "<h2 style='color:#4CAF50'>✓ Authorization successful!</h2>" +
              "<p>You can close this window and return to the terminal.</p></body></html>"
            : $"<html><body style='font-family:sans-serif;text-align:center;padding:60px'>" +
              $"<h2 style='color:#f44336'>Authorization failed</h2><p>{error}</p></body></html>";

        var buffer = System.Text.Encoding.UTF8.GetBytes(responseHtml);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, ct);
        context.Response.Close();
        listener.Stop();

        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogError("OAuth error: {Error}", error);
            return false;
        }

        if (string.IsNullOrEmpty(authCode))
        {
            _logger.LogError("No authorization code received");
            return false;
        }

        if (returnedState != state)
        {
            _logger.LogError("State mismatch — possible CSRF");
            return false;
        }

        return await ExchangeCodeAsync(authCode, redirectUri, ct);
    }

    private async Task<bool> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        });

        var response = await _http.PostAsync(TokenEndpoint, body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Token exchange failed: {Status} {Body}", (int)response.StatusCode, json);
            return false;
        }

        // Save the raw token response
        var dir = Path.GetDirectoryName(_tokenPath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(_tokenPath, json);
        return true;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                         System.Runtime.InteropServices.OSPlatform.OSX))
                System.Diagnostics.Process.Start("open", url);
            else
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch
        {
            // User can copy the URL from the log
        }
    }
}
