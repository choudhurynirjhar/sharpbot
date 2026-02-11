using Microsoft.Extensions.Logging;

namespace Sharpbot.Agent.Tools;

/// <summary>
/// Registry for agent tools. Allows dynamic registration and execution.
/// Programs to the <see cref="ITool"/> interface — any implementation can be registered.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = [];
    private readonly ILogger? _logger;

    public ToolRegistry(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Register a tool.</summary>
    public void Register(ITool tool) => _tools[tool.Name] = tool;

    /// <summary>Unregister a tool by name.</summary>
    public void Unregister(string name) => _tools.Remove(name);

    /// <summary>Get a tool by name.</summary>
    public ITool? Get(string name) => _tools.GetValueOrDefault(name);

    /// <summary>Check if a tool is registered.</summary>
    public bool Has(string name) => _tools.ContainsKey(name);

    /// <summary>Get all tool definitions in OpenAI format.</summary>
    public List<Dictionary<string, object?>> GetDefinitions() =>
        _tools.Values.Select(t => t.ToSchema()).ToList();

    /// <summary>Execute a tool by name with given parameters.</summary>
    public async Task<string> ExecuteAsync(string name, Dictionary<string, object?> args)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return $"Error: Tool '{name}' not found";

        try
        {
            return await tool.ExecuteAsync(args);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error executing tool {Name}", name);
            return $"Error executing {name}: {e.Message}";
        }
    }

    /// <summary>Get list of registered tool names.</summary>
    public List<string> ToolNames => [.. _tools.Keys];

    public int Count => _tools.Count;
}
