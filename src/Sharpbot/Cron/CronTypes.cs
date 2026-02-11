namespace Sharpbot.Cron;

/// <summary>Schedule definition for a cron job.</summary>
public sealed class CronSchedule
{
    public string Kind { get; init; } = ScheduleKinds.Every;
    public long? AtMs { get; init; }
    public long? EveryMs { get; init; }
    public string? Expr { get; init; }
    public string? Tz { get; init; }
}

/// <summary>What to do when the job runs.</summary>
public sealed class CronPayload
{
    public string Kind { get; init; } = PayloadKinds.AgentTurn;
    public string Message { get; init; } = "";
    public bool Deliver { get; init; }
    public string? Channel { get; init; }
    public string? To { get; init; }
}

/// <summary>Runtime state of a job (mutable — updated as jobs execute).</summary>
public sealed class CronJobState
{
    public long? NextRunAtMs { get; set; }
    public long? LastRunAtMs { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
}

/// <summary>A scheduled job.</summary>
public sealed class CronJob
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; set; } = true;
    public CronSchedule Schedule { get; init; } = new();
    public CronPayload Payload { get; init; } = new();
    public CronJobState State { get; init; } = new();
    public long CreatedAtMs { get; init; }
    public long UpdatedAtMs { get; set; }
    public bool DeleteAfterRun { get; init; }
}

/// <summary>Persistent store for cron jobs.</summary>
public sealed class CronStore
{
    public int Version { get; init; } = 1;
    public List<CronJob> Jobs { get; init; } = [];
}
