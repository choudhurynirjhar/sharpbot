using System.Text.Json;

namespace Sharpbot.Agent.Tools;

/// <summary>
/// Abstract base class that implements <see cref="ITool"/> and provides
/// convenience helpers for parameter extraction.
/// Concrete tools can inherit from this to avoid boilerplate.
/// </summary>
public abstract class ToolBase : ITool
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract Dictionary<string, object?> Parameters { get; }

    /// <inheritdoc />
    public abstract Task<string> ExecuteAsync(Dictionary<string, object?> args);

    /// <inheritdoc />
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

    /// <summary>Helper to get a string parameter.</summary>
    protected static string GetString(Dictionary<string, object?> args, string key, string defaultValue = "")
    {
        if (!args.TryGetValue(key, out var val) || val == null) return defaultValue;
        if (val is JsonElement je) return je.GetString() ?? defaultValue;
        return val.ToString() ?? defaultValue;
    }

    /// <summary>Helper to get an int parameter.</summary>
    protected static int? GetInt(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val == null) return null;
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number) return je.GetInt32();
            if (int.TryParse(je.GetString(), out var parsed)) return parsed;
            return null;
        }
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (int.TryParse(val.ToString(), out var result)) return result;
        return null;
    }

    /// <summary>Helper to get a bool parameter.</summary>
    protected static bool GetBool(Dictionary<string, object?> args, string key, bool defaultValue = false)
    {
        if (!args.TryGetValue(key, out var val) || val == null) return defaultValue;
        if (val is JsonElement je) return je.ValueKind == JsonValueKind.True;
        if (val is bool b) return b;
        return defaultValue;
    }
}
