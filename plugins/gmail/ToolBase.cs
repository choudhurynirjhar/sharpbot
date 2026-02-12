using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sharpbot.Agent.Tools;

namespace SharpbotPlugin.Gmail;

/// <summary>Base class for Gmail tools — provides schema helper, auth guard, and arg helpers.</summary>
internal abstract class GmailToolBase : ITool
{
    protected readonly GmailClient? Client;
    protected readonly ILogger? Logger;

    protected GmailToolBase(GmailClient? client, ILogger? logger)
    {
        Client = client;
        Logger = logger;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract Dictionary<string, object?> Parameters { get; }

    public async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        if (Client == null)
            return "Error: Gmail plugin is not configured. Set GMAIL_CLIENT_ID and GMAIL_CLIENT_SECRET environment variables, then run 'sharpbot gmail-auth'.";

        if (!Client.IsAuthorized)
            return "Error: Gmail is not authorized. Run 'sharpbot gmail-auth' to complete the one-time OAuth setup.";

        try
        {
            return await ExecuteCoreAsync(args);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not authorized"))
        {
            return $"Error: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            Logger?.LogWarning(ex, "Gmail API error in {Tool}", Name);
            return $"Gmail API error: {ex.Message}";
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Unexpected error in {Tool}", Name);
            return $"Error: {ex.Message}";
        }
    }

    protected abstract Task<string> ExecuteCoreAsync(Dictionary<string, object?> args);

    public Dictionary<string, object?> ToSchema() => new()
    {
        ["type"] = "function",
        ["function"] = new Dictionary<string, object?>
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = Parameters,
        }
    };

    protected static string GetString(Dictionary<string, object?> args, string key, string defaultValue = "")
    {
        if (!args.TryGetValue(key, out var v) || v == null) return defaultValue;

        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                var s = je.GetString();
                return string.IsNullOrEmpty(s) ? defaultValue : s;
            }
            return je.ValueKind == JsonValueKind.Null ? defaultValue : je.ToString();
        }

        if (v is string str)
            return string.IsNullOrEmpty(str) ? defaultValue : str;

        var raw = v.ToString();
        return string.IsNullOrEmpty(raw) ? defaultValue : raw;
    }

    protected static int GetInt(Dictionary<string, object?> args, string key, int defaultValue = 0)
    {
        if (!args.TryGetValue(key, out var v) || v == null) return defaultValue;

        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number)
                return je.TryGetInt32(out var n) ? n : defaultValue;
            if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var parsed))
                return parsed;
            return defaultValue;
        }

        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is double d) return (int)d;
        return int.TryParse(v.ToString(), out var p) ? p : defaultValue;
    }

    protected static bool GetBool(Dictionary<string, object?> args, string key, bool defaultValue = false)
    {
        if (!args.TryGetValue(key, out var v) || v == null) return defaultValue;

        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.True) return true;
            if (je.ValueKind == JsonValueKind.False) return false;
            if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var parsed))
                return parsed;
            return defaultValue;
        }

        if (v is bool b) return b;
        return bool.TryParse(v.ToString(), out var p) ? p : defaultValue;
    }
}
