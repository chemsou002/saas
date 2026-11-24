using System.Text.Json;
using Nightshift.Core.Recovery;

namespace Nightshift.Storage;

/// <summary>
/// The service's recovery record, stored in the same SQLite database (single row). Each Save is its own
/// committed transaction with synchronous=FULL, so the original values are durable before a setting is written.
/// </summary>
public sealed class SqliteRecoveryStore(Database db, TimeProvider? time = null) : IRecoveryStore
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool Exists
    {
        get
        {
            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM recovery;";
            return (long)cmd.ExecuteScalar()! > 0;
        }
    }

    public RecoveryRecord? Load()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT record_json FROM recovery WHERE id = 1;";
        if (cmd.ExecuteScalar() is not string json)
        {
            return null;
        }
        var record = JsonSerializer.Deserialize<RecoveryRecord>(json, RecoveryStore.JsonOptions)
            ?? throw new InvalidDataException("Recovery record is empty.");
        if (record.FormatVersion > RecoveryRecord.CurrentFormatVersion)
        {
            throw new InvalidDataException($"Recovery record format {record.FormatVersion} is newer than supported.");
        }
        return record;
    }

    public void Save(RecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recovery (id, session_id, record_json, updated_utc) VALUES (1, $s, $j, $t)
            ON CONFLICT(id) DO UPDATE SET session_id = excluded.session_id, record_json = excluded.record_json, updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$s", record.SessionId.ToString("D"));
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(record, RecoveryStore.JsonOptions));
        cmd.Parameters.AddWithValue("$t", SqliteSessionRepository.Ts(_time.GetUtcNow()));
        cmd.ExecuteNonQuery();
    }

    public void Delete()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM recovery;";
        cmd.ExecuteNonQuery();
    }
}
