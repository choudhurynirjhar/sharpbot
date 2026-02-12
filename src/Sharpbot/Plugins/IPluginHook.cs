using Sharpbot.Bus;

namespace Sharpbot.Plugins;

/// <summary>
/// Hook interface for intercepting agent lifecycle events.
/// Plugins return <see cref="IPluginHook"/> instances from <see cref="IPlugin.GetHooks"/>.
/// All methods have default implementations that pass through unchanged.
/// </summary>
public interface IPluginHook
{
    /// <summary>Modify the system prompt before it is sent to the LLM.</summary>
    Task<string> OnSystemPromptAsync(string prompt) => Task.FromResult(prompt);

    /// <summary>
    /// Called before each tool execution. Return false to block the call.
    /// </summary>
    Task<bool> OnBeforeToolCallAsync(string toolName, Dictionary<string, object?> args)
        => Task.FromResult(true);

    /// <summary>Called after each tool execution with the result.</summary>
    Task OnAfterToolCallAsync(string toolName, string result) => Task.CompletedTask;

    /// <summary>
    /// Called when a message is received, before the agent processes it.
    /// Return a non-null string to short-circuit processing and reply with that string.
    /// Return null to proceed normally.
    /// </summary>
    Task<string?> OnMessageReceivedAsync(InboundMessage msg) => Task.FromResult<string?>(null);
}
