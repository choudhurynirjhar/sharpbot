namespace Sharpbot.Agent.Tools;

/// <summary>
/// Tool that allows the agent to load the full content of a skill on demand.
/// Used with progressive skill loading: the agent sees skill summaries in the
/// system prompt and can call this tool to get the full instructions when needed.
/// </summary>
public sealed class LoadSkillTool : ToolBase
{
    private readonly SkillsLoader _skills;

    public LoadSkillTool(SkillsLoader skills)
    {
        _skills = skills;
    }

    public override string Name => "load_skill";

    public override string Description =>
        "Load the full instructions for a skill by name. " +
        "Use this when you see a skill in the available skills list that is relevant to the current task " +
        "and you need its full instructions to proceed.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["name"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The name of the skill to load (e.g. 'github', 'weather', 'cron').",
            },
        },
        ["required"] = new[] { "name" },
    };

    public override Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var name = GetString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult("Error: skill name is required.");

        // Verify the skill exists and is available
        var allSkills = _skills.ListAllSkills();
        var skill = allSkills.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
            return Task.FromResult($"Error: skill '{name}' not found. Use the skill names from the available skills list.");

        if (!skill.Available)
            return Task.FromResult(
                $"Error: skill '{name}' is not available. Reason: {skill.UnavailableReason ?? "unknown"}. " +
                "You may help the user install the missing requirements.");

        // Load the full content
        var content = _skills.LoadSkillsForContext([name]);
        if (string.IsNullOrEmpty(content))
            return Task.FromResult($"Error: could not read skill '{name}' content.");

        return Task.FromResult(content);
    }
}
