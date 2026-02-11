using Sharpbot.Channels;

namespace Sharpbot.Agent.Tools;

/// <summary>
/// Tool to spawn a subagent for background task execution.
/// The subagent runs asynchronously and announces its result back
/// to the main agent when complete.
/// </summary>
public sealed class SpawnTool : ToolBase
{
    private readonly Agent.SubagentManager _manager;
    private string _originChannel = WellKnown.Cli;
    private string _originChatId = WellKnown.Direct;

    public SpawnTool(SubagentManager manager) => _manager = manager;

    /// <summary>Set the origin context for subagent announcements.</summary>
    public void SetContext(string channel, string chatId)
    {
        _originChannel = channel;
        _originChatId = chatId;
    }

    public override string Name => "spawn";
    public override string Description =>
        "Spawn a subagent to handle a task in the background. " +
        "Use this for complex or time-consuming tasks that can run independently. " +
        "The subagent will complete the task and report back when done.";
    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["task"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The task for the subagent to complete" },
            ["label"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Optional short label for the task (for display)" },
        },
        ["required"] = new[] { "task" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var task = GetString(args, "task");
        var label = GetString(args, "label");
        return await _manager.SpawnAsync(
            task: task,
            label: string.IsNullOrEmpty(label) ? null : label,
            originChannel: _originChannel,
            originChatId: _originChatId);
    }
}
