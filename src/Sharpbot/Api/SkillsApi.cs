using Sharpbot.Services;

namespace Sharpbot.Api;

/// <summary>Skills API — list and inspect loaded skills.</summary>
public static class SkillsApi
{
    public static void MapSkillsApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/skills").WithTags("Skills");

        group.MapGet("/", ListSkills);
        group.MapGet("/{name}", GetSkill);
    }

    /// <summary>List all skills with metadata and availability status.</summary>
    private static IResult ListSkills(SharpbotHostedService gateway)
    {
        var skills = gateway.Agent?.Skills.ListAllSkills();
        if (skills is null)
            return Results.Json(new { skills = Array.Empty<object>() });

        var result = skills.Select(s => new
        {
            name = s.Name,
            description = s.Description,
            source = s.Source,
            available = s.Available,
            unavailableReason = s.UnavailableReason,
            path = s.Path,
            metadata = s.Metadata.Where(m => m.Key != "metadata").ToDictionary(m => m.Key, m => m.Value),
        }).ToList();

        return Results.Json(new
        {
            skills = result,
            total = result.Count,
            available = result.Count(s => s.available),
            unavailable = result.Count(s => !s.available),
        });
    }

    /// <summary>Get the full content of a skill by name.</summary>
    private static IResult GetSkill(string name, SharpbotHostedService gateway)
    {
        var skills = gateway.Agent?.Skills;
        if (skills is null)
            return Results.NotFound(new { error = true, message = "Agent not ready" });

        var allSkills = skills.ListAllSkills();
        var skill = allSkills.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
            return Results.NotFound(new { error = true, message = $"Skill '{name}' not found" });

        var content = skills.LoadSkill(name);

        return Results.Json(new
        {
            name = skill.Name,
            description = skill.Description,
            source = skill.Source,
            available = skill.Available,
            unavailableReason = skill.UnavailableReason,
            path = skill.Path,
            content = content ?? "",
        });
    }
}
