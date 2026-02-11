using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Sharpbot.Database;

namespace Sharpbot.Cron;

/// <summary>
/// Service for managing and executing scheduled jobs.
/// Backed by SQLite for atomic per-job persistence.
/// </summary>
public sealed class CronService : IDisposable
{
    private readonly SharpbotDb _db;
    private CancellationTokenSource? _timerCts;
    private Task? _timerTask;
    private volatile bool _running;
    private readonly ILogger? _logger;

    /// <summary>Callback to execute a job, returns response text.</summary>
    public Func<CronJob, Task<string?>>? OnJob { get; set; }

    public CronService(SharpbotDb db, ILogger? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static long? ComputeNextRun(CronSchedule schedule, long nowMs)
    {
        if (schedule.Kind == ScheduleKinds.At)
            return schedule.AtMs.HasValue && schedule.AtMs.Value > nowMs ? schedule.AtMs.Value : null;

        if (schedule.Kind == ScheduleKinds.Every)
        {
            if (!schedule.EveryMs.HasValue || schedule.EveryMs.Value <= 0) return null;
            return nowMs + schedule.EveryMs.Value;
        }

        if (schedule.Kind == ScheduleKinds.Cron && !string.IsNullOrEmpty(schedule.Expr))
        {
            try
            {
                var cronExpr = Cronos.CronExpression.Parse(schedule.Expr);
                var next = cronExpr.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
                return next?.ToUnixTimeMilliseconds();
            }
            catch { return null; }
        }

        return null;
    }

    // ========== DB helpers ==========

    private CronJob ReadJob(SqliteDataReader r)
    {
        return new CronJob
        {
            Id = r.GetString(r.GetOrdinal("id")),
            Name = r.GetString(r.GetOrdinal("name")),
            Enabled = r.GetInt32(r.GetOrdinal("enabled")) != 0,
            Schedule = new CronSchedule
            {
                Kind = r.GetString(r.GetOrdinal("schedule_kind")),
                AtMs = r.IsDBNull(r.GetOrdinal("schedule_at_ms")) ? null : r.GetInt64(r.GetOrdinal("schedule_at_ms")),
                EveryMs = r.IsDBNull(r.GetOrdinal("schedule_every_ms")) ? null : r.GetInt64(r.GetOrdinal("schedule_every_ms")),
                Expr = r.IsDBNull(r.GetOrdinal("schedule_expr")) ? null : r.GetString(r.GetOrdinal("schedule_expr")),
                Tz = r.IsDBNull(r.GetOrdinal("schedule_tz")) ? null : r.GetString(r.GetOrdinal("schedule_tz")),
            },
            Payload = new CronPayload
            {
                Kind = r.GetString(r.GetOrdinal("payload_kind")),
                Message = r.GetString(r.GetOrdinal("payload_message")),
                Deliver = r.GetInt32(r.GetOrdinal("payload_deliver")) != 0,
                Channel = r.IsDBNull(r.GetOrdinal("payload_channel")) ? null : r.GetString(r.GetOrdinal("payload_channel")),
                To = r.IsDBNull(r.GetOrdinal("payload_to")) ? null : r.GetString(r.GetOrdinal("payload_to")),
            },
            State = new CronJobState
            {
                NextRunAtMs = r.IsDBNull(r.GetOrdinal("next_run_at_ms")) ? null : r.GetInt64(r.GetOrdinal("next_run_at_ms")),
                LastRunAtMs = r.IsDBNull(r.GetOrdinal("last_run_at_ms")) ? null : r.GetInt64(r.GetOrdinal("last_run_at_ms")),
                LastStatus = r.IsDBNull(r.GetOrdinal("last_status")) ? null : r.GetString(r.GetOrdinal("last_status")),
                LastError = r.IsDBNull(r.GetOrdinal("last_error")) ? null : r.GetString(r.GetOrdinal("last_error")),
            },
            CreatedAtMs = r.GetInt64(r.GetOrdinal("created_at_ms")),
            UpdatedAtMs = r.GetInt64(r.GetOrdinal("updated_at_ms")),
            DeleteAfterRun = r.GetInt32(r.GetOrdinal("delete_after_run")) != 0,
        };
    }

    private CronJob? GetJobById(string jobId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM cron_jobs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", jobId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadJob(r) : null;
    }

    private void UpdateJobState(CronJob job)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE cron_jobs SET
                enabled = @enabled,
                next_run_at_ms = @next,
                last_run_at_ms = @last,
                last_status = @status,
                last_error = @error,
                updated_at_ms = @updated
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", job.Id);
        cmd.Parameters.AddWithValue("@enabled", job.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@next", (object?)job.State.NextRunAtMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last", (object?)job.State.LastRunAtMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", (object?)job.State.LastStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)job.State.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@updated", job.UpdatedAtMs);
        cmd.ExecuteNonQuery();
    }

    // ========== Lifecycle ==========

    /// <summary>Start the cron service.</summary>
    public async Task StartAsync()
    {
        _running = true;
        RecomputeNextRuns();
        ArmTimer();

        var count = ListJobs(includeDisabled: true).Count;
        _logger?.LogInformation("Cron service started with {Count} jobs", count);
        await Task.CompletedTask;
    }

    /// <summary>Stop the cron service.</summary>
    public void Stop()
    {
        _running = false;
        _timerCts?.Cancel();
        _timerCts = null;
    }

    private void RecomputeNextRuns()
    {
        using var conn = _db.CreateConnection();

        // Load all enabled jobs
        using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = "SELECT * FROM cron_jobs WHERE enabled = 1";
        var jobs = new List<CronJob>();
        using (var r = selectCmd.ExecuteReader())
        {
            while (r.Read()) jobs.Add(ReadJob(r));
        }

        // Update next run times
        var now = NowMs();
        foreach (var job in jobs)
        {
            var nextRun = ComputeNextRun(job.Schedule, now);

            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE cron_jobs SET next_run_at_ms = @next WHERE id = @id";
            updateCmd.Parameters.AddWithValue("@next", (object?)nextRun ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("@id", job.Id);
            updateCmd.ExecuteNonQuery();
        }
    }

    private long? GetNextWakeMs()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(next_run_at_ms) FROM cron_jobs WHERE enabled = 1 AND next_run_at_ms IS NOT NULL";
        var result = cmd.ExecuteScalar();
        return result is long val ? val : null;
    }

    private void ArmTimer()
    {
        _timerCts?.Cancel();
        var nextWake = GetNextWakeMs();
        if (!nextWake.HasValue || !_running) return;

        var delayMs = Math.Max(0, nextWake.Value - NowMs());
        _timerCts = new CancellationTokenSource();
        var ct = _timerCts.Token;

        _timerTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
                if (_running) await OnTimerAsync();
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private async Task OnTimerAsync()
    {
        var now = NowMs();

        // Find due jobs
        List<CronJob> dueJobs;
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM cron_jobs WHERE enabled = 1 AND next_run_at_ms IS NOT NULL AND next_run_at_ms <= @now";
            cmd.Parameters.AddWithValue("@now", now);

            dueJobs = [];
            using var r = cmd.ExecuteReader();
            while (r.Read()) dueJobs.Add(ReadJob(r));
        }

        foreach (var job in dueJobs)
            await ExecuteJobAsync(job);

        ArmTimer();
    }

    private async Task ExecuteJobAsync(CronJob job)
    {
        var startMs = NowMs();
        _logger?.LogInformation("Cron: executing job '{Name}' ({Id})", job.Name, job.Id);

        try
        {
            if (OnJob != null)
                await OnJob(job);

            job.State.LastStatus = JobStatus.Ok;
            job.State.LastError = null;
            _logger?.LogInformation("Cron: job '{Name}' completed", job.Name);
        }
        catch (Exception e)
        {
            job.State.LastStatus = JobStatus.Error;
            job.State.LastError = e.Message;
            _logger?.LogError(e, "Cron: job '{Name}' failed", job.Name);
        }

        job.State.LastRunAtMs = startMs;
        job.UpdatedAtMs = NowMs();

        if (job.Schedule.Kind == ScheduleKinds.At)
        {
            if (job.DeleteAfterRun)
            {
                RemoveJob(job.Id);
                return;
            }
            else
            {
                job.Enabled = false;
                job.State.NextRunAtMs = null;
            }
        }
        else
        {
            job.State.NextRunAtMs = ComputeNextRun(job.Schedule, NowMs());
        }

        UpdateJobState(job);
    }

    // ========== Public API ==========

    /// <summary>List all jobs.</summary>
    public List<CronJob> ListJobs(bool includeDisabled = false)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = includeDisabled
            ? "SELECT * FROM cron_jobs ORDER BY next_run_at_ms ASC"
            : "SELECT * FROM cron_jobs WHERE enabled = 1 ORDER BY next_run_at_ms ASC";

        var jobs = new List<CronJob>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) jobs.Add(ReadJob(r));
        return jobs;
    }

    /// <summary>Add a new job.</summary>
    public CronJob AddJob(
        string name,
        CronSchedule schedule,
        string message,
        bool deliver = false,
        string? channel = null,
        string? to = null,
        bool deleteAfterRun = false)
    {
        var now = NowMs();
        var nextRun = ComputeNextRun(schedule, now);

        var job = new CronJob
        {
            Id = Guid.NewGuid().ToString()[..8],
            Name = name,
            Enabled = true,
            Schedule = schedule,
            Payload = new CronPayload
            {
                Kind = PayloadKinds.AgentTurn,
                Message = message,
                Deliver = deliver,
                Channel = channel,
                To = to,
            },
            State = new CronJobState { NextRunAtMs = nextRun },
            CreatedAtMs = now,
            UpdatedAtMs = now,
            DeleteAfterRun = deleteAfterRun,
        };

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cron_jobs (id, name, enabled, schedule_kind, schedule_at_ms, schedule_every_ms,
                schedule_expr, schedule_tz, payload_kind, payload_message, payload_deliver,
                payload_channel, payload_to, next_run_at_ms, last_run_at_ms, last_status,
                last_error, created_at_ms, updated_at_ms, delete_after_run)
            VALUES (@id, @name, @enabled, @sk, @sam, @sem, @se, @stz, @pk, @pm, @pd, @pc, @pt,
                @nram, @lram, @ls, @le, @cam, @uam, @dar)
            """;
        cmd.Parameters.AddWithValue("@id", job.Id);
        cmd.Parameters.AddWithValue("@name", job.Name);
        cmd.Parameters.AddWithValue("@enabled", 1);
        cmd.Parameters.AddWithValue("@sk", schedule.Kind);
        cmd.Parameters.AddWithValue("@sam", (object?)schedule.AtMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sem", (object?)schedule.EveryMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@se", (object?)schedule.Expr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stz", (object?)schedule.Tz ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pk", job.Payload.Kind);
        cmd.Parameters.AddWithValue("@pm", message);
        cmd.Parameters.AddWithValue("@pd", deliver ? 1 : 0);
        cmd.Parameters.AddWithValue("@pc", (object?)channel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pt", (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nram", (object?)nextRun ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lram", DBNull.Value);
        cmd.Parameters.AddWithValue("@ls", DBNull.Value);
        cmd.Parameters.AddWithValue("@le", DBNull.Value);
        cmd.Parameters.AddWithValue("@cam", now);
        cmd.Parameters.AddWithValue("@uam", now);
        cmd.Parameters.AddWithValue("@dar", deleteAfterRun ? 1 : 0);
        cmd.ExecuteNonQuery();

        ArmTimer();
        _logger?.LogInformation("Cron: added job '{Name}' ({Id})", name, job.Id);
        return job;
    }

    /// <summary>Remove a job by ID.</summary>
    public bool RemoveJob(string jobId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM cron_jobs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", jobId);
        var removed = cmd.ExecuteNonQuery() > 0;

        if (removed)
        {
            ArmTimer();
            _logger?.LogInformation("Cron: removed job {JobId}", jobId);
        }
        return removed;
    }

    /// <summary>Enable or disable a job.</summary>
    public CronJob? EnableJob(string jobId, bool enabled = true)
    {
        var job = GetJobById(jobId);
        if (job == null) return null;

        job.Enabled = enabled;
        job.UpdatedAtMs = NowMs();
        job.State.NextRunAtMs = enabled ? ComputeNextRun(job.Schedule, NowMs()) : null;
        UpdateJobState(job);
        ArmTimer();
        return job;
    }

    /// <summary>Manually run a job.</summary>
    public async Task<bool> RunJobAsync(string jobId, bool force = false)
    {
        var job = GetJobById(jobId);
        if (job == null || (!force && !job.Enabled)) return false;

        await ExecuteJobAsync(job);
        ArmTimer();
        return true;
    }

    /// <summary>Get service status.</summary>
    public Dictionary<string, object?> Status()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM cron_jobs";
        var count = Convert.ToInt32(cmd.ExecuteScalar());

        return new()
        {
            ["enabled"] = _running,
            ["jobs"] = count,
            ["next_wake_at_ms"] = GetNextWakeMs(),
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _running = false;
        _timerCts?.Cancel();
        _timerCts?.Dispose();
    }
}
