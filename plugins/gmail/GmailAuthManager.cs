using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Gmail;

/// <summary>
/// Manages Google OAuth2 tokens for the Gmail API.
///
/// Lifecycle:
///   1. One-time: <see cref="AuthorizeInteractiveAsync"/> opens a browser, user logs in,
///      tokens are saved to disk.
///   2. Runtime: <see cref="GetAccessTokenAsync"/> loads the saved refresh token,
///      auto-refreshes when the access token expires, and returns a valid bearer token.
/// </summary>
internal sealed class GmailAuthManager
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string DefaultScope = "https://www.googleapis.com/auth/gmail.modify";
    private const int CallbackPort = 8765;

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _tokenPath;
    private readonly ILogger? _logger;
    private readonly HttpClient _http = new();

    private TokenData? _tokens;
    private DateTime _accessTokenExpiry = DateTime.MinValue;

    public GmailAuthManager(string clientId, string clientSecret, string tokenPath, ILogger? logger = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _tokenPath = tokenPath;
        _logger = logger;

        // Load existing tokens if available
        if (File.Exists(_tokenPath))
        {
            try
            {
                var json = File.ReadAllText(_tokenPath);
                _tokens = JsonSerializer.Deserialize<TokenData>(json);
                _logger?.LogInformation("Loaded Gmail OAuth tokens from {Path}", _tokenPath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load Gmail tokens from {Path}", _tokenPath);
            }
        }
    }

    /// <summary>Whether we have a refresh token (authorized at least once).</summary>
    public bool IsAuthorized
    {
        get
        {
            if (_tokens?.RefreshToken != null) return true;
            TryReloadTokens();
            return _tokens?.RefreshToken != null;
        }
    }

    /// <summary>Re-check the token file on disk (user may have run gmail-auth since startup).</summary>
    private void TryReloadTokens()
    {
        if (!File.Exists(_tokenPath)) return;
        try
        {
            var json = File.ReadAllText(_tokenPath);
            _tokens = JsonSerializer.Deserialize<TokenData>(json);
            _logger?.LogInformation("Loaded Gmail OAuth tokens from {Path} (lazy reload)", _tokenPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to reload Gmail tokens from {Path}", _tokenPath);
        }
    }

    /// <summary>
    /// Get a valid access token, refreshing if needed.
    /// Returns null if not authorized (run <see cref="AuthorizeInteractiveAsync"/> first).
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // Lazy-load: if tokens weren't available at construction time,
        // re-check the file (user may have run gmail-auth since startup)
        if (_tokens?.RefreshToken == null)
        {
            TryReloadTokens();
            if (_tokens?.RefreshToken == null) return null;
        }

        // Return cached token if still valid (with 60s buffer)
        if (_tokens.AccessToken != null
            && _accessTokenExpiry > DateTime.MinValue
            && DateTime.UtcNow < _accessTokenExpiry.AddSeconds(-60))
            return _tokens.AccessToken;

        // Refresh the access token
        var refreshed = await RefreshAccessTokenAsync(ct);
        return refreshed ? _tokens!.AccessToken : null;
    }

    /// <summary>
    /// Run the interactive OAuth2 authorization flow.
    /// Opens a browser for the user to log in, captures the callback, exchanges for tokens.
    /// </summary>
    public async Task<bool> AuthorizeInteractiveAsync(CancellationToken ct = default)
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

        // Start a temporary HTTP listener for the callback
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{CallbackPort}/");
        listener.Start();

        _logger?.LogInformation("Opening browser for Google OAuth2 authorization...");
        _logger?.LogInformation("If browser doesn't open, visit: {Url}", authUrl);
        OpenBrowser(authUrl);

        // Wait for the callback
        string? authCode = null;
        string? error = null;

        var contextTask = listener.GetContextAsync();
        var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(5), ct));

        if (completed != contextTask)
        {
            _logger?.LogError("Authorization timed out (5 minutes)");
            listener.Stop();
            return false;
        }

        var context = await contextTask;
        var query = context.Request.QueryString;
        authCode = query["code"];
        error = query["error"];
        var returnedState = query["state"];

        // Send response to browser
        var responseHtml = error == null
            ? "<html><body><h2>Authorization successful!</h2><p>You can close this window and return to sharpbot.</p></body></html>"
            : $"<html><body><h2>Authorization failed</h2><p>{error}</p></body></html>";
        var buffer = Encoding.UTF8.GetBytes(responseHtml);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, ct);
        context.Response.Close();
        listener.Stop();

        if (!string.IsNullOrEmpty(error))
        {
            _logger?.LogError("Google OAuth error: {Error}", error);
            return false;
        }

        if (string.IsNullOrEmpty(authCode))
        {
            _logger?.LogError("No authorization code received");
            return false;
        }

        if (returnedState != state)
        {
            _logger?.LogError("OAuth state mismatch — possible CSRF");
            return false;
        }

        // Exchange auth code for tokens
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
            _logger?.LogError("Token exchange failed: {Status} {Body}", (int)response.StatusCode, json);
            return false;
        }

        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
        {
            _logger?.LogError("Invalid token response");
            return false;
        }

        _tokens = new TokenData
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken ?? _tokens?.RefreshToken,
            ExpiresIn = tokenResponse.ExpiresIn,
        };
        _accessTokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

        SaveTokens();
        _logger?.LogInformation("Gmail OAuth tokens saved successfully");
        return true;
    }

    private async Task<bool> RefreshAccessTokenAsync(CancellationToken ct)
    {
        if (_tokens?.RefreshToken == null) return false;

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["refresh_token"] = _tokens.RefreshToken,
            ["grant_type"] = "refresh_token",
        });

        try
        {
            var response = await _http.PostAsync(TokenEndpoint, body, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError("Token refresh failed: {Status} {Body}", (int)response.StatusCode, json);
                return false;
            }

            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
                return false;

            _tokens.AccessToken = tokenResponse.AccessToken;
            _tokens.ExpiresIn = tokenResponse.ExpiresIn;
            // Refresh token is NOT returned on refresh; keep the original
            _accessTokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

            SaveTokens();
            _logger?.LogDebug("Gmail access token refreshed");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to refresh Gmail access token");
            return false;
        }
    }

    private void SaveTokens()
    {
        if (_tokens == null) return;

        var dir = Path.GetDirectoryName(_tokenPath);
        if (dir != null) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_tokens, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_tokenPath, json);
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch
        {
            // If browser launch fails, user can still copy the URL from the log
        }
    }
}

internal sealed class TokenData
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

internal sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}
