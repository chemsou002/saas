using Nightshift.Core.Compatibility;
using Nightshift.Core.Power;
using Nightshift.Core.Recovery;
using Nightshift.Core.Session;
using Nightshift.Core.Util;
using Nightshift.Core.Verification;

namespace Nightshift.Core.Tests;

public sealed class DurationParserTests
{
    [Theory]
    [InlineData("15m", 0, 15, 0)]
    [InlineData("1h", 1, 0, 0)]
    [InlineData("1h30m", 1, 30, 0)]
    [InlineData("90s", 0, 1, 30)]
    [InlineData("24h", 24, 0, 0)]
    [InlineData(" 2H ", 2, 0, 0)]
    public void Parses_durations(string text, int h, int m, int s) =>
        Assert.Equal(new TimeSpan(h, m, s), DurationParser.Parse(text));

    [Theory]
    [InlineData("unlimited")]
    [InlineData("Unlimited")]
    public void Unlimited_is_null(string text) => Assert.Null(DurationParser.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("0m")]
    [InlineData("abc")]
    [InlineData("10")]
    [InlineData("200h")]
    [InlineData("-5m")]
    public void Rejects_invalid(string text) => Assert.Throws<FormatException>(() => DurationParser.Parse(text));
}

public sealed class CompatibilityAssessorTests
{
    [Fact]
    public void S3_laptop_on_ac_is_configurable()
    {
        var r = CompatibilityAssessor.Assess(TestData.Laptop(), TestData.OnAc(), 1, 1);
        Assert.Equal(CompatibilityVerdict.Configurable, r.Verdict);
        Assert.True(r.CanStart);
    }

    [Fact]
    public void Modern_standby_is_flagged_with_warnings()
    {
        var r = CompatibilityAssessor.Assess(TestData.Laptop(modernStandby: true), TestData.OnAc(), 1, 1);
        Assert.Equal(CompatibilityVerdict.ConfigurableWithWarnings, r.Verdict);
        Assert.Contains(r.Findings, f => f.Code == "modern-standby");
    }

    [Fact]
    public void Desktop_is_keep_awake_only()
    {
        var r = CompatibilityAssessor.Assess(TestData.Desktop(), TestData.OnAc(), null, null);
        Assert.Equal(CompatibilityVerdict.KeepAwakeOnly, r.Verdict);
    }

    [Fact]
    public void Group_policy_lid_action_blocks()
    {
        var r = CompatibilityAssessor.Assess(TestData.Laptop() with { LidActionManagedByPolicy = true }, TestData.OnAc(), 1, 1);
        Assert.Equal(CompatibilityVerdict.Unsupported, r.Verdict);
        Assert.False(r.CanStart);
    }

    [Fact]
    public void Missing_thermal_and_battery_power_are_warnings()
    {
        var r = CompatibilityAssessor.Assess(TestData.Laptop() with { ThermalControl = false }, TestData.OnBattery(50), 1, 1);
        Assert.Equal(CompatibilityVerdict.ConfigurableWithWarnings, r.Verdict);
        Assert.Contains(r.Findings, f => f.Code == "no-thermal-zone");
        Assert.Contains(r.Findings, f => f.Code == "on-battery");
    }

    [Fact]
    public void Pre_windows_10_is_unsupported()
    {
        var r = CompatibilityAssessor.Assess(TestData.Laptop() with { OsBuild = 9600 }, TestData.OnAc(), 1, 1);
        Assert.Equal(CompatibilityVerdict.Unsupported, r.Verdict);
    }
}

public sealed class PlannerTests
{
    [Fact]
    public void Plan_never_touches_sleep_or_hibernate_timeouts()
    {
        foreach (var ms in new[] { false, true })
        {
            var caps = TestData.Laptop(ms);
            var plan = SessionPlanner.Plan(new SessionOptions(), caps, SessionOptions.Resolve(DisplayOffMethod.Auto, caps));
            Assert.DoesNotContain(plan, p => p.Key == PowerSettingKeys.StandbyTimeout || p.Key == PowerSettingKeys.HibernateTimeout);
            Assert.Contains(plan, p => p.Key == PowerSettingKeys.LidCloseAction && p.Source == PowerSource.AC && p.Value == 0);
            Assert.Contains(plan, p => p.Key == PowerSettingKeys.LidCloseAction && p.Source == PowerSource.DC && p.Value == 0);
        }
    }

    [Fact]
    public void Keep_lid_option_leaves_lid_alone()
    {
        var plan = SessionPlanner.Plan(new SessionOptions { ConfigureLid = false }, TestData.Laptop(), DisplayOffMethod.None);
        Assert.Empty(plan);
    }
}

public sealed class RecoveryStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Round_trips_and_deletes()
    {
        var store = new RecoveryStore(_dir.File("r.json"));
        var record = new RecoveryRecord
        {
            SessionId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            OwnerProcessId = 123,
            Changes =
            [
                new TrackedChange
                {
                    Scheme = Guid.NewGuid(), Subgroup = PowerSettingKeys.ButtonsSubgroup, Setting = PowerSettingKeys.LidCloseAction.Setting,
                    Name = "LidCloseAction", Source = PowerSource.DC, OriginalValue = 1, AppliedValue = 0, WriteConfirmed = true,
                },
            ],
        };
        store.Save(record);
        var loaded = store.Load()!;
        Assert.Equal(record.SessionId, loaded.SessionId);
        Assert.Equal(record.Changes, loaded.Changes);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
        store.Delete();
        Assert.Null(store.Load());
    }

    [Fact]
    public void Refuses_records_from_newer_format()
    {
        var path = _dir.File("r.json");
        File.WriteAllText(path, """{"FormatVersion":99,"SessionId":"00000000-0000-0000-0000-000000000001","StartedAt":"2026-01-01T00:00:00Z","OwnerProcessId":1,"Changes":[]}""");
        Assert.Throws<InvalidDataException>(() => new RecoveryStore(path).Load());
    }
}

public sealed class HeartbeatTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 22, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static List<DateTimeOffset> Beats(int fromSec, int toSec) =>
        Enumerable.Range(fromSec, toSec - fromSec + 1).Select(s => T0.AddSeconds(s)).ToList();

    private static SessionLogEntry Power(int sec, string evt) => new(T0.AddSeconds(sec), "power", evt, Event: evt);

    private static SessionLogEntry RestoredIdle(int sec) => new(T0.AddSeconds(sec), "state", "Restoring -> Idle", State: "Idle");

    [Fact]
    public void Continuous_heartbeat_through_lid_closure_passes()
    {
        var r = HeartbeatAnalyzer.Analyze(Beats(0, 120), [Power(10, "LidClosed"), Power(100, "LidOpened"), RestoredIdle(120)], TimeSpan.FromSeconds(5));
        Assert.Equal("PASS", r.Overall);
        Assert.Single(r.LidIntervals);
        Assert.Equal(IntervalVerdict.Pass, r.LidIntervals[0].Verdict);
    }

    [Fact]
    public void Gap_while_lid_closed_fails()
    {
        var beats = Beats(0, 30).Concat(Beats(90, 120)).ToList(); // frozen for a minute
        var r = HeartbeatAnalyzer.Analyze(beats, [Power(10, "LidClosed"), Power(100, "LidOpened"), RestoredIdle(120)], TimeSpan.FromSeconds(5));
        Assert.Equal("FAIL", r.Overall);
        Assert.Single(r.Gaps);
        Assert.Equal(TimeSpan.FromSeconds(60), r.LidIntervals[0].LongestGap);
    }

    [Fact]
    public void Gap_outside_lid_interval_does_not_fail_the_interval()
    {
        var beats = Beats(0, 5).Concat(Beats(60, 120)).ToList();
        var r = HeartbeatAnalyzer.Analyze(beats, [Power(70, "LidClosed"), Power(100, "LidOpened"), RestoredIdle(120)], TimeSpan.FromSeconds(5));
        Assert.Equal("PASS", r.Overall);
        Assert.Single(r.Gaps);
    }

    [Fact]
    public void Never_closing_the_lid_is_inconclusive()
    {
        var r = HeartbeatAnalyzer.Analyze(Beats(0, 60), [RestoredIdle(60)], TimeSpan.FromSeconds(5));
        Assert.StartsWith("INCONCLUSIVE", r.Overall, StringComparison.Ordinal);
    }

    [Fact]
    public void Suspend_event_fails()
    {
        var r = HeartbeatAnalyzer.Analyze(Beats(0, 60),
            [Power(10, "LidClosed"), new SessionLogEntry(T0.AddSeconds(20), "error", "suspend", Event: "UnexpectedSuspend"), Power(50, "LidOpened"), RestoredIdle(60)],
            TimeSpan.FromSeconds(5));
        Assert.Equal("FAIL", r.Overall);
    }

    [Fact]
    public void Missing_restore_fails()
    {
        var r = HeartbeatAnalyzer.Analyze(Beats(0, 60), [Power(10, "LidClosed"), Power(50, "LidOpened")], TimeSpan.FromSeconds(5));
        Assert.StartsWith("FAIL", r.Overall, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writer_produces_parseable_samples()
    {
        var path = _dir.File("hb.log");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(450));
        await HeartbeatWriter.RunAsync(path, TimeSpan.FromMilliseconds(100), TimeProvider.System, cts.Token);
        var samples = HeartbeatAnalyzer.ReadSamples(path);
        Assert.InRange(samples.Count, 3, 6);
        Assert.Equal(samples.OrderBy(s => s), samples);
    }
}
