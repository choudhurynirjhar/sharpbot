using Sharpbot.Cron;
using Sharpbot.Services;

namespace Sharpbot.Api;

/// <summary>Cron API — manage scheduled jobs.</summary>
public static class CronApi
{
    public static void MapCronApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/cron").WithTags("Cron");

        group.MapGet("/", ListJobs);
        group.MapPost("/", AddJob);
        group.MapDelete("/{id}", RemoveJob);
        group.MapPut("/{id}/enable", EnableJob);
        group.MapPost("/{id}/run", RunJob);
    }

    private static IResult ListJobs(SharpbotHostedService gateway, bool includeDisabled = false)
    {
        var jobs = gateway.CronService.ListJobs(includeDisabled);
        var result = jobs.Select(j => new
        {
            id = j.Id,
            name = j.Name,
            enabled = j.Enabled,
            schedule = new
            {
                kind = j.Schedule.Kind,
                everyMs = j.Schedule.EveryMs,
                expr = j.Schedule.Expr,
                atMs = j.Schedule.AtMs,
            },
            payload = new
            {
                message = j.Payload.Message,
                deliver = j.Payload.Deliver,
                channel = j.Payload.Channel,
                to = j.Payload.To,
            },
            state = new
            {
                nextRunAtMs = j.State.NextRunAtMs,
                lastRunAtMs = j.State.LastRunAtMs,
                lastStatus = j.State.LastStatus,
                lastError = j.State.LastError,
            },
            createdAtMs = j.CreatedAtMs,
        });

        return Results.Json(new { jobs = result });
    }

    private static IResult AddJob(CronJobRequest request, SharpbotHostedService gateway)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = true, message = "Job name is required." });
        if (string.IsNullOrWhiteSpace(request.Message))
            return Results.BadRequest(new { error = true, message = "Message is required." });

        CronSchedule schedule;
        if (request.EverySeconds.HasValue)
        {
            schedule = new CronSchedule { Kind = ScheduleKinds.Every, EveryMs = request.EverySeconds.Value * 1000 };
        }
        else if (!string.IsNullOrEmpty(request.CronExpr))
        {
            schedule = new CronSchedule { Kind = ScheduleKinds.Cron, Expr = request.CronExpr };
        }
        else
        {
            return Results.BadRequest(new { error = true, message = "Must specify everySeconds or cronExpr." });
        }

        var job = gateway.CronService.AddJob(
            name: request.Name,
            schedule: schedule,
            message: request.Message,
            deliver: request.Deliver,
            channel: request.Channel,
            to: request.To);

        return Results.Json(new
        {
            success = true,
            job = new { id = job.Id, name = job.Name },
            message = $"Job '{job.Name}' added.",
        });
    }

    private static IResult RemoveJob(string id, SharpbotHostedService gateway)
    {
        var removed = gateway.CronService.RemoveJob(id);
        return removed
            ? Results.Json(new { success = true, message = $"Job {id} removed." })
            : Results.NotFound(new { error = true, message = $"Job {id} not found." });
    }

    private static IResult EnableJob(string id, CronEnableRequest request, SharpbotHostedService gateway)
    {
        var job = gateway.CronService.EnableJob(id, request.Enabled);
        return job is not null
            ? Results.Json(new { success = true, enabled = job.Enabled, message = $"Job {id} {(job.Enabled ? "enabled" : "disabled")}." })
            : Results.NotFound(new { error = true, message = $"Job {id} not found." });
    }

    private static async Task<IResult> RunJob(string id, SharpbotHostedService gateway)
    {
        var ran = await gateway.CronService.RunJobAsync(id, force: true);
        return ran
            ? Results.Json(new { success = true, message = $"Job {id} triggered." })
            : Results.NotFound(new { error = true, message = $"Job {id} not found." });
    }
}

public record CronJobRequest
{
    public string Name { get; init; } = "";
    public string Message { get; init; } = "";
    public int? EverySeconds { get; init; }
    public string? CronExpr { get; init; }
    public bool Deliver { get; init; }
    public string? Channel { get; init; }
    public string? To { get; init; }
}

public record CronEnableRequest
{
    public bool Enabled { get; init; } = true;
}
