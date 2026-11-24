using Nightshift.Core.Engine;
using Nightshift.Core.Power;
using Nightshift.Core.Session;

namespace Nightshift.Engine.Tests;

public sealed class SessionManagerTests : IDisposable
{
    private readonly EngineFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static SessionEngineException Code(string code, Action act)
    {
        var ex = Assert.Throws<SessionEngineException>(act);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    // ------------------------------------------------------------------ timed / unlimited / stop

    [Fact]
    public void Timed_session_applies_settings_and_completes_with_restore()
    {
        var m = _f.NewManager();
        var s = m.StartSession(EngineFixture.Timed(TimeSpan.FromMinutes(5)));

        Assert.Equal(SessionStatus.Running, s.Status);
        Assert.True(_f.KeepAwake.IsHeld);
        Assert.Equal(0u, _f.Lid(PowerSource.AC));
        Assert.Equal(0u, _f.Lid(PowerSource.DC));
        Assert.Equal(60u, _f.Video(PowerSource.AC)); // service always uses the tracked idle-timeout display method
        Assert.True(_f.Recovery.Exists);

        _f.Advance(TimeSpan.FromMinutes(2), m);
        Assert.Equal(TimeSpan.FromMinutes(3), m.GetRemainingTime());
        Assert.Equal(TimeSpan.FromMinutes(2), m.GetElapsedTime());

        _f.Advance(TimeSpan.FromMinutes(3), m);

        Assert.False(m.GetSessionStatus().HasSession);
        Assert.True(_f.SettingsAreOriginal());
        Assert.False(_f.KeepAwake.IsHeld);
        Assert.False(_f.Recovery.Exists);
        var row = _f.Repository.Get(s.SessionId!.Value)!;
        Assert.Equal(SessionStatus.Completed, row.SessionStatus);
        Assert.Equal(TerminationReason.DurationElapsed, row.TerminationReason);
        Assert.Equal(_f.Time.GetUtcNow(), row.EndTime);
    }

    [Fact]
    public void Unlimited_session_never_times_out_and_stops_on_request()
    {
        var m = _f.NewManager();
        var s = m.StartSession(EngineFixture.Unlimited());

        _f.Advance(TimeSpan.FromDays(3), m, TimeSpan.FromMinutes(1));

        var status = m.GetSessionStatus();
        Assert.Equal(SessionStatus.Running, status.Status);
        Assert.Null(status.Remaining);
        Assert.Null(m.GetRemainingTime());
        Assert.Equal(TimeSpan.FromDays(3), m.GetElapsedTime());

        var stopped = m.StopSession(EngineFixture.Alice);
        Assert.Equal(SessionStatus.Stopped, stopped.Status);
        Assert.Equal(TerminationReason.UserStopped, stopped.TerminationReason);
        Assert.True(_f.SettingsAreOriginal());
        Assert.Equal(SessionStatus.Stopped, _f.Repository.Get(s.SessionId!.Value)!.SessionStatus);
    }

    [Fact]
    public void Unlimited_mode_keeps_battery_safety()
    {
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Unlimited());
        _f.Power.Status = TestData.OnBattery(10);
        m.HandlePowerEvent(new PowerEvent(_f.Time.GetUtcNow(), PowerEventKind.PowerSourceDc));
        _f.Advance(TimeSpan.FromSeconds(2), m);

        var last = m.GetLastSession()!;
        Assert.Equal(SessionStatus.SafeStopped, last.Status);
        Assert.Equal(TerminationReason.BatteryCritical, last.TerminationReason);
        Assert.True(_f.SettingsAreOriginal());
    }

    [Fact]
    public void Stop_without_session_reports_no_active_session() =>
        Code(EngineErrorCodes.NoActiveSession, () => _f.NewManager().StopSession(EngineFixture.Alice));

    // ------------------------------------------------------------------ pause / resume

    [Fact]
    public void Pause_restores_settings_and_freezes_the_clock_resume_reapplies()
    {
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Timed(TimeSpan.FromMinutes(10)));
        _f.Advance(TimeSpan.FromMinutes(4), m);

        var paused = m.PauseSession(EngineFixture.Alice);
        Assert.Equal(SessionStatus.Paused, paused.Status);
        Assert.Equal(SessionState.Paused, paused.State);
        Assert.True(_f.SettingsAreOriginal());
        Assert.False(_f.KeepAwake.IsHeld);
        Assert.False(_f.Recovery.Exists);

        _f.Advance(TimeSpan.FromHours(1), m, TimeSpan.FromMinutes(1)); // paused time does not count
        Assert.Equal(TimeSpan.FromMinutes(4), m.GetElapsedTime());
        Assert.Equal(TimeSpan.FromMinutes(6), m.GetRemainingTime());
        Assert.Equal(SessionStatus.Paused, m.GetSessionStatus().Status);

        var resumed = m.ResumeSession(EngineFixture.Alice);
        Assert.Equal(SessionStatus.Running, resumed.Status);
        Assert.Equal(0u, _f.Lid(PowerSource.AC));
        Assert.True(_f.KeepAwake.IsHeld);

        _f.Advance(TimeSpan.FromMinutes(6), m);
        Assert.Equal(SessionStatus.Completed, m.GetLastSession()!.Status);
        Assert.Equal(TimeSpan.FromMinutes(10), m.GetLastSession()!.Elapsed);
        Assert.True(_f.SettingsAreOriginal());
    }

    [Fact]
    public void Pause_and_resume_reject_invalid_states()
    {
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Unlimited());
        Code(EngineErrorCodes.InvalidState, () => m.ResumeSession(EngineFixture.Alice));
        m.PauseSession(EngineFixture.Alice);
        Code(EngineErrorCodes.InvalidState, () => m.PauseSession(EngineFixture.Alice));
    }

    [Fact]
    public void Stopping_a_paused_session_counts_pause_time_and_restores_nothing_twice()
    {
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Unlimited());
        _f.Advance(TimeSpan.FromMinutes(1), m);
        m.PauseSession(EngineFixture.Alice);
        var writes = _f.Store.Writes.Count;
        _f.Advance(TimeSpan.FromMinutes(5), m);

        var stopped = m.StopSession(EngineFixture.Alice);
        Assert.Equal(TimeSpan.FromMinutes(1), stopped.Elapsed);
        Assert.Equal(writes, _f.Store.Writes.Count);
    }

    // ------------------------------------------------------------------ duplicate prevention / authorization

    [Fact]
    public void Second_start_is_rejected()
    {
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Unlimited());
        Code(EngineErrorCodes.SessionAlreadyActive, () => m.StartSession(EngineFixture.Unlimited(EngineFixture.Bob)));
    }

    [Fact]
    public void Database_prevents_two_active_sessions_even_across_managers()
    {
        _f.NewManager().StartSession(EngineFixture.Unlimited());
        var second = _f.NewManager(); // e.g. a second service process on the same database
        Code(EngineErrorCodes.SessionAlreadyActive, () => second.StartSession(EngineFixture.Unlimited()));
        Assert.Single(_f.Repository.GetActive());
    }

    [Fact]
    public void Only_owner_or_administrator_can_control_a_session()
    {
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Unlimited(EngineFixture.Alice));
        Code(EngineErrorCodes.NotAuthorized, () => m.StopSession(EngineFixture.Bob));
        Code(EngineErrorCodes.NotAuthorized, () => m.PauseSession(EngineFixture.Bob));
        Assert.Equal(SessionStatus.Running, m.GetSessionStatus().Status);
        Assert.Equal(SessionStatus.Stopped, m.StopSession(EngineFixture.Admin).Status);
    }

    // ------------------------------------------------------------------ start validation

    [Fact]
    public void Start_requires_ventilation_acknowledgement()
    {
        var m = _f.NewManager();
        Code(EngineErrorCodes.VentilationAckRequired, () => m.StartSession(EngineFixture.Unlimited() with { VentilationAcknowledged = false }));
        Assert.Empty(_f.Store.Writes);
    }

    [Theory]
    [InlineData(SessionMode.Timed, null)]
    [InlineData(SessionMode.Timed, 0)]
    [InlineData(SessionMode.Timed, 700000)]
    [InlineData(SessionMode.Unlimited, 60)]
    public void Start_rejects_inconsistent_durations(SessionMode mode, int? seconds) =>
        Code(EngineErrorCodes.InvalidRequest, () => _f.NewManager().StartSession(new StartSessionRequest
        {
            Caller = EngineFixture.Alice,
            Mode = mode,
            Duration = seconds is null ? null : TimeSpan.FromSeconds(seconds.Value),
            VentilationAcknowledged = true,
        }));

    [Fact]
    public void Start_refuses_on_critical_battery_and_ac_only_on_battery()
    {
        _f.Power.Status = TestData.OnBattery(12);
        Code(EngineErrorCodes.BatteryTooLow, () => _f.NewManager().StartSession(EngineFixture.Unlimited()));
        _f.Power.Status = TestData.OnBattery(80);
        Code(EngineErrorCodes.AcPowerRequired, () => _f.NewManager().StartSession(EngineFixture.Unlimited() with { PowerPolicy = PowerPolicy.AcOnly }));
        Assert.Empty(_f.Store.Writes);
        Assert.Empty(_f.Repository.GetActive());
    }

    [Fact]
    public void Start_refuses_when_group_policy_owns_the_lid_action()
    {
        _f.Caps.Caps = TestData.Laptop() with { LidActionManagedByPolicy = true };
        Code(EngineErrorCodes.UnsupportedSystem, () => _f.NewManager().StartSession(EngineFixture.Unlimited()));
        Assert.Empty(_f.Store.Writes);
    }

    [Fact]
    public void Access_denied_rolls_back_marks_failed_and_allows_a_new_start()
    {
        var m = _f.NewManager();
        _f.Store.DenyWriteNumber = 2;
        Code(EngineErrorCodes.AccessDenied, () => m.StartSession(EngineFixture.Unlimited()));

        Assert.True(_f.SettingsAreOriginal());
        Assert.False(_f.KeepAwake.IsHeld);
        Assert.False(_f.Recovery.Exists);
        var failed = _f.Repository.ListRecent(1).Single();
        Assert.Equal(SessionStatus.Failed, failed.SessionStatus);
        Assert.Equal(TerminationReason.StartupFailed, failed.TerminationReason);

        Assert.Equal(SessionStatus.Running, m.StartSession(EngineFixture.Unlimited()).Status);
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public void Session_model_is_persisted_with_all_fields()
    {
        _f.Power.Status = TestData.OnBattery(77);
        var m = _f.NewManager();
        var s = m.StartSession(EngineFixture.Timed(TimeSpan.FromHours(2)));

        var row = _f.Repository.Get(s.SessionId!.Value)!;
        Assert.Equal(EngineFixture.Alice.UserSid, row.OwnerSid);
        Assert.Equal(new SelectedApplication("Claude Code", @"C:\Tools\claude.exe", 4242), Assert.Single(row.SelectedApplications));
        Assert.Equal(_f.Time.GetUtcNow(), row.StartTime);
        Assert.Equal(TimeSpan.Zero, row.StartTime.Offset);
        Assert.Null(row.EndTime);
        Assert.Equal(SessionMode.Timed, row.SessionMode);
        Assert.Equal(TimeSpan.FromHours(2), row.Duration);
        Assert.Equal(SessionStatus.Running, row.SessionStatus);
        Assert.Equal(PowerSource.DC, row.PowerSource);
        Assert.Equal(77, row.BatteryLevelAtStart);
        Assert.Contains(new PowerSettingValue("LidCloseAction", PowerSource.AC, 1), row.OriginalPowerSettings);
        Assert.Contains(new PowerSettingValue("LidCloseAction", PowerSource.AC, 0), row.ModifiedPowerSettings);
        Assert.Contains(new PowerSettingValue("VideoPowerDownTimeout", PowerSource.DC, 300), row.OriginalPowerSettings);
        Assert.Null(row.TerminationReason);

        _f.Power.Status = TestData.OnBattery(70);
        _f.Advance(TimeSpan.FromSeconds(31), m);
        row = _f.Repository.Get(s.SessionId.Value)!;
        Assert.Equal(70, row.BatteryLevelCurrent);
        Assert.True(row.LastHeartbeat >= _f.Time.GetUtcNow().AddSeconds(-10));
    }

    [Fact]
    public void State_transitions_are_recorded_as_events()
    {
        var m = _f.NewManager();
        var s = m.StartSession(EngineFixture.Unlimited());
        m.PauseSession(EngineFixture.Alice);
        m.ResumeSession(EngineFixture.Alice);
        m.StopSession(EngineFixture.Alice);
        var states = _f.Repository.GetEvents(s.SessionId).Where(e => e.Type == "state").Select(e => e.State).ToList();
        Assert.Equal(["Starting", "Running", "Paused", "Running", "Stopping", "Stopped"], states);
    }

    // ------------------------------------------------------------------ service restart / crash recovery

    [Fact]
    public void Clean_service_shutdown_ends_session_and_restores()
    {
        var m = _f.NewManager();
        var s = m.StartSession(EngineFixture.Unlimited());
        m.Shutdown();

        Assert.True(_f.SettingsAreOriginal());
        var row = _f.Repository.Get(s.SessionId!.Value)!;
        Assert.Equal(SessionStatus.Stopped, row.SessionStatus);
        Assert.Equal(TerminationReason.ServiceStopping, row.TerminationReason);

        var restarted = _f.NewManager();
        var result = restarted.RecoverOnStartup();
        Assert.Empty(result.InterruptedSessions);
        Assert.Null(result.Restore);
    }

    [Fact]
    public void Unexpected_termination_is_recovered_on_next_start()
    {
        var crashed = _f.NewManager();
        var s = crashed.StartSession(EngineFixture.Timed(TimeSpan.FromHours(1)));
        _f.Advance(TimeSpan.FromSeconds(15), crashed);
        // Process dies here: no Stop, no Shutdown. Windows drops the power request with the process.
        Assert.Equal(0u, _f.Lid(PowerSource.AC));

        _f.Time.Advance(TimeSpan.FromMinutes(3));
        var restarted = _f.NewManager();
        var result = restarted.RecoverOnStartup();

        Assert.Equal([s.SessionId!.Value], result.InterruptedSessions);
        Assert.True(result.Restore!.Success);
        Assert.False(result.RecoveryPending);
        Assert.True(_f.SettingsAreOriginal());
        var row = _f.Repository.Get(s.SessionId.Value)!;
        Assert.Equal(SessionStatus.Interrupted, row.SessionStatus);
        Assert.Equal(TerminationReason.ServiceTerminatedUnexpectedly, row.TerminationReason);
        Assert.Contains(_f.Repository.GetEvents(s.SessionId), e => e.Type == "recovery" && e.Message.Contains("interrupted", StringComparison.Ordinal));

        Assert.Equal(SessionStatus.Running, restarted.StartSession(EngineFixture.Unlimited()).Status);
    }

    [Fact]
    public void Recovery_does_not_overwrite_changes_made_after_the_crash()
    {
        var crashed = _f.NewManager();
        crashed.StartSession(EngineFixture.Unlimited());
        // While the service was down, the user (or an administrator) chose "Shut down" on lid close (AC).
        _f.Store.Set(PowerSettingKeys.LidCloseAction, ac: 3, dc: 0);

        var result = _f.NewManager().RecoverOnStartup();

        Assert.Equal(3u, _f.Lid(PowerSource.AC));   // newer change kept
        Assert.Equal(1u, _f.Lid(PowerSource.DC));   // still NIGHTSHIFT's value -> restored
        Assert.Contains(result.Restore!.Items, i => i.Outcome == Nightshift.Core.Power.RestoreOutcome.SkippedModifiedByOthers);
    }

    [Fact]
    public void Crash_while_paused_marks_interrupted_without_touching_settings()
    {
        var m = _f.NewManager();
        var s = m.StartSession(EngineFixture.Unlimited());
        m.PauseSession(EngineFixture.Alice);
        var writes = _f.Store.Writes.Count;

        var result = _f.NewManager().RecoverOnStartup();

        Assert.Equal([s.SessionId!.Value], result.InterruptedSessions);
        Assert.Null(result.Restore);
        Assert.Equal(writes, _f.Store.Writes.Count);
    }

    [Fact]
    public void Failed_restore_stays_pending_and_is_retried()
    {
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Unlimited());
        _f.Store.DenyWriteNumber = _f.Store.Writes.Count + 1; // the next write (first restore write) fails

        var stopped = m.StopSession(EngineFixture.Alice);
        Assert.NotNull(stopped.SessionId);
        Assert.True(m.RecoveryPending);
        Code(EngineErrorCodes.RecoveryPending, () => { _f.Store.DenyWriteNumber = _f.Store.Writes.Count + 1; m.StartSession(EngineFixture.Unlimited()); });

        _f.Advance(TimeSpan.FromMinutes(2), m);   // periodic retry succeeds now
        Assert.False(m.RecoveryPending);
        Assert.True(_f.SettingsAreOriginal());
    }

    // ------------------------------------------------------------------ power source transitions

    [Fact]
    public void Ac_to_battery_switches_source_warns_and_safe_stops_at_critical()
    {
        var m = _f.NewManager();
        var s = m.StartSession(EngineFixture.Unlimited());

        _f.Power.Status = TestData.OnBattery(30);
        m.HandlePowerEvent(new PowerEvent(_f.Time.GetUtcNow(), PowerEventKind.PowerSourceDc));
        _f.Advance(TimeSpan.FromSeconds(1), m);
        Assert.Equal(PowerSource.DC, m.GetSessionStatus().PowerSource);
        Assert.Equal(PowerSource.DC, _f.Repository.Get(s.SessionId!.Value)!.PowerSource);
        Assert.Equal(SessionState.Running, m.GetSessionStatus().State);

        _f.Power.Status = TestData.OnBattery(24);
        _f.Advance(TimeSpan.FromSeconds(31), m);
        Assert.Equal(SessionState.Warning, m.GetSessionStatus().State);

        _f.Power.Status = TestData.OnBattery(15);
        _f.Advance(TimeSpan.FromSeconds(31), m);
        var ended = _f.Repository.Get(s.SessionId.Value)!;
        Assert.Equal(SessionStatus.SafeStopped, ended.SessionStatus);
        Assert.Equal(TerminationReason.BatteryCritical, ended.TerminationReason);
        Assert.Equal(15, ended.BatteryLevelCurrent);
        Assert.True(_f.SettingsAreOriginal());
    }

    [Fact]
    public void Battery_to_ac_clears_warning_and_continues()
    {
        _f.Power.Status = TestData.OnBattery(20);
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Unlimited());
        _f.Advance(TimeSpan.FromSeconds(1), m);
        Assert.Equal(SessionState.Warning, m.GetSessionStatus().State);

        _f.Power.Status = TestData.OnAc();
        m.HandlePowerEvent(new PowerEvent(_f.Time.GetUtcNow(), PowerEventKind.PowerSourceAc));
        _f.Advance(TimeSpan.FromSeconds(1), m);

        var s = m.GetSessionStatus();
        Assert.Equal(SessionState.Running, s.State);
        Assert.Equal(PowerSource.AC, s.PowerSource);
        Assert.Equal(SessionStatus.Running, s.Status);
    }

    [Fact]
    public void Ac_only_session_stops_after_grace_period_without_charger()
    {
        var m = _f.NewManager();
        var s = m.StartSession(EngineFixture.Unlimited() with { PowerPolicy = PowerPolicy.AcOnly });
        _f.Power.Status = TestData.OnBattery(90);
        m.HandlePowerEvent(new PowerEvent(_f.Time.GetUtcNow(), PowerEventKind.PowerSourceDc));

        _f.Advance(TimeSpan.FromSeconds(59), m);
        Assert.Equal(SessionStatus.Running, m.GetSessionStatus().Status);
        _f.Advance(TimeSpan.FromSeconds(2), m);

        var row = _f.Repository.Get(s.SessionId!.Value)!;
        Assert.Equal(SessionStatus.SafeStopped, row.SessionStatus);
        Assert.Equal(TerminationReason.AcPowerLost, row.TerminationReason);
        Assert.True(_f.SettingsAreOriginal());
    }

    [Fact]
    public void Ac_only_session_survives_a_short_unplug()
    {
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Unlimited() with { PowerPolicy = PowerPolicy.AcOnly });
        _f.Power.Status = TestData.OnBattery(90);
        m.HandlePowerEvent(new PowerEvent(_f.Time.GetUtcNow(), PowerEventKind.PowerSourceDc));
        _f.Advance(TimeSpan.FromSeconds(30), m);
        _f.Power.Status = TestData.OnAc();
        m.HandlePowerEvent(new PowerEvent(_f.Time.GetUtcNow(), PowerEventKind.PowerSourceAc));
        _f.Advance(TimeSpan.FromMinutes(5), m);
        Assert.Equal(SessionStatus.Running, m.GetSessionStatus().Status);
    }

    // ------------------------------------------------------------------ sleep/resume, lid, reliability

    [Fact]
    public void Suspend_and_resume_are_recorded_and_expired_duration_ends_after_resume()
    {
        var m = _f.NewManager();
        var s = m.StartSession(EngineFixture.Timed(TimeSpan.FromMinutes(10)));
        m.HandlePowerEvent(new PowerEvent(_f.Time.GetUtcNow(), PowerEventKind.SystemSuspending));
        Assert.Equal(1, m.GetSessionStatus().SuspendCount);

        _f.Time.Advance(TimeSpan.FromMinutes(30)); // asleep: no ticks
        m.HandlePowerEvent(new PowerEvent(_f.Time.GetUtcNow(), PowerEventKind.SystemResumed));
        m.Tick();

        Assert.Equal(SessionStatus.Completed, _f.Repository.Get(s.SessionId!.Value)!.SessionStatus);
        Assert.Contains(_f.Repository.GetEvents(s.SessionId), e => e.Message.Contains("suspending", StringComparison.Ordinal));
    }

    [Fact]
    public void Lid_events_drive_the_session_state()
    {
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Unlimited());
        m.HandlePowerEvent(new PowerEvent(_f.Time.GetUtcNow(), PowerEventKind.LidClosed));
        Assert.Equal(SessionState.LidClosed, m.GetSessionStatus().State);
        m.HandlePowerEvent(new PowerEvent(_f.Time.GetUtcNow(), PowerEventKind.LidOpened));
        Assert.Equal(SessionState.Running, m.GetSessionStatus().State);
    }

    [Fact]
    public void Lost_power_request_is_reacquired()
    {
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Unlimited());
        _f.KeepAwake.Release();
        m.Tick();
        Assert.True(_f.KeepAwake.IsHeld);
        Assert.Equal(2, _f.KeepAwake.Acquired);
    }

    [Fact]
    public void Exited_application_is_reported_but_session_continues()
    {
        var m = _f.NewManager();
        m.StartSession(EngineFixture.Timed(TimeSpan.FromHours(1)));
        _f.DeadProcesses.Add(4242);
        _f.Advance(TimeSpan.FromSeconds(2), m);
        var s = m.GetSessionStatus();
        Assert.Equal(SessionStatus.Running, s.Status);
        Assert.False(s.Applications.Single().Running);
    }

    [Fact]
    public void Repeated_monitoring_failures_trigger_safe_stop_with_restore()
    {
        var flaky = new FlakyRepository(_f.Repository);
        var m = _f.NewManager(flaky);
        m.StartSession(EngineFixture.Unlimited());
        flaky.FailUpdates = true;

        // Heartbeat persistence fails every tick once the interval has passed.
        _f.Advance(TimeSpan.FromSeconds(10 + _f.Options.MaxConsecutiveTickFailures), m);

        Assert.False(m.GetSessionStatus().HasSession);
        Assert.True(_f.SettingsAreOriginal());
        Assert.False(_f.KeepAwake.IsHeld);
        Assert.Contains("disk I/O error", m.LastError, StringComparison.Ordinal);

        // The history row could not be written, so the next service start reconciles it.
        flaky.FailUpdates = false;
        var result = _f.NewManager().RecoverOnStartup();
        Assert.Single(result.InterruptedSessions);
        Assert.Null(result.Restore); // nothing left to restore: settings were already restored
    }

    private sealed class FlakyRepository(ISessionRepository inner) : ISessionRepository
    {
        public bool FailUpdates { get; set; }

        public void Insert(SessionRecord session) => inner.Insert(session);

        public void Update(SessionRecord session)
        {
            if (FailUpdates)
            {
                throw new IOException("disk I/O error");
            }
            inner.Update(session);
        }

        public SessionRecord? Get(Guid sessionId) => inner.Get(sessionId);

        public IReadOnlyList<SessionRecord> GetActive() => inner.GetActive();

        public IReadOnlyList<SessionRecord> ListRecent(int count) => inner.ListRecent(count);

        public void AddEvent(Guid? sessionId, string type, string message, string? state = null) => inner.AddEvent(sessionId, type, message, state);

        public IReadOnlyList<SessionEventRecord> GetEvents(Guid? sessionId, int max = 500) => inner.GetEvents(sessionId, max);

        public ServiceInstanceRecord? RecordServiceStart(Guid instanceId, DateTimeOffset now, string? version) => inner.RecordServiceStart(instanceId, now, version);

        public void RecordServiceHeartbeat(Guid instanceId, DateTimeOffset now) => inner.RecordServiceHeartbeat(instanceId, now);

        public void RecordServiceStop(Guid instanceId, DateTimeOffset now) => inner.RecordServiceStop(instanceId, now);
    }
}
