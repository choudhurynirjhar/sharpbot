using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace Sharpbot.Agent.Tools;

/// <summary>Search the web using Brave Search API.</summary>
public sealed class WebSearchTool : ToolBase
{
    private readonly string _apiKey;
    private readonly int _maxResults;
    private readonly HttpClient _httpClient;

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

    public WebSearchTool(string? apiKey = null, int maxResults = 5, HttpClient? httpClient = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("BRAVE_API_KEY") ?? "";
        _maxResults = maxResults;
        _httpClient = httpClient ?? CreateDefaultClient();
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        return client;
    }

    public override string Name => "web_search";
    public override string Description => "Search the web. Returns titles, URLs, and snippets.";
    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["query"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Search query" },
            ["count"] = new Dictionary<string, object?> { ["type"] = "integer", ["description"] = "Results (1-10)", ["minimum"] = 1, ["maximum"] = 10 },
        },
        ["required"] = new[] { "query" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        if (string.IsNullOrEmpty(_apiKey)) return "Error: BRAVE_API_KEY not configured";

        var query = GetString(args, "query");
        var count = Math.Clamp(GetInt(args, "count") ?? _maxResults, 1, 10);

        try
        {
            var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={count}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Subscription-Token", _apiKey);
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var results = doc.RootElement
                .GetProperty("web")
                .GetProperty("results");

            if (results.GetArrayLength() == 0)
                return $"No results for: {query}";

            var lines = new List<string> { $"Results for: {query}\n" };
            var i = 0;
            foreach (var item in results.EnumerateArray())
            {
                if (++i > count) break;
                var title = item.GetProperty("title").GetString() ?? "";
                var itemUrl = item.GetProperty("url").GetString() ?? "";
                lines.Add($"{i}. {title}\n   {itemUrl}");
                if (item.TryGetProperty("description", out var desc))
                    lines.Add($"   {desc.GetString()}");
            }
            return string.Join("\n", lines);
        }
        catch (Exception e)
        {
            return $"Error: {e.Message}";
        }
    }
}

/// <summary>Fetch and extract content from a URL.</summary>
public sealed class WebFetchTool : ToolBase
{
    private readonly int _maxChars;
    private readonly HttpClient _httpClient;

    public WebFetchTool(int maxChars = 50000, HttpClient? httpClient = null)
    {
        _maxChars = maxChars;
        _httpClient = httpClient ?? CreateDefaultClient();
    }

    private static HttpClient CreateDefaultClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        return client;
    }

    public override string Name => "web_fetch";
    public override string Description => "Fetch URL and extract readable content (HTML → text).";
    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["url"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "URL to fetch" },
            ["maxChars"] = new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 100 },
        },
        ["required"] = new[] { "url" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var url = GetString(args, "url");
        var maxChars = GetInt(args, "maxChars") ?? _maxChars;

        // Validate URL
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return JsonSerializer.Serialize(new { error = "URL must be http or https", url });
        }

        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var body = await response.Content.ReadAsStringAsync();

            string text;
            string extractor;

            if (contentType.Contains("application/json"))
            {
                text = body;
                extractor = "json";
            }
            else if (contentType.Contains("text/html") || body.TrimStart().StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) || body.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                text = ExtractReadableText(body);
                extractor = "html_strip";
            }
            else
            {
                text = body;
                extractor = "raw";
            }

            var truncated = text.Length > maxChars;
            if (truncated) text = text[..maxChars];

            return JsonSerializer.Serialize(new
            {
                url,
                finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url,
                status = (int)response.StatusCode,
                extractor,
                truncated,
                length = text.Length,
                text
            });
        }
        catch (Exception e)
        {
            return JsonSerializer.Serialize(new { error = e.Message, url });
        }
    }

    private static string ExtractReadableText(string html)
    {
        // Use HtmlAgilityPack for better extraction
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        // Remove scripts and styles
        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//noscript") ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
            node.Remove();

        // Get title
        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();

        // Get text from body
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        var text = HttpUtility.HtmlDecode(body.InnerText);

        // Clean whitespace
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        text = text.Trim();

        if (!string.IsNullOrEmpty(title))
            text = $"# {title}\n\n{text}";

        return text;
    }
}
