using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Sharpbot.Database;

namespace Sharpbot.Session;

/// <summary>
/// A conversation session.
/// Stores messages as in-memory objects backed by SQLite.
/// </summary>
public sealed class Session
{
    public string Key { get; }
    public List<Dictionary<string, object?>> Messages { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public Dictionary<string, object?> Metadata { get; set; } = [];

    public Session(string key) => Key = key;

    /// <summary>Add a message to the session.</summary>
    public void AddMessage(string role, string content)
    {
        Messages.Add(new()
        {
            ["role"] = role,
            ["content"] = content,
            ["timestamp"] = DateTime.Now.ToString("o"),
        });
        UpdatedAt = DateTime.Now;
    }

    /// <summary>Get message history for LLM context.</summary>
    public List<Dictionary<string, object?>> GetHistory(int maxMessages = 50)
    {
        var recent = Messages.Count > maxMessages
            ? Messages.GetRange(Messages.Count - maxMessages, maxMessages)
            : Messages;

        return recent.Select(m => new Dictionary<string, object?>
        {
            ["role"] = m.GetValueOrDefault("role"),
            ["content"] = m.GetValueOrDefault("content"),
        }).ToList();
    }

    /// <summary>Clear all messages in the session.</summary>
    public void Clear()
    {
        Messages.Clear();
        UpdatedAt = DateTime.Now;
    }
}

/// <summary>
/// Manages conversation sessions backed by SQLite.
/// Hot sessions are cached in memory; all mutations are persisted atomically.
/// </summary>
public sealed class SessionManager
{
    private readonly SharpbotDb _db;
    private readonly Dictionary<string, Session> _cache = [];
    private readonly ILogger? _logger;

    public SessionManager(SharpbotDb db, ILogger? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Get an existing session or create a new one.</summary>
    public Session GetOrCreate(string key)
    {
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var session = Load(key) ?? new Session(key);
        _cache[key] = session;
        return session;
    }

    private Session? Load(string key)
    {
        try
        {
            using var conn = _db.CreateConnection();

            // Load session metadata
            using var sessionCmd = conn.CreateCommand();
            sessionCmd.CommandText = "SELECT created_at, updated_at FROM sessions WHERE key = @key";
            sessionCmd.Parameters.AddWithValue("@key", key);

            using var reader = sessionCmd.ExecuteReader();
            if (!reader.Read()) return null;

            var createdAt = DateTime.Parse(reader.GetString(0));
            var updatedAt = DateTime.Parse(reader.GetString(1));

            // Load messages ordered by insertion
            using var msgCmd = conn.CreateCommand();
            msgCmd.CommandText = "SELECT role, content, timestamp FROM messages WHERE session_key = @key ORDER BY id";
            msgCmd.Parameters.AddWithValue("@key", key);

            var messages = new List<Dictionary<string, object?>>();
            using var msgReader = msgCmd.ExecuteReader();
            while (msgReader.Read())
            {
                messages.Add(new()
                {
                    ["role"] = msgReader.GetString(0),
                    ["content"] = msgReader.GetString(1),
                    ["timestamp"] = msgReader.GetString(2),
                });
            }

            return new Session(key)
            {
                Messages = messages,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
            };
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "Failed to load session {Key}", key);
            return null;
        }
    }

    /// <summary>Save a session to the database (atomic upsert).</summary>
    public void Save(Session session)
    {
        try
        {
            using var conn = _db.CreateConnection();
            using var tx = conn.BeginTransaction();

            // Upsert session row
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO sessions (key, created_at, updated_at, metadata_json)
                    VALUES (@key, @created, @updated, '{}')
                    ON CONFLICT(key) DO UPDATE SET updated_at = @updated
                    """;
                cmd.Parameters.AddWithValue("@key", session.Key);
                cmd.Parameters.AddWithValue("@created", session.CreatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@updated", session.UpdatedAt.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            // Delete existing messages and re-insert (atomic replacement)
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM messages WHERE session_key = @key";
                cmd.Parameters.AddWithValue("@key", session.Key);
                cmd.ExecuteNonQuery();
            }

            // Batch-insert messages
            if (session.Messages.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO messages (session_key, role, content, timestamp)
                    VALUES (@key, @role, @content, @ts)
                    """;

                var keyParam = cmd.Parameters.Add("@key", SqliteType.Text);
                var roleParam = cmd.Parameters.Add("@role", SqliteType.Text);
                var contentParam = cmd.Parameters.Add("@content", SqliteType.Text);
                var tsParam = cmd.Parameters.Add("@ts", SqliteType.Text);

                foreach (var msg in session.Messages)
                {
                    keyParam.Value = session.Key;
                    roleParam.Value = msg.GetValueOrDefault("role")?.ToString() ?? "";
                    contentParam.Value = msg.GetValueOrDefault("content")?.ToString() ?? "";
                    tsParam.Value = msg.GetValueOrDefault("timestamp")?.ToString() ?? DateTime.Now.ToString("o");
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
            _cache[session.Key] = session;
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "Failed to save session {Key}", session.Key);
        }
    }

    /// <summary>Delete a session.</summary>
    public bool Delete(string key)
    {
        _cache.Remove(key);

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);

        // Messages are cascade-deleted by the FK constraint
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>List all sessions with message counts.</summary>
    public List<Dictionary<string, object?>> ListSessions()
    {
        var sessions = new List<Dictionary<string, object?>>();

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.key, s.created_at, s.updated_at, COUNT(m.id) AS message_count
            FROM sessions s
            LEFT JOIN messages m ON m.session_key = s.key
            GROUP BY s.key
            ORDER BY s.updated_at DESC
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new()
            {
                ["key"] = reader.GetString(0),
                ["created_at"] = reader.GetString(1),
                ["updated_at"] = reader.GetString(2),
                ["messageCount"] = reader.GetInt32(3),
            });
        }

        return sessions;
    }
}
