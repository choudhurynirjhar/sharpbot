using Sharpbot.Cron;

namespace Sharpbot.Agent.Tools;

/// <summary>Tool to schedule reminders and recurring tasks.</summary>
public sealed class CronTool : ToolBase
{
    private readonly CronService _cron;
    private string _channel = "";
    private string _chatId = "";

    public CronTool(CronService cronService) => _cron = cronService;

    /// <summary>Set the current session context for delivery.</summary>
    public void SetContext(string channel, string chatId)
    {
        _channel = channel;
        _chatId = chatId;
    }

    public override string Name => "cron";
    public override string Description => "Schedule reminders and recurring tasks. Actions: add, list, remove.";
    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["action"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new[] { "add", "list", "remove" }, ["description"] = "Action to perform" },
            ["message"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Reminder message (for add)" },
            ["every_seconds"] = new Dictionary<string, object?> { ["type"] = "integer", ["description"] = "Interval in seconds (for recurring tasks)" },
            ["cron_expr"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Cron expression like '0 9 * * *' (for scheduled tasks)" },
            ["job_id"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Job ID (for remove)" },
        },
        ["required"] = new[] { "action" },
    };

    public override Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var action = GetString(args, "action");
        return action switch
        {
            "add" => Task.FromResult(AddJob(args)),
            "list" => Task.FromResult(ListJobs()),
            "remove" => Task.FromResult(RemoveJob(args)),
            _ => Task.FromResult($"Unknown action: {action}"),
        };
    }

    private string AddJob(Dictionary<string, object?> args)
    {
        var message = GetString(args, "message");
        if (string.IsNullOrEmpty(message)) return "Error: message is required for add";
        if (string.IsNullOrEmpty(_channel) || string.IsNullOrEmpty(_chatId))
            return "Error: no session context (channel/chat_id)";

        var everySeconds = GetInt(args, "every_seconds");
        var cronExpr = GetString(args, "cron_expr");

        CronSchedule schedule;
        if (everySeconds.HasValue)
            schedule = new CronSchedule { Kind = ScheduleKinds.Every, EveryMs = everySeconds.Value * 1000 };
        else if (!string.IsNullOrEmpty(cronExpr))
            schedule = new CronSchedule { Kind = ScheduleKinds.Cron, Expr = cronExpr };
        else
            return "Error: either every_seconds or cron_expr is required";

        var job = _cron.AddJob(
            name: message.Length > 30 ? message[..30] : message,
            schedule: schedule,
            message: message,
            deliver: true,
            channel: _channel,
            to: _chatId);

        return $"Created job '{job.Name}' (id: {job.Id})";
    }

    private string ListJobs()
    {
        var jobs = _cron.ListJobs();
        if (jobs.Count == 0) return "No scheduled jobs.";
        var lines = jobs.Select(j => $"- {j.Name} (id: {j.Id}, {j.Schedule.Kind})");
        return "Scheduled jobs:\n" + string.Join("\n", lines);
    }

    private string RemoveJob(Dictionary<string, object?> args)
    {
        var jobId = GetString(args, "job_id");
        if (string.IsNullOrEmpty(jobId)) return "Error: job_id is required for remove";
        return _cron.RemoveJob(jobId) ? $"Removed job {jobId}" : $"Job {jobId} not found";
    }
}
