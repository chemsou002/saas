using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Nightshift.Core.Engine;
using Nightshift.Core.Power;

namespace Nightshift.Storage;

public sealed class SqliteSessionRepository(Database db, TimeProvider? time = null) : ISessionRepository
{
    private const int SqliteConstraint = 19;
    private const int MaxMessageLength = 2000;

    internal static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private const string Columns = """
        session_id, owner_sid, owner_name, selected_applications, start_time_utc, end_time_utc, session_mode,
        duration_seconds, session_status, power_policy, power_source, battery_level_at_start, battery_level_current,
        original_power_settings, modified_power_settings, last_heartbeat_utc, termination_reason,
        paused_total_seconds, paused_at_utc, battery_warning_percent, battery_critical_percent, configure_lid,
        turn_off_display, error, is_active
        """;

    public void Insert(SessionRecord session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO sessions ({Columns}) VALUES ($id, $owner_sid, $owner_name, $apps, $start, $end, $mode, $duration,
              $status, $policy, $source, $bat_start, $bat_cur, $orig, $mod, $hb, $reason, $paused_total, $paused_at,
              $warn, $crit, $lid, $display, $error, $active);
            """;
        Bind(cmd, session);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
        {
            throw new DuplicateActiveSessionException();
        }
    }

    public void Update(SessionRecord session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET owner_sid=$owner_sid, owner_name=$owner_name, selected_applications=$apps,
              start_time_utc=$start, end_time_utc=$end, session_mode=$mode, duration_seconds=$duration,
              session_status=$status, power_policy=$policy, power_source=$source, battery_level_at_start=$bat_start,
              battery_level_current=$bat_cur, original_power_settings=$orig, modified_power_settings=$mod,
              last_heartbeat_utc=$hb, termination_reason=$reason, paused_total_seconds=$paused_total,
              paused_at_utc=$paused_at, battery_warning_percent=$warn, battery_critical_percent=$crit,
              configure_lid=$lid, turn_off_display=$display, error=$error, is_active=$active
            WHERE session_id=$id;
            """;
        Bind(cmd, session);
        int rows;
        try
        {
            rows = cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
        {
            throw new DuplicateActiveSessionException();
        }
        if (rows != 1)
        {
            throw new InvalidOperationException($"Session {session.SessionId} does not exist.");
        }
    }

    public SessionRecord? Get(Guid sessionId) =>
        Query("WHERE session_id = $id", cmd => cmd.Parameters.AddWithValue("$id", sessionId.ToString("D"))).FirstOrDefault();

    public IReadOnlyList<SessionRecord> GetActive() => Query("WHERE is_active = 1", _ => { });

    public IReadOnlyList<SessionRecord> ListRecent(int count) =>
        Query("ORDER BY start_time_utc DESC LIMIT $n", cmd => cmd.Parameters.AddWithValue("$n", Math.Clamp(count, 1, 1000)));

    public void AddEvent(Guid? sessionId, string type, string message, string? state = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session_events (session_id, time_utc, type, message, state) VALUES ($s, $t, $type, $m, $st);";
        cmd.Parameters.AddWithValue("$s", (object?)sessionId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", Ts(_time.GetUtcNow()));
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$m", message.Length > MaxMessageLength ? message[..MaxMessageLength] : message);
        cmd.Parameters.AddWithValue("$st", (object?)state ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SessionEventRecord> GetEvents(Guid? sessionId, int max = 500)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sessionId is null
            ? "SELECT id, session_id, time_utc, type, message, state FROM session_events ORDER BY id DESC LIMIT $n;"
            : "SELECT id, session_id, time_utc, type, message, state FROM session_events WHERE session_id = $s ORDER BY id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", Math.Clamp(max, 1, 10000));
        if (sessionId is not null)
        {
            cmd.Parameters.AddWithValue("$s", sessionId.Value.ToString("D"));
        }
        var list = new List<SessionEventRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SessionEventRecord(r.GetInt64(0), r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)),
                ParseTs(r.GetString(2)), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }
        list.Reverse();
        return list;
    }

    public ServiceInstanceRecord? RecordServiceStart(Guid instanceId, DateTimeOffset now, string? version)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        ServiceInstanceRecord? previous = null;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT instance_id, started_utc, last_heartbeat_utc, stopped_utc, clean_shutdown, version FROM service_instances ORDER BY started_utc DESC LIMIT 1;";
            using var r = q.ExecuteReader();
            if (r.Read())
            {
                previous = new ServiceInstanceRecord(Guid.Parse(r.GetString(0)), ParseTs(r.GetString(1)), ParseTs(r.GetString(2)),
                    r.IsDBNull(3) ? null : ParseTs(r.GetString(3)), r.GetInt64(4) != 0, r.IsDBNull(5) ? null : r.GetString(5));
            }
        }
        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO service_instances (instance_id, started_utc, last_heartbeat_utc, clean_shutdown, version) VALUES ($id, $t, $t, 0, $v);
                DELETE FROM service_instances WHERE instance_id NOT IN (SELECT instance_id FROM service_instances ORDER BY started_utc DESC LIMIT 50);
                """;
            ins.Parameters.AddWithValue("$id", instanceId.ToString("D"));
            ins.Parameters.AddWithValue("$t", Ts(now));
            ins.Parameters.AddWithValue("$v", (object?)version ?? DBNull.Value);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return previous;
    }

    public void RecordServiceHeartbeat(Guid instanceId, DateTimeOffset now) =>
        Exec("UPDATE service_instances SET last_heartbeat_utc = $t WHERE instance_id = $id;", instanceId, now);

    public void RecordServiceStop(Guid instanceId, DateTimeOffset now) =>
        Exec("UPDATE service_instances SET stopped_utc = $t, last_heartbeat_utc = $t, clean_shutdown = 1 WHERE instance_id = $id;", instanceId, now);

    private void Exec(string sql, Guid id, DateTimeOffset t)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$t", Ts(t));
        cmd.ExecuteNonQuery();
    }

    private List<SessionRecord> Query(string where, Action<SqliteCommand> bind)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM sessions {where};";
        bind(cmd);
        var list = new List<SessionRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(Read(r));
        }
        return list;
    }

    private static void Bind(SqliteCommand cmd, SessionRecord s)
    {
        var p = cmd.Parameters;
        p.AddWithValue("$id", s.SessionId.ToString("D"));
        p.AddWithValue("$owner_sid", s.OwnerSid);
        p.AddWithValue("$owner_name", s.OwnerName);
        p.AddWithValue("$apps", JsonSerializer.Serialize(s.SelectedApplications, Json));
        p.AddWithValue("$start", Ts(s.StartTime));
        p.AddWithValue("$end", Nullable(s.EndTime));
        p.AddWithValue("$mode", s.SessionMode.ToString());
        p.AddWithValue("$duration", (object?)s.Duration?.TotalSeconds ?? DBNull.Value);
        p.AddWithValue("$status", s.SessionStatus.ToString());
        p.AddWithValue("$policy", s.PowerPolicy.ToString());
        p.AddWithValue("$source", s.PowerSource.ToString());
        p.AddWithValue("$bat_start", (object?)s.BatteryLevelAtStart ?? DBNull.Value);
        p.AddWithValue("$bat_cur", (object?)s.BatteryLevelCurrent ?? DBNull.Value);
        p.AddWithValue("$orig", JsonSerializer.Serialize(s.OriginalPowerSettings, Json));
        p.AddWithValue("$mod", JsonSerializer.Serialize(s.ModifiedPowerSettings, Json));
        p.AddWithValue("$hb", Nullable(s.LastHeartbeat));
        p.AddWithValue("$reason", (object?)s.TerminationReason?.ToString() ?? DBNull.Value);
        p.AddWithValue("$paused_total", s.PausedTotal.TotalSeconds);
        p.AddWithValue("$paused_at", Nullable(s.PausedAt));
        p.AddWithValue("$warn", s.BatteryWarningPercent);
        p.AddWithValue("$crit", s.BatteryCriticalPercent);
        p.AddWithValue("$lid", s.ConfigureLid ? 1 : 0);
        p.AddWithValue("$display", s.TurnOffDisplay ? 1 : 0);
        p.AddWithValue("$error", (object?)s.Error ?? DBNull.Value);
        p.AddWithValue("$active", s.SessionStatus.IsActive() ? 1 : 0);
    }

    private static SessionRecord Read(SqliteDataReader r) => new()
    {
        SessionId = Guid.Parse(r.GetString(0)),
        OwnerSid = r.GetString(1),
        OwnerName = r.GetString(2),
        SelectedApplications = JsonSerializer.Deserialize<List<SelectedApplication>>(r.GetString(3), Json) ?? [],
        StartTime = ParseTs(r.GetString(4)),
        EndTime = r.IsDBNull(5) ? null : ParseTs(r.GetString(5)),
        SessionMode = Enum.Parse<SessionMode>(r.GetString(6)),
        Duration = r.IsDBNull(7) ? null : TimeSpan.FromSeconds(r.GetDouble(7)),
        SessionStatus = Enum.Parse<SessionStatus>(r.GetString(8)),
        PowerPolicy = Enum.Parse<PowerPolicy>(r.GetString(9)),
        PowerSource = Enum.Parse<PowerSource>(r.GetString(10)),
        BatteryLevelAtStart = r.IsDBNull(11) ? null : r.GetInt32(11),
        BatteryLevelCurrent = r.IsDBNull(12) ? null : r.GetInt32(12),
        OriginalPowerSettings = JsonSerializer.Deserialize<List<PowerSettingValue>>(r.GetString(13), Json) ?? [],
        ModifiedPowerSettings = JsonSerializer.Deserialize<List<PowerSettingValue>>(r.GetString(14), Json) ?? [],
        LastHeartbeat = r.IsDBNull(15) ? null : ParseTs(r.GetString(15)),
        TerminationReason = r.IsDBNull(16) ? null : Enum.Parse<TerminationReason>(r.GetString(16)),
        PausedTotal = TimeSpan.FromSeconds(r.GetDouble(17)),
        PausedAt = r.IsDBNull(18) ? null : ParseTs(r.GetString(18)),
        BatteryWarningPercent = r.GetInt32(19),
        BatteryCriticalPercent = r.GetInt32(20),
        ConfigureLid = r.GetInt64(21) != 0,
        TurnOffDisplay = r.GetInt64(22) != 0,
        Error = r.IsDBNull(23) ? null : r.GetString(23),
    };

    internal static string Ts(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseTs(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static object Nullable(DateTimeOffset? t) => t is null ? DBNull.Value : Ts(t.Value);
}
