using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Sharpbot.Agent.Tools;

/// <summary>
/// General-purpose HTTP request tool supporting all HTTP methods,
/// custom headers, request body, and authentication.
/// This enables skills to define arbitrary API calls that the agent can execute.
/// </summary>
public sealed class HttpRequestTool : ToolBase
{
    private readonly HttpClient _httpClient;

    public HttpRequestTool(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
    }

    private static HttpClient CreateDefaultClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("User-Agent", "Sharpbot/1.0");
        return client;
    }

    public override string Name => "http_request";

    public override string Description =>
        "Make an HTTP request. Supports GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS. " +
        "Use for API calls with custom headers, JSON body, or authentication tokens.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["url"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The URL to send the request to",
            },
            ["method"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "HTTP method (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS). Defaults to GET.",
                ["enum"] = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" },
            },
            ["headers"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["description"] = "Custom HTTP headers as key-value pairs (e.g. {\"Authorization\": \"Bearer token\", \"X-Api-Key\": \"key\"})",
                ["additionalProperties"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["body"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Request body (string). For JSON, pass a JSON string and set Content-Type header to application/json.",
            },
            ["json"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["description"] = "JSON body (object). Automatically serialized and Content-Type set to application/json. Mutually exclusive with 'body'.",
            },
            ["bearer_token"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Bearer token for Authorization header. Shorthand for setting Authorization: Bearer <token>.",
            },
            ["timeout"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Request timeout in seconds (default: 30, max: 120).",
                ["minimum"] = 1,
                ["maximum"] = 120,
            },
            ["max_response_chars"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Maximum characters to return from response body (default: 50000).",
                ["minimum"] = 100,
            },
        },
        ["required"] = new[] { "url" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var url = GetString(args, "url");
        var method = GetString(args, "method", "GET").ToUpperInvariant();
        var bearerToken = GetString(args, "bearer_token");
        var body = GetString(args, "body");
        var timeout = Math.Clamp(GetInt(args, "timeout") ?? 30, 1, 120);
        var maxChars = GetInt(args, "max_response_chars") ?? 50000;

        // Validate URL
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return JsonSerializer.Serialize(new { error = "URL must be http or https", url });
        }

        // Parse HTTP method
        var httpMethod = method switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            "DELETE" => HttpMethod.Delete,
            "HEAD" => HttpMethod.Head,
            "OPTIONS" => HttpMethod.Options,
            _ => HttpMethod.Get,
        };

        try
        {
            using var request = new HttpRequestMessage(httpMethod, uri);

            // Set bearer token
            if (!string.IsNullOrEmpty(bearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            // Set custom headers
            var headers = GetJsonObject(args, "headers");
            if (headers != null)
            {
                foreach (var kvp in headers)
                {
                    var headerValue = kvp.Value?.ToString() ?? "";
                    // Content-Type must go on the content, not the request headers
                    if (kvp.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue; // handled when setting body
                    request.Headers.TryAddWithoutValidation(kvp.Key, headerValue);
                }
            }

            // Set body: prefer 'json' object over 'body' string
            var jsonBody = GetJsonObject(args, "json");
            if (jsonBody != null)
            {
                var jsonStr = JsonSerializer.Serialize(jsonBody);
                request.Content = new StringContent(jsonStr, Encoding.UTF8, "application/json");
            }
            else if (!string.IsNullOrEmpty(body))
            {
                var contentType = GetContentTypeFromHeaders(headers) ?? "text/plain";
                request.Content = new StringContent(body, Encoding.UTF8, contentType);
            }

            // Execute with timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            var response = await _httpClient.SendAsync(request, cts.Token);

            // Build response
            var statusCode = (int)response.StatusCode;
            var responseHeaders = new Dictionary<string, string>();
            foreach (var header in response.Headers)
                responseHeaders[header.Key] = string.Join(", ", header.Value);
            foreach (var header in response.Content.Headers)
                responseHeaders[header.Key] = string.Join(", ", header.Value);

            var responseBody = "";
            if (httpMethod != HttpMethod.Head)
            {
                responseBody = await response.Content.ReadAsStringAsync();
            }

            var truncated = responseBody.Length > maxChars;
            if (truncated)
                responseBody = responseBody[..maxChars];

            return JsonSerializer.Serialize(new
            {
                url,
                method,
                status = statusCode,
                statusText = response.ReasonPhrase ?? "",
                headers = responseHeaders,
                truncated,
                length = responseBody.Length,
                body = responseBody,
            });
        }
        catch (TaskCanceledException)
        {
            return JsonSerializer.Serialize(new { error = $"Request timed out after {timeout}s", url, method });
        }
        catch (Exception e)
        {
            return JsonSerializer.Serialize(new { error = e.Message, url, method });
        }
    }

    /// <summary>Extract a JSON object parameter as a Dictionary.</summary>
    private static Dictionary<string, string>? GetJsonObject(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val == null) return null;

        if (val is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, string>();
            foreach (var prop in je.EnumerateObject())
                result[prop.Name] = prop.Value.ToString();
            return result;
        }

        if (val is Dictionary<string, object?> dict)
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in dict)
                result[kvp.Key] = kvp.Value?.ToString() ?? "";
            return result;
        }

        if (val is Dictionary<string, string> strDict)
            return strDict;

        return null;
    }

    private static string? GetContentTypeFromHeaders(Dictionary<string, string>? headers)
    {
        if (headers == null) return null;
        foreach (var kvp in headers)
        {
            if (kvp.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return null;
    }
}
