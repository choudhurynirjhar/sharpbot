using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Gmail;

/// <summary>
/// Lightweight Gmail REST API client.
/// Uses OAuth2 bearer tokens managed by <see cref="GmailAuthManager"/>.
/// All endpoints target https://gmail.googleapis.com/gmail/v1/users/me/...
/// </summary>
internal sealed class GmailClient : IDisposable
{
    private const string BaseUrl = "https://gmail.googleapis.com/gmail/v1/users/me";

    private readonly GmailAuthManager _auth;
    private readonly HttpClient _http;
    private readonly ILogger? _logger;

    public GmailClient(GmailAuthManager auth, ILogger? logger = null)
    {
        _auth = auth;
        _http = new HttpClient();
        _logger = logger;
    }

    public bool IsAuthorized => _auth.IsAuthorized;

    /// <summary>Search messages using Gmail query syntax.</summary>
    public async Task<JsonElement> SearchAsync(
        string query,
        int maxResults = 20,
        string? labelIds = null,
        string? pageToken = null,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/messages?q={Uri.EscapeDataString(query)}&maxResults={maxResults}";
        if (!string.IsNullOrEmpty(labelIds))
            url += $"&labelIds={Uri.EscapeDataString(labelIds)}";
        if (!string.IsNullOrEmpty(pageToken))
            url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

        return await GetJsonAsync(url, ct);
    }

    /// <summary>Get a single message with full or metadata format.</summary>
    public async Task<JsonElement> GetMessageAsync(
        string messageId,
        string format = "full",
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/messages/{Uri.EscapeDataString(messageId)}?format={format}";
        return await GetJsonAsync(url, ct);
    }

    /// <summary>Get a thread with all messages.</summary>
    public async Task<JsonElement> GetThreadAsync(
        string threadId,
        string format = "full",
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/threads/{Uri.EscapeDataString(threadId)}?format={format}";
        return await GetJsonAsync(url, ct);
    }

    /// <summary>Send a new email. Takes a raw RFC 2822 message (base64url-encoded).</summary>
    public async Task<JsonElement> SendMessageAsync(
        string to,
        string subject,
        string body,
        string? cc = null,
        string? bcc = null,
        string? inReplyTo = null,
        string? references = null,
        string? threadId = null,
        CancellationToken ct = default)
    {
        var mime = BuildMimeMessage(to, subject, body, cc, bcc, inReplyTo, references);
        var encoded = Base64UrlEncode(Encoding.UTF8.GetBytes(mime));

        var payload = new Dictionary<string, object?> { ["raw"] = encoded };
        if (!string.IsNullOrEmpty(threadId))
            payload["threadId"] = threadId;

        var url = $"{BaseUrl}/messages/send";
        return await PostJsonAsync(url, payload, ct);
    }

    /// <summary>Modify labels on a message (add/remove labels).</summary>
    public async Task<JsonElement> ModifyMessageAsync(
        string messageId,
        List<string>? addLabels = null,
        List<string>? removeLabels = null,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/messages/{Uri.EscapeDataString(messageId)}/modify";
        var payload = new Dictionary<string, object?>
        {
            ["addLabelIds"] = addLabels ?? [],
            ["removeLabelIds"] = removeLabels ?? [],
        };
        return await PostJsonAsync(url, payload, ct);
    }

    /// <summary>Trash a message (moves to Trash, recoverable for 30 days).</summary>
    public async Task<JsonElement> TrashMessageAsync(string messageId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/messages/{Uri.EscapeDataString(messageId)}/trash";
        return await PostJsonAsync(url, new { }, ct);
    }

    /// <summary>List available labels.</summary>
    public async Task<JsonElement> ListLabelsAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/labels";
        return await GetJsonAsync(url, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        await SetAuthHeaderAsync(ct);
        var response = await _http.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Gmail API {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 500)}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private async Task<JsonElement> PostJsonAsync(string url, object payload, CancellationToken ct)
    {
        await SetAuthHeaderAsync(ct);

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Gmail API {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 500)}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(responseJson);
    }

    private async Task SetAuthHeaderAsync(CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                "Gmail not authorized. Run 'sharpbot gmail-auth' to complete the one-time OAuth setup.");

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Build an RFC 2822 MIME message string.</summary>
    internal static string BuildMimeMessage(
        string to, string subject, string body,
        string? cc = null, string? bcc = null,
        string? inReplyTo = null, string? references = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"To: {to}");
        if (!string.IsNullOrEmpty(cc))
            sb.AppendLine($"Cc: {cc}");
        if (!string.IsNullOrEmpty(bcc))
            sb.AppendLine($"Bcc: {bcc}");
        sb.AppendLine($"Subject: {subject}");
        sb.AppendLine("Content-Type: text/plain; charset=utf-8");
        sb.AppendLine("MIME-Version: 1.0");

        if (!string.IsNullOrEmpty(inReplyTo))
            sb.AppendLine($"In-Reply-To: {inReplyTo}");
        if (!string.IsNullOrEmpty(references))
            sb.AppendLine($"References: {references}");

        sb.AppendLine();
        sb.Append(body);

        return sb.ToString();
    }

    /// <summary>Base64url encode (RFC 4648 §5, no padding).</summary>
    internal static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>Base64url decode.</summary>
    internal static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    /// <summary>Extract plain text body from a Gmail message payload.</summary>
    internal static string ExtractBody(JsonElement payload)
    {
        // Try multipart first (most emails)
        if (payload.TryGetProperty("parts", out var parts))
        {
            // Prefer text/plain
            foreach (var part in parts.EnumerateArray())
            {
                var mime = part.TryGetProperty("mimeType", out var mt) ? mt.GetString() : "";
                if (mime == "text/plain" && part.TryGetProperty("body", out var b)
                    && b.TryGetProperty("data", out var d))
                {
                    return DecodeBodyData(d.GetString());
                }
            }

            // Fall back to text/html with tag stripping
            foreach (var part in parts.EnumerateArray())
            {
                var mime = part.TryGetProperty("mimeType", out var mt) ? mt.GetString() : "";
                if (mime == "text/html" && part.TryGetProperty("body", out var b)
                    && b.TryGetProperty("data", out var d))
                {
                    return StripHtml(DecodeBodyData(d.GetString()));
                }
            }

            // Recurse into nested multipart
            foreach (var part in parts.EnumerateArray())
            {
                var result = ExtractBody(part);
                if (!string.IsNullOrEmpty(result)) return result;
            }
        }

        // Simple body (single-part message)
        if (payload.TryGetProperty("body", out var body) && body.TryGetProperty("data", out var data))
            return DecodeBodyData(data.GetString());

        return "(no body)";
    }

    private static string DecodeBodyData(string? data)
    {
        if (string.IsNullOrEmpty(data)) return "";
        return Encoding.UTF8.GetString(Base64UrlDecode(data));
    }

    /// <summary>Basic HTML tag stripper for when no text/plain is available.</summary>
    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        // Remove style/script blocks
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<(style|script)[^>]*>.*?</\1>", "", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Replace br/p tags with newlines
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"</(p|div|tr|li)>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Strip remaining tags
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", "");
        // Decode entities
        html = System.Net.WebUtility.HtmlDecode(html);
        // Collapse whitespace
        html = System.Text.RegularExpressions.Regex.Replace(html, @"[ \t]+", " ");
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\n{3,}", "\n\n");
        return html.Trim();
    }

    /// <summary>Get a header value from a Gmail message.</summary>
    internal static string? GetHeader(JsonElement headers, string name)
    {
        foreach (var h in headers.EnumerateArray())
        {
            if (h.TryGetProperty("name", out var n) &&
                n.GetString()?.Equals(name, StringComparison.OrdinalIgnoreCase) == true &&
                h.TryGetProperty("value", out var v))
            {
                return v.GetString();
            }
        }
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    public void Dispose() => _http.Dispose();
}
