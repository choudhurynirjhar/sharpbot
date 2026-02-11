namespace Sharpbot.Cron;

/// <summary>
/// Constants for cron schedule kinds.
/// Eliminates magic strings used across CronService, CronTool, and CronCommands.
/// </summary>
public static class ScheduleKinds
{
    public const string At = "at";
    public const string Every = "every";
    public const string Cron = "cron";
}

/// <summary>
/// Constants for cron payload kinds.
/// </summary>
public static class PayloadKinds
{
    public const string AgentTurn = "agent_turn";
    public const string SystemEvent = "system_event";
}

/// <summary>
/// Constants for cron job execution status.
/// </summary>
public static class JobStatus
{
    public const string Ok = "ok";
    public const string Error = "error";
}
