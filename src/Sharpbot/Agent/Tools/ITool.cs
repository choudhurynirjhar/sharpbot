namespace Sharpbot.Agent.Tools;

/// <summary>
/// Interface for agent tools — adheres to ISP by defining the minimal contract.
/// Tools are capabilities that the agent can use to interact with
/// the environment, such as reading files, executing commands, etc.
/// </summary>
public interface ITool
{
    /// <summary>Tool name used in function calls.</summary>
    string Name { get; }

    /// <summary>Description of what the tool does.</summary>
    string Description { get; }

    /// <summary>JSON Schema for tool parameters.</summary>
    Dictionary<string, object?> Parameters { get; }

    /// <summary>Execute the tool with given parameters.</summary>
    Task<string> ExecuteAsync(Dictionary<string, object?> args);

    /// <summary>Convert tool to OpenAI function schema format.</summary>
    Dictionary<string, object?> ToSchema();
}
