using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpbotPlugin.Jira;

/// <summary>
/// List all Jira projects the user has access to, or get details of a specific project.
/// </summary>
internal sealed class JiraProjectsTool(JiraClient? client, ILogger? logger) : JiraToolBase(client, logger)
{
    public override string Name => "jira_projects";

    public override string Description =>
        "List all Jira projects you have access to, or get details of a specific project by key. " +
        "Shows project name, key, type, lead, and description.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["project"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Project key to get details for. If omitted, lists all projects."
            },
        },
        ["required"] = Array.Empty<string>()
    };

    protected override async Task<string> ExecuteCoreAsync(Dictionary<string, object?> args)
    {
        var projectKey = GetString(args, "project");

        if (!string.IsNullOrEmpty(projectKey))
            return await GetProjectDetailAsync(projectKey);

        return await ListProjectsAsync();
    }

    private async Task<string> ListProjectsAsync()
    {
        var result = await Client!.GetProjectsAsync();
        var sb = new StringBuilder();
        sb.AppendLine("## Jira Projects\n");

        if (result.ValueKind != JsonValueKind.Array)
            return "No projects found or unexpected response format.";

        foreach (var proj in result.EnumerateArray())
        {
            var key = proj.TryGetProperty("key", out var k) ? k.GetString() : "?";
            var name = proj.TryGetProperty("name", out var n) ? n.GetString() : "?";
            var projectType = proj.TryGetProperty("projectTypeKey", out var pt) ? pt.GetString() : "?";
            var lead = "";
            if (proj.TryGetProperty("lead", out var l) && l.TryGetProperty("displayName", out var ld))
                lead = $" | Lead: {ld.GetString()}";

            sb.AppendLine($"- **{key}** — {name} ({projectType}){lead}");
        }

        sb.AppendLine($"\nTotal: {result.GetArrayLength()} projects");
        return sb.ToString();
    }

    private async Task<string> GetProjectDetailAsync(string key)
    {
        var proj = await Client!.GetProjectAsync(key);
        var sb = new StringBuilder();

        var name = proj.TryGetProperty("name", out var n) ? n.GetString() : "?";
        var projKey = proj.TryGetProperty("key", out var k) ? k.GetString() : key;
        var projectType = proj.TryGetProperty("projectTypeKey", out var pt) ? pt.GetString() : "?";
        var lead = "";
        if (proj.TryGetProperty("lead", out var l) && l.TryGetProperty("displayName", out var ld))
            lead = ld.GetString();

        sb.AppendLine($"# {projKey}: {name}");
        sb.AppendLine($"**Type:** {projectType}");
        if (!string.IsNullOrEmpty(lead))
            sb.AppendLine($"**Lead:** {lead}");

        if (proj.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
        {
            var descText = desc.GetString();
            if (!string.IsNullOrEmpty(descText))
                sb.AppendLine($"**Description:** {descText}");
        }

        // Issue types
        if (proj.TryGetProperty("issueTypes", out var types) && types.ValueKind == JsonValueKind.Array)
        {
            var typeNames = types.EnumerateArray()
                .Select(t => t.TryGetProperty("name", out var tn) ? tn.GetString() : null)
                .Where(t => t != null);
            sb.AppendLine($"**Issue Types:** {string.Join(", ", typeNames)}");
        }

        return sb.ToString();
    }
}
