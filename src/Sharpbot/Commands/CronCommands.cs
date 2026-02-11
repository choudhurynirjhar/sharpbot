using System.CommandLine;
using Sharpbot.Config;
using Sharpbot.Cron;
using Spectre.Console;

namespace Sharpbot.Commands;

/// <summary>CLI command: manage scheduled tasks.</summary>
public sealed class CronCommand : Command
{
    public CronCommand() : base("cron", "Manage scheduled tasks.")
    {
        Subcommands.Add(new CronListCommand());
        Subcommands.Add(new CronAddCommand());
        Subcommands.Add(new CronRemoveCommand());
        Subcommands.Add(new CronEnableCommand());
        Subcommands.Add(new CronDisableCommand());
        Subcommands.Add(new CronRunCommand());
    }
}

// ------------------------------------------------------------------
// Helper: creates a CronService from the standard store path.
// ------------------------------------------------------------------

file static class CronCommandHelpers
{
    public static CronService CreateService()
    {
        var db = Database.SharpbotDb.CreateDefault();
        return new CronService(db);
    }
}

// ------------------------------------------------------------------
// cron list
// ------------------------------------------------------------------

file sealed class CronListCommand : Command
{
    private readonly Option<bool> _allOption = new("--all", "-a")
    {
        Description = "Include disabled jobs"
    };

    public CronListCommand() : base("list", "List scheduled jobs.")
    {
        Options.Add(_allOption);

        this.SetAction(parseResult =>
        {
            var all = parseResult.GetValue(_allOption);
            Execute(all);
        });
    }

    private static void Execute(bool all)
    {
        var service = CronCommandHelpers.CreateService();

        var jobs = service.ListJobs(includeDisabled: all);
        if (jobs.Count == 0)
        {
            AnsiConsole.MarkupLine("No scheduled jobs.");
            return;
        }

        var table = new Table().Title("Scheduled Jobs");
        table.AddColumn("[cyan]ID[/]");
        table.AddColumn("Name");
        table.AddColumn("Schedule");
        table.AddColumn("Status");
        table.AddColumn("Next Run");

        foreach (var job in jobs)
        {
            var sched = job.Schedule.Kind switch
            {
                ScheduleKinds.Every => $"every {(job.Schedule.EveryMs ?? 0) / 1000}s",
                ScheduleKinds.Cron => job.Schedule.Expr ?? "",
                _ => "one-time",
            };

            var nextRun = job.State.NextRunAtMs.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(job.State.NextRunAtMs.Value)
                    .LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                : "";

            var status = job.Enabled ? "[green]enabled[/]" : "[dim]disabled[/]";
            table.AddRow(job.Id, job.Name, sched, status, nextRun);
        }

        AnsiConsole.Write(table);
    }
}

// ------------------------------------------------------------------
// cron add
// ------------------------------------------------------------------

file sealed class CronAddCommand : Command
{
    private readonly Option<string> _nameOption = new("--name", "-n")
    {
        Description = "Job name",
        Required = true
    };

    private readonly Option<string> _messageOption = new("--message", "-m")
    {
        Description = "Message for agent",
        Required = true
    };

    private readonly Option<int?> _everyOption = new("--every", "-e")
    {
        Description = "Run every N seconds"
    };

    private readonly Option<string?> _cronExprOption = new("--cron", "-c")
    {
        Description = "Cron expression (e.g. '0 9 * * *')"
    };

    public CronAddCommand() : base("add", "Add a scheduled job.")
    {
        Options.Add(_nameOption);
        Options.Add(_messageOption);
        Options.Add(_everyOption);
        Options.Add(_cronExprOption);

        this.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(_nameOption)!;
            var message = parseResult.GetValue(_messageOption)!;
            var every = parseResult.GetValue(_everyOption);
            var cronExpr = parseResult.GetValue(_cronExprOption);
            Execute(name, message, every, cronExpr);
        });
    }

    private static void Execute(string name, string message, int? every, string? cronExpr)
    {
        CronSchedule schedule;

        if (every.HasValue)
            schedule = new CronSchedule { Kind = ScheduleKinds.Every, EveryMs = every.Value * 1000 };
        else if (!string.IsNullOrEmpty(cronExpr))
            schedule = new CronSchedule { Kind = ScheduleKinds.Cron, Expr = cronExpr };
        else
        {
            AnsiConsole.MarkupLine("[red]Error: Must specify --every or --cron[/]");
            return;
        }

        var service = CronCommandHelpers.CreateService();
        var job = service.AddJob(name: name, schedule: schedule, message: message);

        AnsiConsole.MarkupLine($"[green]✓[/] Added job '{job.Name}' ({job.Id})");
    }
}

// ------------------------------------------------------------------
// cron remove
// ------------------------------------------------------------------

file sealed class CronRemoveCommand : Command
{
    private readonly Argument<string> _jobIdArg = new("job-id")
    {
        Description = "Job ID to remove"
    };

    public CronRemoveCommand() : base("remove", "Remove a scheduled job.")
    {
        Arguments.Add(_jobIdArg);

        this.SetAction(parseResult =>
        {
            var jobId = parseResult.GetValue(_jobIdArg)!;
            Execute(jobId);
        });
    }

    private static void Execute(string jobId)
    {
        var service = CronCommandHelpers.CreateService();

        if (service.RemoveJob(jobId))
            AnsiConsole.MarkupLine($"[green]✓[/] Removed job {jobId}");
        else
            AnsiConsole.MarkupLine($"[red]Job {jobId} not found[/]");
    }
}

// ------------------------------------------------------------------
// cron enable
// ------------------------------------------------------------------

file sealed class CronEnableCommand : Command
{
    private readonly Argument<string> _jobIdArg = new("job-id")
    {
        Description = "Job ID to enable"
    };

    public CronEnableCommand() : base("enable", "Enable a disabled scheduled job.")
    {
        Arguments.Add(_jobIdArg);

        this.SetAction(parseResult =>
        {
            var jobId = parseResult.GetValue(_jobIdArg)!;
            Execute(jobId);
        });
    }

    private static void Execute(string jobId)
    {
        var service = CronCommandHelpers.CreateService();
        var job = service.EnableJob(jobId, enabled: true);

        if (job is null)
        {
            AnsiConsole.MarkupLine($"[red]Job {jobId} not found[/]");
            return;
        }

        var nextRun = job.State.NextRunAtMs.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(job.State.NextRunAtMs.Value)
                .LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : "N/A";

        AnsiConsole.MarkupLine($"[green]✓[/] Enabled job '{job.Name}' ({job.Id})");
        AnsiConsole.MarkupLine($"  Next run: {nextRun}");
    }
}

// ------------------------------------------------------------------
// cron disable
// ------------------------------------------------------------------

file sealed class CronDisableCommand : Command
{
    private readonly Argument<string> _jobIdArg = new("job-id")
    {
        Description = "Job ID to disable"
    };

    public CronDisableCommand() : base("disable", "Disable a scheduled job.")
    {
        Arguments.Add(_jobIdArg);

        this.SetAction(parseResult =>
        {
            var jobId = parseResult.GetValue(_jobIdArg)!;
            Execute(jobId);
        });
    }

    private static void Execute(string jobId)
    {
        var service = CronCommandHelpers.CreateService();
        var job = service.EnableJob(jobId, enabled: false);

        if (job is null)
        {
            AnsiConsole.MarkupLine($"[red]Job {jobId} not found[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[green]✓[/] Disabled job '{job.Name}' ({job.Id})");
    }
}

// ------------------------------------------------------------------
// cron run
// ------------------------------------------------------------------

file sealed class CronRunCommand : Command
{
    private readonly Argument<string> _jobIdArg = new("job-id")
    {
        Description = "Job ID to run manually"
    };

    private readonly Option<bool> _forceOption = new("--force", "-f")
    {
        Description = "Run even if the job is disabled"
    };

    public CronRunCommand() : base("run", "Manually trigger a scheduled job.")
    {
        Arguments.Add(_jobIdArg);
        Options.Add(_forceOption);

        this.SetAction(async (parseResult, _) =>
        {
            var jobId = parseResult.GetValue(_jobIdArg)!;
            var force = parseResult.GetValue(_forceOption);
            await ExecuteAsync(jobId, force);
        });
    }

    private static async Task ExecuteAsync(string jobId, bool force)
    {
        var service = CronCommandHelpers.CreateService();

        // Peek at the job to show its name
        var jobs = service.ListJobs(includeDisabled: true);
        var target = jobs.FirstOrDefault(j => j.Id == jobId);

        if (target is null)
        {
            AnsiConsole.MarkupLine($"[red]Job {jobId} not found[/]");
            return;
        }

        if (!target.Enabled && !force)
        {
            AnsiConsole.MarkupLine($"[yellow]Job '{target.Name}' is disabled. Use --force to run anyway.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"Triggering job '{target.Name}' ({jobId})...");

        // Note: Without a running gateway, OnJob callback is not set,
        // so this will just update the job state (lastRun, status).
        // Full execution requires the gateway to be running.
        var ran = await service.RunJobAsync(jobId, force);

        if (ran)
            AnsiConsole.MarkupLine($"[green]✓[/] Job '{target.Name}' triggered successfully.");
        else
            AnsiConsole.MarkupLine($"[red]Failed to trigger job {jobId}.[/]");
    }
}
