using Nightshift.Core.Power;
using Nightshift.Core.Recovery;

namespace Nightshift.Core.Tests;

public sealed class PowerConfiguratorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakePowerStore _store = new();
    private readonly RecoveryStore _recovery;

    public PowerConfiguratorTests() => _recovery = new RecoveryStore(_dir.File("recovery.json"));

    public void Dispose() => _dir.Dispose();

    private static RecoveryRecord NewRecord() => new() { SessionId = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, OwnerProcessId = 1 };

    private static PlannedChange[] LidPlan() =>
    [
        new(PowerSettingKeys.LidCloseAction, PowerSource.AC, 0),
        new(PowerSettingKeys.LidCloseAction, PowerSource.DC, 0),
    ];

    [Fact]
    public void Apply_then_restore_returns_exact_original_values()
    {
        _store.Set(PowerSettingKeys.LidCloseAction, ac: 1, dc: 2);
        var sut = new PowerConfigurator(_store, _recovery);

        var record = sut.Apply(NewRecord(), LidPlan());

        Assert.Equal(0u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC));
        Assert.Equal(0u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.DC));
        Assert.All(record.Changes, c => Assert.True(c.WriteConfirmed));
        Assert.True(_recovery.Exists);

        var report = sut.Restore(record);

        Assert.True(report.Success);
        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC));
        Assert.Equal(2u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.DC));
        Assert.False(_recovery.Exists);
    }

    [Fact]
    public void Settings_already_at_target_are_never_touched()
    {
        _store.Set(PowerSettingKeys.LidCloseAction, ac: 0, dc: 1);
        var record = new PowerConfigurator(_store, _recovery).Apply(NewRecord(), LidPlan());

        Assert.Single(record.Changes);
        Assert.Equal(PowerSource.DC, record.Changes[0].Source);
        Assert.Equal(["LidCloseAction/DC=0"], _store.Writes);
    }

    [Fact]
    public void Missing_settings_are_skipped()
    {
        // Desktop: no lid setting in the scheme at all.
        var record = new PowerConfigurator(_store, _recovery).Apply(NewRecord(), LidPlan());
        Assert.Empty(record.Changes);
        Assert.Empty(_store.Writes);
        Assert.Equal(0, _store.Activations);
    }

    [Fact]
    public void Original_value_is_on_disk_before_each_write()
    {
        _store.Set(PowerSettingKeys.LidCloseAction, ac: 1, dc: 2);
        var checks = 0;
        _store.BeforeWrite = (key, source) =>
        {
            var onDisk = _recovery.Load()!;
            var entry = Assert.Single(onDisk.Changes, c => c.Setting == key.Setting && c.Source == source);
            Assert.Equal(source == PowerSource.AC ? 1u : 2u, entry.OriginalValue);
            Assert.False(entry.WriteConfirmed);
            checks++;
        };

        new PowerConfigurator(_store, _recovery).Apply(NewRecord(), LidPlan());

        Assert.Equal(2, checks);
    }

    [Fact]
    public void Failure_after_a_write_is_rolled_back_in_process()
    {
        _store.Set(PowerSettingKeys.LidCloseAction, ac: 1, dc: 1);
        _store.CrashAfterWriteNumber = 1;
        var sut = new PowerConfigurator(_store, _recovery);

        Assert.Throws<SimulatedCrashException>(() => sut.Apply(NewRecord(), LidPlan()));

        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC));
        Assert.False(_recovery.Exists);
    }

    [Fact]
    public void Recovery_after_hard_crash_with_unconfirmed_write_restores_value()
    {
        // State on disk after a power loss between Write() and the confirmation save.
        _store.Set(PowerSettingKeys.LidCloseAction, ac: 0, dc: 1);
        var record = NewRecord() with
        {
            Changes =
            [
                new TrackedChange
                {
                    Scheme = _store.Active, Subgroup = PowerSettingKeys.ButtonsSubgroup, Setting = PowerSettingKeys.LidCloseAction.Setting,
                    Name = "LidCloseAction", Source = PowerSource.AC, OriginalValue = 1, AppliedValue = 0, WriteConfirmed = false,
                },
            ],
        };
        _recovery.Save(record);

        var loaded = _recovery.Load()!;
        var report = new PowerConfigurator(_store, _recovery).Restore(loaded);

        Assert.True(report.Success);
        Assert.Equal(RestoreOutcome.Restored, report.Items[0].Outcome);
        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC));
        Assert.False(_recovery.Exists);
    }

    [Fact]
    public void Restore_does_not_overwrite_values_changed_by_the_user()
    {
        _store.Set(PowerSettingKeys.LidCloseAction, ac: 1, dc: 1);
        var sut = new PowerConfigurator(_store, _recovery);
        var record = sut.Apply(NewRecord(), LidPlan());

        _store.Set(PowerSettingKeys.LidCloseAction, ac: 3, dc: 0); // user changed AC to "Shut down" mid-session

        var report = sut.Restore(record);

        Assert.True(report.Success);
        Assert.Equal(3u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC));
        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.DC));
        Assert.Contains(report.Items, i => i.Outcome == RestoreOutcome.SkippedModifiedByOthers);
    }

    [Fact]
    public void Access_denied_rolls_back_partial_changes()
    {
        _store.Set(PowerSettingKeys.LidCloseAction, ac: 1, dc: 2);
        _store.DenyWriteNumber = 2;
        var sut = new PowerConfigurator(_store, _recovery);

        Assert.Throws<PowerSettingAccessDeniedException>(() => sut.Apply(NewRecord(), LidPlan()));

        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC));
        Assert.Equal(2u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.DC));
        Assert.False(_recovery.Exists);
    }

    [Fact]
    public void Restore_targets_original_scheme_and_does_not_switch_active_scheme()
    {
        _store.Set(PowerSettingKeys.LidCloseAction, ac: 1, dc: 1);
        var original = _store.Active;
        var sut = new PowerConfigurator(_store, _recovery);
        var record = sut.Apply(NewRecord(), LidPlan());

        _store.Active = Guid.NewGuid(); // user switched power plan during the session
        var activationsBefore = _store.Activations;
        var report = sut.Restore(record);

        Assert.True(report.Success);
        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC, original));
        Assert.Equal(activationsBefore, _store.Activations);
    }

    [Fact]
    public void Restore_is_idempotent()
    {
        _store.Set(PowerSettingKeys.LidCloseAction, ac: 1, dc: 1);
        var sut = new PowerConfigurator(_store, _recovery);
        var record = sut.Apply(NewRecord(), LidPlan());
        sut.Restore(record);
        var writes = _store.Writes.Count;

        var second = sut.Restore(record);

        Assert.True(second.Success);
        Assert.All(second.Items, i => Assert.Equal(RestoreOutcome.AlreadyOriginal, i.Outcome));
        Assert.Equal(writes, _store.Writes.Count);
    }
}
