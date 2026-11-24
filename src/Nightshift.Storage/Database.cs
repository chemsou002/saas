using Microsoft.Data.Sqlite;

namespace Nightshift.Storage;

/// <summary>
/// SQLite database (WAL, synchronous=FULL so a committed row survives a crash or power loss).
/// Schema versions are tracked with PRAGMA user_version.
/// </summary>
public sealed class Database
{
    public const int SchemaVersion = 1;

    private readonly string _connectionString;

    public Database(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 10,
        }.ToString();
        Migrate();
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>Runs PRAGMA integrity_check; returns "ok" when healthy.</summary>
    public string IntegrityCheck()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    }

    private void Migrate()
    {
        using var c = Open();
        using (var wal = c.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }

        long version;
        using (var v = c.CreateCommand())
        {
            v.CommandText = "PRAGMA user_version;";
            version = (long)v.ExecuteScalar()!;
        }
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException($"Database schema {version} is newer than this NIGHTSHIFT version supports ({SchemaVersion}).");
        }
        if (version == SchemaVersion)
        {
            return;
        }

        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                session_id               TEXT PRIMARY KEY,
                owner_sid                TEXT NOT NULL,
                owner_name               TEXT NOT NULL,
                selected_applications    TEXT NOT NULL,
                start_time_utc           TEXT NOT NULL,
                end_time_utc             TEXT,
                session_mode             TEXT NOT NULL,
                duration_seconds         REAL,
                session_status           TEXT NOT NULL,
                power_policy             TEXT NOT NULL,
                power_source             TEXT NOT NULL,
                battery_level_at_start   INTEGER,
                battery_level_current    INTEGER,
                original_power_settings  TEXT NOT NULL,
                modified_power_settings  TEXT NOT NULL,
                last_heartbeat_utc       TEXT,
                termination_reason       TEXT,
                paused_total_seconds     REAL NOT NULL DEFAULT 0,
                paused_at_utc            TEXT,
                battery_warning_percent  INTEGER NOT NULL,
                battery_critical_percent INTEGER NOT NULL,
                configure_lid            INTEGER NOT NULL,
                turn_off_display         INTEGER NOT NULL,
                error                    TEXT,
                is_active                INTEGER NOT NULL
            );
            -- Enforces "at most one active session" at the storage level.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_sessions_single_active ON sessions(is_active) WHERE is_active = 1;
            CREATE INDEX IF NOT EXISTS ix_sessions_start ON sessions(start_time_utc DESC);

            CREATE TABLE IF NOT EXISTS session_events (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT,
                time_utc   TEXT NOT NULL,
                type       TEXT NOT NULL,
                message    TEXT NOT NULL,
                state      TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_events_session ON session_events(session_id, id);

            CREATE TABLE IF NOT EXISTS recovery (
                id          INTEGER PRIMARY KEY CHECK (id = 1),
                session_id  TEXT NOT NULL,
                record_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS service_instances (
                instance_id        TEXT PRIMARY KEY,
                started_utc        TEXT NOT NULL,
                last_heartbeat_utc TEXT NOT NULL,
                stopped_utc        TEXT,
                clean_shutdown     INTEGER NOT NULL DEFAULT 0,
                version            TEXT
            );

            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}
