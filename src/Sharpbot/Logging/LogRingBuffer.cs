using System.Collections.Concurrent;
using Sharpbot.Database;

namespace Sharpbot.Logging;

/// <summary>
/// A single log entry captured by the ring buffer.
/// </summary>
public sealed record LogEntry
{
    public required long Id { get; init; }
    public required DateTime Timestamp { get; init; }
    public required LogLevel Level { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public string? Exception { get; init; }
}

/// <summary>
/// In-memory circular buffer that stores the last N log entries.
/// Optionally persists all entries to SQLite for cross-restart history.
/// Thread-safe for concurrent writers and readers.
/// </summary>
public sealed class LogRingBuffer
{
    private readonly LogEntry[] _buffer;
    private readonly int _capacity;
    private long _nextId;
    private int _head;   // next write position
    private int _count;
    private readonly object _lock = new();

    private readonly SharpbotDb? _db;

    /// <summary>Maximum number of rows to keep in the SQLite logs table.</summary>
    private const int MaxPersistedRows = 50_000;

    /// <summary>Maximum age of persisted log entries.</summary>
    private static readonly TimeSpan MaxLogAge = TimeSpan.FromDays(7);

    /// <summary>How often to run the pruning check (every N inserts).</summary>
    private const int PruneInterval = 500;

    /// <summary>Counter tracking inserts since the last prune check.</summary>
    private int _insertsSinceLastPrune;

    public LogRingBuffer(int capacity = 1000, SharpbotDb? db = null)
    {
        _capacity = capacity;
        _buffer = new LogEntry[capacity];
        _db = db;

        // If we have a database, seed _nextId from the max persisted ID
        // so IDs are globally monotonic across restarts.
        if (_db != null)
        {
            try
            {
                using var conn = _db.CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM logs";
                _nextId = Convert.ToInt64(cmd.ExecuteScalar());
            }
            catch { /* start from 0 if table doesn't exist yet */ }

            // Prune on startup: remove old entries + enforce row cap
            PrunePersisted();
        }
    }

    /// <summary>Add a log entry to the buffer (and optionally persist to SQLite).</summary>
    public void Add(LogLevel level, string category, string message, string? exception = null)
    {
        var entry = new LogEntry
        {
            Id = Interlocked.Increment(ref _nextId),
            Timestamp = DateTime.UtcNow,
            Level = level,
            Category = category,
            Message = message,
            Exception = exception,
        };

        // Write to in-memory ring buffer
        lock (_lock)
        {
            _buffer[_head] = entry;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }

        // Persist to SQLite (fire-and-forget; don't block the logger)
        if (_db != null)
        {
            try
            {
                using var conn = _db.CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO logs (id, timestamp, level, level_name, category, message, exception)
                    VALUES (@id, @ts, @lvl, @lvlName, @cat, @msg, @exc)
                    """;
                cmd.Parameters.AddWithValue("@id", entry.Id);
                cmd.Parameters.AddWithValue("@ts", entry.Timestamp.ToString("o"));
                cmd.Parameters.AddWithValue("@lvl", (int)entry.Level);
                cmd.Parameters.AddWithValue("@lvlName", entry.Level.ToString());
                cmd.Parameters.AddWithValue("@cat", entry.Category);
                cmd.Parameters.AddWithValue("@msg", entry.Message);
                cmd.Parameters.AddWithValue("@exc", (object?)entry.Exception ?? DBNull.Value);
                cmd.ExecuteNonQuery();

                // Periodically prune old rows to keep the DB size bounded
                if (Interlocked.Increment(ref _insertsSinceLastPrune) >= PruneInterval)
                {
                    Interlocked.Exchange(ref _insertsSinceLastPrune, 0);
                    PrunePersisted();
                }
            }
            catch
            {
                // Never let log persistence failures crash the application
            }
        }
    }

    /// <summary>
    /// Get log entries, optionally filtered.
    /// Uses the in-memory buffer for real-time polling (fast),
    /// falls back to SQLite for entries no longer in memory.
    /// </summary>
    public List<LogEntry> GetEntries(
        LogLevel? minLevel = null,
        string? category = null,
        string? search = null,
        int limit = 200,
        long? afterId = null)
    {
        // If afterId is provided and the entry might be outside the ring buffer,
        // try SQLite first for a complete result
        if (_db != null && afterId.HasValue)
        {
            long oldestInBuffer;
            lock (_lock)
            {
                if (_count == 0) return [];
                var oldestIdx = _count < _capacity ? 0 : _head;
                oldestInBuffer = _buffer[oldestIdx]?.Id ?? 0;
            }

            // If the requested afterId is older than what's in the buffer, use SQLite
            if (afterId.Value < oldestInBuffer)
                return GetPersistedEntries(minLevel, category, search, limit, afterId);
        }

        // Use in-memory buffer (fast path)
        List<LogEntry> snapshot;
        lock (_lock)
        {
            snapshot = new List<LogEntry>(_count);
            if (_count == 0) return snapshot;

            var start = _count < _capacity ? 0 : _head;
            for (int i = 0; i < _count; i++)
            {
                var idx = (start + i) % _capacity;
                var entry = _buffer[idx];
                if (entry != null) snapshot.Add(entry);
            }
        }

        // Apply filters
        IEnumerable<LogEntry> filtered = snapshot;

        if (afterId.HasValue)
            filtered = filtered.Where(e => e.Id > afterId.Value);

        if (minLevel.HasValue)
            filtered = filtered.Where(e => e.Level >= minLevel.Value);

        if (!string.IsNullOrEmpty(category))
            filtered = filtered.Where(e =>
                e.Category.Contains(category, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(search))
            filtered = filtered.Where(e =>
                e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

        return filtered.TakeLast(limit).ToList();
    }

    /// <summary>Query persisted logs from SQLite.</summary>
    private List<LogEntry> GetPersistedEntries(
        LogLevel? minLevel, string? category, string? search, int limit, long? afterId)
    {
        if (_db == null) return [];

        try
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();

            var conditions = new List<string>();
            if (afterId.HasValue)
            {
                conditions.Add("id > @afterId");
                cmd.Parameters.AddWithValue("@afterId", afterId.Value);
            }
            if (minLevel.HasValue)
            {
                conditions.Add("level >= @minLevel");
                cmd.Parameters.AddWithValue("@minLevel", (int)minLevel.Value);
            }
            if (!string.IsNullOrEmpty(category))
            {
                conditions.Add("category LIKE @cat");
                cmd.Parameters.AddWithValue("@cat", $"%{category}%");
            }
            if (!string.IsNullOrEmpty(search))
            {
                conditions.Add("(message LIKE @search OR exception LIKE @search)");
                cmd.Parameters.AddWithValue("@search", $"%{search}%");
            }

            var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

            // Get the last N entries matching the filters
            cmd.CommandText = $"""
                SELECT id, timestamp, level, category, message, exception
                FROM logs {where}
                ORDER BY id DESC LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@limit", limit);

            var entries = new List<LogEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new LogEntry
                {
                    Id = reader.GetInt64(0),
                    Timestamp = DateTime.Parse(reader.GetString(1)),
                    Level = (LogLevel)reader.GetInt32(2),
                    Category = reader.GetString(3),
                    Message = reader.GetString(4),
                    Exception = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }

            entries.Reverse(); // oldest first
            return entries;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Total entries ever written (monotonic across restarts with SQLite).</summary>
    public long TotalEntries => Interlocked.Read(ref _nextId);

    /// <summary>Current entries in the in-memory buffer.</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>Clear the in-memory buffer and optionally the persisted logs.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer);
            _head = 0;
            _count = 0;
        }

        if (_db != null)
        {
            try
            {
                using var conn = _db.CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM logs";
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    /// <summary>
    /// Prune persisted logs to stay within size limits.
    /// Enforces two caps:
    ///   1. Time-based — delete entries older than <see cref="MaxLogAge"/> (7 days).
    ///   2. Row-based  — keep at most <see cref="MaxPersistedRows"/> (50 000) rows.
    /// This runs on startup and every <see cref="PruneInterval"/> inserts.
    /// </summary>
    private void PrunePersisted()
    {
        if (_db == null) return;
        try
        {
            using var conn = _db.CreateConnection();
            var deleted = 0;

            // 1. Remove entries older than the age limit
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM logs WHERE timestamp < @cutoff";
                cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.Subtract(MaxLogAge).ToString("o"));
                deleted += cmd.ExecuteNonQuery();
            }

            // 2. Enforce the hard row cap — keep only the most recent rows
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    DELETE FROM logs WHERE id NOT IN (
                        SELECT id FROM logs ORDER BY id DESC LIMIT @maxRows
                    )
                    """;
                cmd.Parameters.AddWithValue("@maxRows", MaxPersistedRows);
                deleted += cmd.ExecuteNonQuery();
            }
        }
        catch { }
    }
}

/// <summary>
/// Logger provider that writes to the ring buffer.
/// Register as an additional provider alongside Console.
/// </summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    private readonly LogRingBuffer _buffer;
    private readonly ConcurrentDictionary<string, RingBufferLogger> _loggers = new();

    public RingBufferLoggerProvider(LogRingBuffer buffer)
    {
        _buffer = buffer;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new RingBufferLogger(name, _buffer));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}

/// <summary>
/// Individual logger instance that writes to the shared ring buffer.
/// </summary>
internal sealed class RingBufferLogger : ILogger
{
    private readonly string _category;
    private readonly LogRingBuffer _buffer;

    public RingBufferLogger(string category, LogRingBuffer buffer)
    {
        _category = SimplifyCategory(category);
        _buffer = buffer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        if (!ShouldCapture(_category, message, logLevel)) return;
        _buffer.Add(logLevel, _category, message, exception?.ToString());
    }

    private static bool ShouldCapture(string category, string message, LogLevel level)
    {
        // Keep only agent-focused logs in the web UI log buffer.
        var isAgentCategory = category.Equals("agent", StringComparison.OrdinalIgnoreCase) ||
                              category.Contains("Agent", StringComparison.OrdinalIgnoreCase);
        if (!isAgentCategory) return false;

        // Always keep warnings/errors from the agent category.
        if (level >= LogLevel.Warning) return true;

        // Drop aggregate telemetry noise; keep concrete request/tool/skill flow logs.
        if (message.Contains("Agent telemetry", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    /// <summary>Shorten category names like "Microsoft.Hosting.Lifetime" to "Hosting.Lifetime".</summary>
    private static string SimplifyCategory(string category)
    {
        // Keep short categories as-is
        if (category.Length <= 30) return category;

        // Remove common prefixes
        var prefixes = new[] { "Microsoft.AspNetCore.", "Microsoft.Hosting.", "Microsoft.Extensions.", "Sharpbot." };
        foreach (var prefix in prefixes)
        {
            if (category.StartsWith(prefix))
                return category[prefix.Length..];
        }

        // Take last two segments
        var parts = category.Split('.');
        return parts.Length >= 2
            ? $"{parts[^2]}.{parts[^1]}"
            : category;
    }
}
