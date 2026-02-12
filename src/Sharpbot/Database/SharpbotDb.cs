using Microsoft.Data.Sqlite;

namespace Sharpbot.Database;

/// <summary>
/// Central SQLite database for Sharpbot.
/// Manages the connection string, schema initialisation, and connection factory.
/// All runtime data (sessions, usage, cron, logs) is stored in a single file.
/// </summary>
public sealed class SharpbotDb : IDisposable
{
    private readonly string _connectionString;

    /// <summary>Create a SharpbotDb using the persistent database path.</summary>
    public static SharpbotDb CreateDefault()
    {
        var dbPath = Utils.Helpers.GetPersistentDbPath();
        return new SharpbotDb(dbPath);
    }

    public SharpbotDb(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        Initialize();
    }

    /// <summary>Create and open a new connection to the database.</summary>
    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = CreateConnection();

        // WAL mode for better concurrent read/write performance
        Execute(conn, "PRAGMA journal_mode=WAL;");
        Execute(conn, "PRAGMA foreign_keys=ON;");

        Execute(conn, """
            -- ── Sessions ────────────────────────────────────────────────
            CREATE TABLE IF NOT EXISTS sessions (
                key             TEXT PRIMARY KEY,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                metadata_json   TEXT
            );

            CREATE TABLE IF NOT EXISTS messages (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                session_key     TEXT NOT NULL,
                role            TEXT NOT NULL,
                content         TEXT NOT NULL,
                timestamp       TEXT NOT NULL,
                FOREIGN KEY (session_key) REFERENCES sessions(key) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_messages_session
                ON messages(session_key, id);

            -- ── Usage Telemetry ─────────────────────────────────────────
            CREATE TABLE IF NOT EXISTS usage (
                id                  TEXT PRIMARY KEY,
                timestamp           TEXT NOT NULL,
                channel             TEXT NOT NULL DEFAULT '',
                session_key         TEXT NOT NULL DEFAULT '',
                model               TEXT NOT NULL DEFAULT '',
                success             INTEGER NOT NULL DEFAULT 1,
                error               TEXT,
                iterations          INTEGER NOT NULL DEFAULT 0,
                prompt_tokens       INTEGER NOT NULL DEFAULT 0,
                completion_tokens   INTEGER NOT NULL DEFAULT 0,
                total_tokens        INTEGER NOT NULL DEFAULT 0,
                llm_duration_ms     REAL NOT NULL DEFAULT 0,
                tool_calls          INTEGER NOT NULL DEFAULT 0,
                failed_tool_calls   INTEGER NOT NULL DEFAULT 0,
                tool_duration_ms    REAL NOT NULL DEFAULT 0,
                total_duration_ms   REAL NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_usage_timestamp
                ON usage(timestamp);

            CREATE TABLE IF NOT EXISTS usage_tools (
                usage_id    TEXT NOT NULL,
                tool_name   TEXT NOT NULL,
                FOREIGN KEY (usage_id) REFERENCES usage(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_usage_tools_id
                ON usage_tools(usage_id);

            -- ── Cron Jobs ───────────────────────────────────────────────
            CREATE TABLE IF NOT EXISTS cron_jobs (
                id                  TEXT PRIMARY KEY,
                name                TEXT NOT NULL,
                enabled             INTEGER NOT NULL DEFAULT 1,
                schedule_kind       TEXT NOT NULL,
                schedule_at_ms      INTEGER,
                schedule_every_ms   INTEGER,
                schedule_expr       TEXT,
                schedule_tz         TEXT,
                payload_kind        TEXT NOT NULL DEFAULT 'agent_turn',
                payload_message     TEXT NOT NULL DEFAULT '',
                payload_deliver     INTEGER NOT NULL DEFAULT 0,
                payload_channel     TEXT,
                payload_to          TEXT,
                next_run_at_ms      INTEGER,
                last_run_at_ms      INTEGER,
                last_status         TEXT,
                last_error          TEXT,
                created_at_ms       INTEGER NOT NULL,
                updated_at_ms       INTEGER NOT NULL,
                delete_after_run    INTEGER NOT NULL DEFAULT 0
            );

            -- ── Logs ────────────────────────────────────────────────────
            CREATE TABLE IF NOT EXISTS logs (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp   TEXT NOT NULL,
                level       INTEGER NOT NULL,
                level_name  TEXT NOT NULL,
                category    TEXT NOT NULL,
                message     TEXT NOT NULL,
                exception   TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_logs_timestamp
                ON logs(timestamp);
            CREATE INDEX IF NOT EXISTS idx_logs_level
                ON logs(level, timestamp);

            -- ── Semantic Memory Embeddings ──────────────────────────────
            CREATE TABLE IF NOT EXISTS memory_embeddings (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                content         TEXT NOT NULL,
                embedding       BLOB NOT NULL,
                dimensions      INTEGER NOT NULL,
                source          TEXT NOT NULL DEFAULT 'manual',
                source_id       TEXT,
                metadata_json   TEXT,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_memory_source
                ON memory_embeddings(source);
        """);
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() { }
}
