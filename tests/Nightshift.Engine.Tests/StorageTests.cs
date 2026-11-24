using Microsoft.Data.Sqlite;
using Nightshift.Core.Engine;
using Nightshift.Core.Power;
using Nightshift.Core.Recovery;
using Nightshift.Storage;

namespace Nightshift.Engine.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly EngineFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static SessionRecord NewSession(SessionStatus status = SessionStatus.Running) => new()
    {
        SessionId = Guid.NewGuid(),
        OwnerSid = "S-1-5-21-1",
        OwnerName = "PC\\alice",
        SelectedApplications = [new SelectedApplication("Codex", null, 99)],
        StartTime = DateTimeOffset.UtcNow,
        SessionMode = SessionMode.Unlimited,
        SessionStatus = status,
        PowerPolicy = PowerPolicy.Automatic,
        PowerSource = PowerSource.AC,
        BatteryWarningPercent = 25,
        BatteryCriticalPercent = 15,
    };

    [Fact]
    public void Schema_is_created_once_and_reopening_is_idempotent()
    {
        _f.Repository.Insert(NewSession());
        var reopened = new Database(_f.Db.Path);
        Assert.Single(new SqliteSessionRepository(reopened).GetActive());
        Assert.Equal("ok", reopened.IntegrityCheck());
    }

    [Fact]
    public void Refuses_a_database_from_a_newer_version()
    {
        using (var c = _f.Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new Database(_f.Db.Path));
    }

    [Fact]
    public void Unique_index_allows_only_one_active_session()
    {
        _f.Repository.Insert(NewSession());
        Assert.Throws<DuplicateActiveSessionException>(() => _f.Repository.Insert(NewSession(SessionStatus.Paused)));
        _f.Repository.Insert(NewSession(SessionStatus.Completed)); // history rows are unlimited
        Assert.Equal(2, _f.Repository.ListRecent(10).Count);
    }

    [Fact]
    public void Timestamps_are_stored_as_utc()
    {
        var local = new DateTimeOffset(2026, 11, 24, 23, 30, 0, TimeSpan.FromHours(3));
        var s = NewSession() with { StartTime = local };
        _f.Repository.Insert(s);
        using var c = _f.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT start_time_utc FROM sessions;";
        Assert.Equal("2026-11-24T20:30:00.0000000+00:00", cmd.ExecuteScalar());
        Assert.Equal(local, _f.Repository.Get(s.SessionId)!.StartTime);
    }

    [Fact]
    public void Recovery_store_round_trips_and_survives_reopen()
    {
        var record = new RecoveryRecord
        {
            SessionId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            OwnerProcessId = 1,
            Changes =
            [
                new TrackedChange
                {
                    Scheme = Guid.NewGuid(), Subgroup = PowerSettingKeys.ButtonsSubgroup, Setting = PowerSettingKeys.LidCloseAction.Setting,
                    Name = "LidCloseAction", Source = PowerSource.AC, OriginalValue = 1, AppliedValue = 0, WriteConfirmed = false,
                },
            ],
        };
        _f.Recovery.Save(record);
        _f.Recovery.Save(record with { Changes = [record.Changes[0] with { WriteConfirmed = true }] });

        SqliteConnection.ClearAllPools();
        var reloaded = new SqliteRecoveryStore(new Database(_f.Db.Path)).Load()!;
        Assert.True(reloaded.Changes.Single().WriteConfirmed);
        Assert.Equal(record.SessionId, reloaded.SessionId);

        _f.Recovery.Delete();
        Assert.False(_f.Recovery.Exists);
        Assert.Null(_f.Recovery.Load());
    }

    [Fact]
    public void Events_are_returned_in_order_and_messages_are_bounded()
    {
        var id = Guid.NewGuid();
        _f.Repository.AddEvent(id, "info", "first");
        _f.Repository.AddEvent(id, "info", new string('x', 10_000));
        _f.Repository.AddEvent(null, "info", "service-level");
        var events = _f.Repository.GetEvents(id);
        Assert.Equal(2, events.Count);
        Assert.Equal("first", events[0].Message);
        Assert.Equal(2000, events[1].Message.Length);
        Assert.Equal(3, _f.Repository.GetEvents(null).Count);
    }

    [Fact]
    public void Service_instances_track_clean_and_unclean_shutdowns()
    {
        var first = Guid.NewGuid();
        Assert.Null(_f.Repository.RecordServiceStart(first, DateTimeOffset.UtcNow, "1"));
        _f.Repository.RecordServiceHeartbeat(first, DateTimeOffset.UtcNow);
        // first instance crashes (no RecordServiceStop)
        var second = Guid.NewGuid();
        var previous = _f.Repository.RecordServiceStart(second, DateTimeOffset.UtcNow.AddSeconds(1), "1")!;
        Assert.Equal(first, previous.InstanceId);
        Assert.False(previous.CleanShutdown);

        _f.Repository.RecordServiceStop(second, DateTimeOffset.UtcNow.AddSeconds(2));
        var third = _f.Repository.RecordServiceStart(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(3), "1")!;
        Assert.Equal(second, third.InstanceId);
        Assert.True(third.CleanShutdown);
    }
}
