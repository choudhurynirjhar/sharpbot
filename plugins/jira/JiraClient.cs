using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SharpbotPlugin.Jira;

/// <summary>
/// Lightweight Jira REST API client.
/// Authenticates via Basic Auth (email + API token) for Jira Cloud,
/// or Bearer token for Jira Data Center / Server.
/// 
/// Environment variables:
///   JIRA_BASE_URL   — e.g. https://yourcompany.atlassian.net
///   JIRA_EMAIL      — your Atlassian account email (Cloud only)
///   JIRA_API_TOKEN  — API token from https://id.atlassian.com/manage-profile/security/api-tokens
/// </summary>
internal sealed class JiraClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiVersion;

    public JiraClient(string baseUrl, string email, string apiToken, string? apiVersion = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient();

        // Auto-detect API version: Jira Cloud (*.atlassian.net) uses v3, everything else uses v2
        _apiVersion = apiVersion
            ?? (_baseUrl.Contains(".atlassian.net", StringComparison.OrdinalIgnoreCase) ? "3" : "2");

        if (!string.IsNullOrEmpty(email))
        {
            // Basic Auth with email:token (works for both Cloud and Data Center)
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        else
        {
            // Bearer token (Data Center / Server personal access tokens)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        }

        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl);

    /// <summary>Execute a JQL search and return matching issues.</summary>
    public async Task<JsonElement> SearchAsync(
        string jql,
        int startAt = 0,
        int maxResults = 20,
        string? fields = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/api/{_apiVersion}/search?jql={Uri.EscapeDataString(jql)}" +
                  $"&startAt={startAt}&maxResults={maxResults}";
        if (!string.IsNullOrEmpty(fields))
            url += $"&fields={Uri.EscapeDataString(fields)}";

        return await GetJsonAsync(url, ct);
    }

    /// <summary>Get a single issue by key (e.g. PROJ-123).</summary>
    public async Task<JsonElement> GetIssueAsync(
        string issueKey,
        string? fields = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/api/{_apiVersion}/issue/{Uri.EscapeDataString(issueKey)}";
        if (!string.IsNullOrEmpty(fields))
            url += $"?fields={Uri.EscapeDataString(fields)}";

        return await GetJsonAsync(url, ct);
    }

    /// <summary>List all projects the user has access to.</summary>
    public async Task<JsonElement> GetProjectsAsync(CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/api/{_apiVersion}/project?expand=description";
        return await GetJsonAsync(url, ct);
    }

    /// <summary>Get details of a specific project.</summary>
    public async Task<JsonElement> GetProjectAsync(string projectKey, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/api/{_apiVersion}/project/{Uri.EscapeDataString(projectKey)}";
        return await GetJsonAsync(url, ct);
    }

    /// <summary>List all boards (Scrum/Kanban) from Jira Software (Agile API).</summary>
    public async Task<JsonElement> GetBoardsAsync(
        string? projectKey = null,
        int startAt = 0,
        int maxResults = 50,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/agile/1.0/board?startAt={startAt}&maxResults={maxResults}";
        if (!string.IsNullOrEmpty(projectKey))
            url += $"&projectKeyOrId={Uri.EscapeDataString(projectKey)}";

        return await GetJsonAsync(url, ct);
    }

    /// <summary>Get sprints for a board.</summary>
    public async Task<JsonElement> GetSprintsAsync(
        int boardId,
        string? state = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/agile/1.0/board/{boardId}/sprint?maxResults=50";
        if (!string.IsNullOrEmpty(state))
            url += $"&state={Uri.EscapeDataString(state)}";

        return await GetJsonAsync(url, ct);
    }

    /// <summary>Get issues in a sprint (Agile API).</summary>
    public async Task<JsonElement> GetSprintIssuesAsync(
        int sprintId,
        int startAt = 0,
        int maxResults = 50,
        string? fields = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/agile/1.0/sprint/{sprintId}/issue" +
                  $"?startAt={startAt}&maxResults={maxResults}";
        if (!string.IsNullOrEmpty(fields))
            url += $"&fields={Uri.EscapeDataString(fields)}";

        return await GetJsonAsync(url, ct);
    }

    /// <summary>Get comments for an issue.</summary>
    public async Task<JsonElement> GetCommentsAsync(string issueKey, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/api/{_apiVersion}/issue/{Uri.EscapeDataString(issueKey)}/comment";
        return await GetJsonAsync(url, ct);
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Jira API {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 500)}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    public void Dispose() => _http.Dispose();
}
