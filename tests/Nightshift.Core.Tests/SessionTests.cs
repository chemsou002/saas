using Nightshift.Core.Power;
using Nightshift.Core.Recovery;
using Nightshift.Core.Session;

namespace Nightshift.Core.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakePowerStore _store = new();
    private readonly FakeKeepAwake _keepAwake = new();
    private readonly FakeEvents _events = new();
    private readonly FakeDisplay _display = new();
    private readonly FakeCaps _caps = new(TestData.Laptop());
    private readonly FakePowerStatus _power = new(TestData.OnAc());
    private readonly RecoveryStore _recovery;
    private readonly SessionEventLog _log;

    public SessionTests()
    {
        _recovery = new RecoveryStore(_dir.File("recovery.json"));
        _log = new SessionEventLog(_dir.File("session.jsonl"));
        _store.Set(PowerSettingKeys.LidCloseAction, ac: 1, dc: 1);
        _store.Set(PowerSettingKeys.VideoPowerDownTimeout, ac: 600, dc: 300);
    }

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    private PocSession Create(SessionOptions? options = null, Func<int, bool>? alive = null) =>
        new(options ?? Fast(TimeSpan.FromMilliseconds(1500)), new SessionDependencies
        {
            Settings = _store,
            KeepAwake = _keepAwake,
            Capabilities = _caps,
            PowerStatus = _power,
            Display = _display,
            PowerEvents = _events,
            Recovery = _recovery,
            Log = _log,
            IsProcessAlive = alive ?? (_ => true),
        });

    /// <summary>Waits until settings are applied, instead of guessing with a fixed delay (CI machines are slow).</summary>
    private static async Task WaitForRunningAsync(PocSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (session.State != SessionState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        Assert.Equal(SessionState.Running, session.State);
    }

    private static SessionOptions Fast(TimeSpan? duration) => new()
    {
        Duration = duration,
        TickInterval = TimeSpan.FromMilliseconds(20),
        DisplayOffDelay = TimeSpan.FromMilliseconds(50),
        BatteryPollInterval = TimeSpan.FromMilliseconds(20),
    };

    [Fact]
    public async Task Timed_session_applies_keeps_awake_and_restores()
    {
        var session = Create();
        var lidDuringSession = (uint?)null;
        var heldDuringSession = false;
        _events.PowerEventReceived += _ =>
        {
            lidDuringSession = _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.DC);
            heldDuringSession = _keepAwake.IsHeld;
        };

        var run = session.RunAsync(CancellationToken.None);
        await WaitForRunningAsync(session);
        _events.Raise(PowerEventKind.LidClosed);
        var result = await run;

        Assert.Equal(StopReason.DurationElapsed, result.Reason);
        Assert.Equal(0u, lidDuringSession);
        Assert.True(heldDuringSession);
        Assert.False(_keepAwake.IsHeld);
        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC));
        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.DC));
        Assert.True(result.Restore!.Success);
        Assert.False(_recovery.Exists);
        Assert.Equal(1, result.LidCloseCount);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Equal(1, _display.OffCount); // S3 laptop → monitor-power method
    }

    [Fact]
    public async Task Modern_standby_uses_tracked_idle_timeout_instead_of_monitor_off()
    {
        _caps.Caps = TestData.Laptop(modernStandby: true);
        uint? videoDuring = null;
        var session = Create();
        _events.PowerEventReceived += _ => videoDuring = _store.Get(PowerSettingKeys.VideoPowerDownTimeout, PowerSource.AC);

        var run = session.RunAsync(CancellationToken.None);
        await WaitForRunningAsync(session);
        _events.Raise(PowerEventKind.DisplayOff);
        await run;

        Assert.Equal(DisplayOffMethod.IdleTimeout, session.DisplayMethod);
        Assert.Equal(0, _display.OffCount);
        Assert.Equal(60u, videoDuring);
        Assert.Equal(600u, _store.Get(PowerSettingKeys.VideoPowerDownTimeout, PowerSource.AC));
        Assert.Equal(300u, _store.Get(PowerSettingKeys.VideoPowerDownTimeout, PowerSource.DC));
    }

    [Fact]
    public async Task Unlimited_session_runs_until_cancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var result = await Create(Fast(null)).RunAsync(cts.Token);

        Assert.Equal(StopReason.UserStopped, result.Reason);
        Assert.True(result.Restore!.Success);
        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC));
    }

    [Fact]
    public async Task Critical_battery_triggers_safe_stop_and_restore()
    {
        _power.Status = TestData.OnBattery(40);
        var session = Create(Fast(null));
        var states = new List<string>();
        _log.EntryWritten += e => { if (e.State is not null) { lock (states) { states.Add(e.State); } } };

        var run = session.RunAsync(CancellationToken.None);
        await Task.Delay(80);
        _power.Status = TestData.OnBattery(20);
        await Task.Delay(80);
        _power.Status = TestData.OnBattery(14);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StopReason.BatteryCritical, result.Reason);
        Assert.Contains("Warning", states);
        Assert.Contains("SafeStop", states);
        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC));
    }

    [Fact]
    public async Task Refuses_to_start_on_critical_battery_and_changes_nothing()
    {
        _power.Status = TestData.OnBattery(10);
        var result = await Create().RunAsync(CancellationToken.None);

        Assert.Equal(StopReason.StartupFailed, result.Reason);
        Assert.Empty(_store.Writes);
        Assert.Equal(0, _keepAwake.Acquired);
    }

    [Fact]
    public async Task Refuses_to_start_while_a_previous_recovery_record_exists_and_keeps_it()
    {
        _recovery.Save(new RecoveryRecord { SessionId = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, OwnerProcessId = 42 });

        var result = await Create().RunAsync(CancellationToken.None);

        Assert.Equal(StopReason.StartupFailed, result.Reason);
        Assert.True(_recovery.Exists);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task Access_denied_fails_cleanly_with_no_leftover_changes()
    {
        _store.DenyWriteNumber = 1;
        var result = await Create().RunAsync(CancellationToken.None);

        Assert.Equal(StopReason.StartupFailed, result.Reason);
        Assert.Contains("elevated", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC));
        Assert.False(_keepAwake.IsHeld);
        Assert.False(_recovery.Exists);
    }

    [Fact]
    public async Task Desktop_without_lid_only_keeps_awake()
    {
        _caps.Caps = TestData.Desktop();
        var result = await Create().RunAsync(CancellationToken.None);

        Assert.Equal(StopReason.DurationElapsed, result.Reason);
        Assert.DoesNotContain(_store.Writes, w => w.StartsWith("LidCloseAction", StringComparison.Ordinal));
        Assert.Equal(1, _keepAwake.Acquired);
    }

    [Fact]
    public async Task Exited_watched_process_is_reported_but_session_continues()
    {
        var result = await Create(alive: pid => pid != 7).RunAsync(CancellationToken.None);
        Assert.Equal(StopReason.DurationElapsed, result.Reason);
        Assert.Empty(result.ExitedProcessIds); // no watched pids configured

        var withPids = Fast(TimeSpan.FromMilliseconds(200)) with { WatchedProcessIds = [7, 8] };
        _recovery.Delete();
        var result2 = await Create(withPids, pid => pid != 7).RunAsync(CancellationToken.None);
        Assert.Equal(StopReason.DurationElapsed, result2.Reason);
        Assert.Equal([7], result2.ExitedProcessIds);
    }

    [Fact]
    public async Task Emergency_restore_during_run_is_idempotent()
    {
        var session = Create(Fast(null));
        using var cts = new CancellationTokenSource();
        var run = session.RunAsync(cts.Token);
        await WaitForRunningAsync(session);

        var first = session.RestoreNow();
        Assert.True(first!.Success);
        Assert.Equal(1u, _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC));
        Assert.False(_keepAwake.IsHeld);

        await cts.CancelAsync();
        await run;
        Assert.Same(first, session.RestoreNow());
    }

    [Fact]
    public async Task All_state_transitions_are_logged()
    {
        await Create().RunAsync(CancellationToken.None);
        _log.Dispose();
        var states = SessionEventLog.Read(_dir.File("session.jsonl")).Where(e => e.Type == "state").Select(e => e.Message).ToList();
        Assert.Equal(["Idle -> Preflight", "Preflight -> Running", "Running -> Completed", "Completed -> Restoring", "Restoring -> Idle"], states);
    }
}
