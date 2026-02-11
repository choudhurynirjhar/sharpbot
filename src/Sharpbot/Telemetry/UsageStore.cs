using Microsoft.Data.Sqlite;
using Sharpbot.Agent;
using Sharpbot.Database;

namespace Sharpbot.Telemetry;

/// <summary>
/// A single persisted usage record derived from <see cref="AgentTelemetry"/>.
/// </summary>
public sealed record UsageEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Channel { get; init; } = "";
    public string SessionKey { get; init; } = "";
    public string Model { get; init; } = "";
    public bool Success { get; init; } = true;
    public string? Error { get; init; }

    // LLM metrics
    public int Iterations { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
    public double LlmDurationMs { get; init; }

    // Tool metrics
    public int ToolCalls { get; init; }
    public int FailedToolCalls { get; init; }
    public double ToolDurationMs { get; init; }
    public List<string> ToolsUsed { get; set; } = [];

    // Overall
    public double TotalDurationMs { get; init; }
}

/// <summary>
/// Aggregated usage summary returned by the API.
/// </summary>
public sealed record UsageSummary
{
    public int TotalRequests { get; init; }
    public int SuccessfulRequests { get; init; }
    public int FailedRequests { get; init; }
    public long TotalTokens { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public int TotalToolCalls { get; init; }
    public double TotalDurationMs { get; init; }
    public List<ModelUsage> ByModel { get; init; } = [];
    public List<ToolUsage> ByTool { get; init; } = [];
    public List<ChannelUsage> ByChannel { get; init; } = [];
    public List<DailyUsage> Daily { get; init; } = [];
    public List<HourlyUsage> ByHour { get; init; } = [];
    public List<DayOfWeekUsage> ByDayOfWeek { get; init; } = [];
    public List<SessionSummary> Sessions { get; init; } = [];
}

public sealed record ModelUsage
{
    public string Model { get; init; } = "";
    public int Requests { get; init; }
    public long Tokens { get; init; }
    public double DurationMs { get; init; }
}

public sealed record ToolUsage
{
    public string Tool { get; init; } = "";
    public int Calls { get; init; }
    public int Failures { get; init; }
    public double DurationMs { get; init; }
}

public sealed record ChannelUsage
{
    public string Channel { get; init; } = "";
    public int Requests { get; init; }
    public long Tokens { get; init; }
}

public sealed record DailyUsage
{
    public string Date { get; init; } = "";
    public int Requests { get; init; }
    public long Tokens { get; init; }
    public int ToolCalls { get; init; }
}

public sealed record HourlyUsage
{
    public int Hour { get; init; }
    public int Requests { get; init; }
    public long Tokens { get; init; }
}

public sealed record DayOfWeekUsage
{
    public int Day { get; init; }       // 0 = Sunday … 6 = Saturday
    public int Requests { get; init; }
    public long Tokens { get; init; }
}

public sealed record SessionSummary
{
    public string SessionKey { get; init; } = "";
    public string Channel { get; init; } = "";
    public string Model { get; init; } = "";
    public int Messages { get; init; }
    public long Tokens { get; init; }
    public int ToolCalls { get; init; }
    public int Errors { get; init; }
    public double DurationMs { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
}

/// <summary>
/// SQLite-backed store for persisting agent usage telemetry.
/// Replaces the previous NDJSON file store with indexed SQL queries
/// for efficient aggregation and date-range filtering.
/// Thread-safe — SQLite WAL mode handles concurrent access.
/// </summary>
public sealed class UsageStore
{
    private readonly SharpbotDb _db;

    public UsageStore(SharpbotDb db)
    {
        _db = db;
    }

    /// <summary>Record a completed agent telemetry as a usage entry.</summary>
    public void Record(AgentTelemetry telemetry)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var timestamp = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO usage (id, timestamp, channel, session_key, model, success, error,
                    iterations, prompt_tokens, completion_tokens, total_tokens,
                    llm_duration_ms, tool_calls, failed_tool_calls, tool_duration_ms, total_duration_ms)
                VALUES (@id, @ts, @ch, @sk, @model, @success, @error,
                    @iter, @pt, @ct, @tt, @ld, @tc, @ftc, @td, @totd)
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@ts", timestamp.ToString("o"));
            cmd.Parameters.AddWithValue("@ch", telemetry.Channel);
            cmd.Parameters.AddWithValue("@sk", telemetry.SessionKey);
            cmd.Parameters.AddWithValue("@model", telemetry.Model);
            cmd.Parameters.AddWithValue("@success", telemetry.Success ? 1 : 0);
            cmd.Parameters.AddWithValue("@error", (object?)telemetry.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@iter", telemetry.Iterations);
            cmd.Parameters.AddWithValue("@pt", telemetry.TotalPromptTokens);
            cmd.Parameters.AddWithValue("@ct", telemetry.TotalCompletionTokens);
            cmd.Parameters.AddWithValue("@tt", telemetry.TotalTokens);
            cmd.Parameters.AddWithValue("@ld", telemetry.TotalLlmDuration.TotalMilliseconds);
            cmd.Parameters.AddWithValue("@tc", telemetry.TotalToolCalls);
            cmd.Parameters.AddWithValue("@ftc", telemetry.FailedToolCalls);
            cmd.Parameters.AddWithValue("@td", telemetry.TotalToolDuration.TotalMilliseconds);
            cmd.Parameters.AddWithValue("@totd", telemetry.TotalDuration.TotalMilliseconds);
            cmd.ExecuteNonQuery();
        }

        // Insert tool names
        if (telemetry.ToolsUsed.Count > 0)
        {
            using var toolCmd = conn.CreateCommand();
            toolCmd.Transaction = tx;
            toolCmd.CommandText = "INSERT INTO usage_tools (usage_id, tool_name) VALUES (@uid, @tool)";
            var uidParam = toolCmd.Parameters.Add("@uid", SqliteType.Text);
            var toolParam = toolCmd.Parameters.Add("@tool", SqliteType.Text);

            foreach (var tool in telemetry.ToolsUsed)
            {
                uidParam.Value = id;
                toolParam.Value = tool;
                toolCmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>Get all entries, optionally filtered by date range.</summary>
    public List<UsageEntry> GetEntries(DateTime? from = null, DateTime? to = null, int? limit = null)
    {
        using var conn = _db.CreateConnection();

        // Build WHERE clause
        var conditions = new List<string>();
        if (from.HasValue) conditions.Add("u.timestamp >= @from");
        if (to.HasValue) conditions.Add("u.timestamp <= @to");
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var limitClause = limit.HasValue ? $"LIMIT {limit.Value}" : "";

        // 1. Load main entries
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT u.id, u.timestamp, u.channel, u.session_key, u.model, u.success, u.error,
                   u.iterations, u.prompt_tokens, u.completion_tokens, u.total_tokens,
                   u.llm_duration_ms, u.tool_calls, u.failed_tool_calls, u.tool_duration_ms,
                   u.total_duration_ms
            FROM usage u
            {where}
            ORDER BY u.timestamp DESC
            {limitClause}
            """;
        if (from.HasValue) cmd.Parameters.AddWithValue("@from", from.Value.ToString("o"));
        if (to.HasValue) cmd.Parameters.AddWithValue("@to", to.Value.ToString("o"));

        var entries = new List<UsageEntry>();
        var entryIds = new List<string>();

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var entryId = reader.GetString(0);
                entries.Add(new UsageEntry
                {
                    Id = entryId,
                    Timestamp = DateTime.Parse(reader.GetString(1)),
                    Channel = reader.GetString(2),
                    SessionKey = reader.GetString(3),
                    Model = reader.GetString(4),
                    Success = reader.GetInt32(5) != 0,
                    Error = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Iterations = reader.GetInt32(7),
                    PromptTokens = reader.GetInt32(8),
                    CompletionTokens = reader.GetInt32(9),
                    TotalTokens = reader.GetInt32(10),
                    LlmDurationMs = reader.GetDouble(11),
                    ToolCalls = reader.GetInt32(12),
                    FailedToolCalls = reader.GetInt32(13),
                    ToolDurationMs = reader.GetDouble(14),
                    TotalDurationMs = reader.GetDouble(15),
                });
                entryIds.Add(entryId);
            }
        }

        // 2. Batch-load tools for all entries in one query
        if (entryIds.Count > 0)
        {
            var paramNames = string.Join(",", entryIds.Select((_, i) => $"@id{i}"));

            using var toolCmd = conn.CreateCommand();
            toolCmd.CommandText = $"SELECT usage_id, tool_name FROM usage_tools WHERE usage_id IN ({paramNames})";
            for (int i = 0; i < entryIds.Count; i++)
                toolCmd.Parameters.AddWithValue($"@id{i}", entryIds[i]);

            var toolMap = new Dictionary<string, List<string>>();
            using var toolReader = toolCmd.ExecuteReader();
            while (toolReader.Read())
            {
                var uid = toolReader.GetString(0);
                var tool = toolReader.GetString(1);
                if (!toolMap.ContainsKey(uid)) toolMap[uid] = [];
                toolMap[uid].Add(tool);
            }

            foreach (var entry in entries)
            {
                if (toolMap.TryGetValue(entry.Id, out var tools))
                    entry.ToolsUsed = tools;
            }
        }

        return entries;
    }

    /// <summary>Build an aggregated usage summary — all aggregation runs in SQL.</summary>
    public UsageSummary GetSummary(DateTime? from = null, DateTime? to = null)
    {
        using var conn = _db.CreateConnection();

        var conditions = new List<string>();
        if (from.HasValue) conditions.Add("timestamp >= @from");
        if (to.HasValue) conditions.Add("timestamp <= @to");
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        void AddDateParams(SqliteCommand c)
        {
            if (from.HasValue) c.Parameters.AddWithValue("@from", from.Value.ToString("o"));
            if (to.HasValue) c.Parameters.AddWithValue("@to", to.Value.ToString("o"));
        }

        // ── Overall stats ───────────────────────────────────────────
        using var overallCmd = conn.CreateCommand();
        overallCmd.CommandText = $"""
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(total_tokens), 0),
                   COALESCE(SUM(prompt_tokens), 0),
                   COALESCE(SUM(completion_tokens), 0),
                   COALESCE(SUM(tool_calls), 0),
                   COALESCE(SUM(total_duration_ms), 0)
            FROM usage {where}
            """;
        AddDateParams(overallCmd);

        int totalReq = 0, successReq = 0, failedReq = 0, totalToolCalls = 0;
        long totalTokens = 0, promptTokens = 0, completionTokens = 0;
        double totalDuration = 0;

        using (var r = overallCmd.ExecuteReader())
        {
            if (r.Read())
            {
                totalReq = r.GetInt32(0);
                successReq = r.GetInt32(1);
                failedReq = r.GetInt32(2);
                totalTokens = r.GetInt64(3);
                promptTokens = r.GetInt64(4);
                completionTokens = r.GetInt64(5);
                totalToolCalls = r.GetInt32(6);
                totalDuration = r.GetDouble(7);
            }
        }

        if (totalReq == 0) return new UsageSummary();

        // ── By model ────────────────────────────────────────────────
        using var modelCmd = conn.CreateCommand();
        modelCmd.CommandText = $"""
            SELECT model, COUNT(*), COALESCE(SUM(total_tokens), 0), COALESCE(SUM(llm_duration_ms), 0)
            FROM usage {where}
            GROUP BY model ORDER BY SUM(total_tokens) DESC
            """;
        AddDateParams(modelCmd);

        var byModel = new List<ModelUsage>();
        using (var r = modelCmd.ExecuteReader())
        {
            while (r.Read())
                byModel.Add(new ModelUsage
                {
                    Model = r.GetString(0),
                    Requests = r.GetInt32(1),
                    Tokens = r.GetInt64(2),
                    DurationMs = r.GetDouble(3),
                });
        }

        // ── By tool ─────────────────────────────────────────────────
        var toolWhere = where.Replace("timestamp", "u.timestamp");

        using var toolCmd = conn.CreateCommand();
        toolCmd.CommandText = $"""
            SELECT ut.tool_name, COUNT(DISTINCT ut.usage_id), 0, 0.0
            FROM usage_tools ut
            JOIN usage u ON u.id = ut.usage_id
            {toolWhere}
            GROUP BY ut.tool_name ORDER BY COUNT(*) DESC
            """;
        AddDateParams(toolCmd);

        var byTool = new List<ToolUsage>();
        using (var r = toolCmd.ExecuteReader())
        {
            while (r.Read())
                byTool.Add(new ToolUsage
                {
                    Tool = r.GetString(0),
                    Calls = r.GetInt32(1),
                    Failures = r.GetInt32(2),
                    DurationMs = r.GetDouble(3),
                });
        }

        // ── By channel ──────────────────────────────────────────────
        using var chCmd = conn.CreateCommand();
        chCmd.CommandText = $"""
            SELECT channel, COUNT(*), COALESCE(SUM(total_tokens), 0)
            FROM usage {where}
            GROUP BY channel ORDER BY COUNT(*) DESC
            """;
        AddDateParams(chCmd);

        var byChannel = new List<ChannelUsage>();
        using (var r = chCmd.ExecuteReader())
        {
            while (r.Read())
                byChannel.Add(new ChannelUsage
                {
                    Channel = r.GetString(0),
                    Requests = r.GetInt32(1),
                    Tokens = r.GetInt64(2),
                });
        }

        // ── Daily (last 30 days) ────────────────────────────────────
        using var dailyCmd = conn.CreateCommand();
        dailyCmd.CommandText = $"""
            SELECT SUBSTR(timestamp, 1, 10) AS date, COUNT(*),
                   COALESCE(SUM(total_tokens), 0), COALESCE(SUM(tool_calls), 0)
            FROM usage {where}
            GROUP BY date ORDER BY date DESC LIMIT 30
            """;
        AddDateParams(dailyCmd);

        var daily = new List<DailyUsage>();
        using (var r = dailyCmd.ExecuteReader())
        {
            while (r.Read())
                daily.Add(new DailyUsage
                {
                    Date = r.GetString(0),
                    Requests = r.GetInt32(1),
                    Tokens = r.GetInt64(2),
                    ToolCalls = r.GetInt32(3),
                });
        }
        daily.Reverse(); // oldest first

        // ── By hour of day ─────────────────────────────────────────
        using var hourCmd = conn.CreateCommand();
        hourCmd.CommandText = $"""
            SELECT CAST(SUBSTR(timestamp, 12, 2) AS INTEGER) AS hour,
                   COUNT(*), COALESCE(SUM(total_tokens), 0)
            FROM usage {where}
            GROUP BY hour ORDER BY hour
            """;
        AddDateParams(hourCmd);

        var byHour = new List<HourlyUsage>();
        using (var r = hourCmd.ExecuteReader())
        {
            while (r.Read())
                byHour.Add(new HourlyUsage
                {
                    Hour = r.GetInt32(0),
                    Requests = r.GetInt32(1),
                    Tokens = r.GetInt64(2),
                });
        }

        // ── By day of week ─────────────────────────────────────────
        using var dowCmd = conn.CreateCommand();
        dowCmd.CommandText = $"""
            SELECT CAST(strftime('%w', timestamp) AS INTEGER) AS dow,
                   COUNT(*), COALESCE(SUM(total_tokens), 0)
            FROM usage {where}
            GROUP BY dow ORDER BY dow
            """;
        AddDateParams(dowCmd);

        var byDow = new List<DayOfWeekUsage>();
        using (var r = dowCmd.ExecuteReader())
        {
            while (r.Read())
                byDow.Add(new DayOfWeekUsage
                {
                    Day = r.GetInt32(0),
                    Requests = r.GetInt32(1),
                    Tokens = r.GetInt64(2),
                });
        }

        // ── Sessions ────────────────────────────────────────────────
        using var sessCmd = conn.CreateCommand();
        sessCmd.CommandText = $"""
            SELECT session_key, channel,
                   GROUP_CONCAT(DISTINCT model) AS models,
                   COUNT(*) AS messages,
                   COALESCE(SUM(total_tokens), 0),
                   COALESCE(SUM(tool_calls), 0),
                   COALESCE(SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(total_duration_ms), 0),
                   MIN(timestamp), MAX(timestamp)
            FROM usage {where}
            GROUP BY session_key
            ORDER BY MAX(timestamp) DESC
            LIMIT 50
            """;
        AddDateParams(sessCmd);

        var sessions = new List<SessionSummary>();
        using (var r = sessCmd.ExecuteReader())
        {
            while (r.Read())
                sessions.Add(new SessionSummary
                {
                    SessionKey = r.GetString(0),
                    Channel = r.GetString(1),
                    Model = r.GetString(2),
                    Messages = r.GetInt32(3),
                    Tokens = r.GetInt64(4),
                    ToolCalls = r.GetInt32(5),
                    Errors = r.GetInt32(6),
                    DurationMs = r.GetDouble(7),
                    FirstSeen = DateTime.Parse(r.GetString(8)),
                    LastSeen = DateTime.Parse(r.GetString(9)),
                });
        }

        return new UsageSummary
        {
            TotalRequests = totalReq,
            SuccessfulRequests = successReq,
            FailedRequests = failedReq,
            TotalTokens = totalTokens,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalToolCalls = totalToolCalls,
            TotalDurationMs = totalDuration,
            ByModel = byModel,
            ByTool = byTool,
            ByChannel = byChannel,
            Daily = daily,
            ByHour = byHour,
            ByDayOfWeek = byDow,
            Sessions = sessions,
        };
    }

    /// <summary>Total number of stored entries.</summary>
    public int Count
    {
        get
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM usage";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Clear all stored data.</summary>
    public void Clear()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM usage_tools; DELETE FROM usage;";
        cmd.ExecuteNonQuery();
    }
}
