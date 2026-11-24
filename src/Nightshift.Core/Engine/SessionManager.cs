using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nightshift.Core.Compatibility;
using Nightshift.Core.Power;
using Nightshift.Core.Recovery;
using Nightshift.Core.Session;

namespace Nightshift.Core.Engine;

public sealed record SessionManagerDependencies
{
    public required IPowerSettingsStore Settings { get; init; }
    public required IKeepAwakeRequest KeepAwake { get; init; }
    public required ISystemCapabilitiesProvider Capabilities { get; init; }
    public required IPowerStatusProvider PowerStatus { get; init; }
    public required IRecoveryStore Recovery { get; init; }
    public required ISessionRepository Repository { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Read-only liveness probe for watched applications.</summary>
    public Func<int, bool> IsProcessAlive { get; init; } = _ => true;
}

public sealed record StartupRecoveryResult(IReadOnlyList<Guid> InterruptedSessions, RestoreReport? Restore, bool RecoveryPending, string? Error);

/// <summary>
/// Owns the single active NIGHTSHIFT session. Host-independent: the Windows service drives it with
/// <see cref="Tick"/> and power events; tests drive it with fakes and a fake clock.
/// All public members are thread-safe (one lock; every operation is short).
/// </summary>
public sealed class SessionManager
{
    private readonly SessionManagerDependencies _deps;
    private readonly EngineOptions _options;
    private readonly ILogger _logger;
    private readonly PowerConfigurator _configurator;
    private readonly Lock _gate = new();
    private ActiveSession? _active;
    private DateTimeOffset _nextPendingRestore;

    public SessionManager(SessionManagerDependencies deps, EngineOptions? options = null, ILogger<SessionManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(deps);
        _deps = deps;
        _options = options ?? new EngineOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _configurator = new PowerConfigurator(deps.Settings, deps.Recovery);
    }

    public int ConsecutiveTickFailures { get; private set; }
    public DateTimeOffset? LastTick { get; private set; }
    public string? LastError { get; private set; }
    public bool RecoveryPending => _deps.Recovery.Exists;

    private DateTimeOffset Now => _deps.Time.GetUtcNow();

    // ------------------------------------------------------------------ startup recovery

    /// <summary>
    /// Must run once when the service starts, before any command is accepted. Restores settings left by an
    /// interrupted session (only values NIGHTSHIFT still owns) and marks such sessions as Interrupted.
    /// </summary>
    public StartupRecoveryResult RecoverOnStartup()
    {
        lock (_gate)
        {
            RestoreReport? report = null;
            string? error = null;
            RecoveryRecord? record = null;
            try
            {
                record = _deps.Recovery.Load();
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            {
                error = $"Recovery record unreadable: {ex.Message}";
                AddEvent(null, "error", error);
            }

            if (record is not null)
            {
                report = _configurator.Restore(record);
                foreach (var item in report.Items)
                {
                    AddEvent(record.SessionId, item.Outcome == RestoreOutcome.Failed ? "error" : "recovery",
                        $"Startup recovery: {Describe(item)}");
                }
                AddEvent(record.SessionId, report.Success ? "recovery" : "error", report.Success
                    ? "Startup recovery restored the original power configuration."
                    : "Startup recovery could not restore every setting; it will be retried.");
            }

            var interrupted = new List<Guid>();
            foreach (var s in _deps.Repository.GetActive())
            {
                var now = Now;
                var paused = s.PausedAt is { } p ? now - p : TimeSpan.Zero;
                _deps.Repository.Update(s with
                {
                    SessionStatus = SessionStatus.Interrupted,
                    EndTime = now,
                    TerminationReason = TerminationReason.ServiceTerminatedUnexpectedly,
                    PausedTotal = s.PausedTotal + paused,
                    PausedAt = null,
                    Error = report?.Success == false ? "Settings restore incomplete after interruption." : null,
                });
                AddEvent(s.SessionId, "recovery",
                    $"Session was interrupted (service terminated unexpectedly; last heartbeat {Fmt(s.LastHeartbeat)}). Recovered at startup.",
                    nameof(SessionStatus.Interrupted));
                interrupted.Add(s.SessionId);
            }
            return new StartupRecoveryResult(interrupted, report, _deps.Recovery.Exists, error);
        }
    }

    // ------------------------------------------------------------------ commands

    public SessionStatusSnapshot StartSession(StartSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            Validate(request);
            if (_active is not null || _deps.Repository.GetActive().Count > 0)
            {
                throw new SessionEngineException(EngineErrorCodes.SessionAlreadyActive, "A NIGHTSHIFT session is already active.");
            }
            EnsureNoPendingRecovery();

            var caps = _deps.Capabilities.Get();
            var power = _deps.PowerStatus.Get();
            var report = Preflight(caps, power, request.PowerPolicy, request.BatteryCriticalPercent);

            var now = Now;
            var record = new SessionRecord
            {
                SessionId = Guid.NewGuid(),
                OwnerSid = request.Caller.UserSid,
                OwnerName = request.Caller.UserName,
                SelectedApplications = request.Applications,
                StartTime = now,
                SessionMode = request.Mode,
                Duration = request.Mode == SessionMode.Timed ? request.Duration : null,
                SessionStatus = SessionStatus.Starting,
                PowerPolicy = request.PowerPolicy,
                PowerSource = power.EffectiveSource,
                BatteryLevelAtStart = power.BatteryPercent,
                BatteryLevelCurrent = power.BatteryPercent,
                LastHeartbeat = now,
                BatteryWarningPercent = request.BatteryWarningPercent,
                BatteryCriticalPercent = request.BatteryCriticalPercent,
                ConfigureLid = request.ConfigureLid,
                TurnOffDisplay = request.TurnOffDisplay,
            };
            try
            {
                _deps.Repository.Insert(record);
            }
            catch (DuplicateActiveSessionException)
            {
                throw new SessionEngineException(EngineErrorCodes.SessionAlreadyActive, "A NIGHTSHIFT session is already active.");
            }

            var active = new ActiveSession(record, now);
            _active = active;
            AddEvent(record.SessionId, "state", $"Session created by {record.OwnerName}: {DescribeMode(record)}, policy {record.PowerPolicy}.",
                nameof(SessionStatus.Starting));
            AddEvent(record.SessionId, "info", $"Compatibility: {report.Verdict}");
            foreach (var f in report.Findings.Where(f => f.Severity != FindingSeverity.Info))
            {
                active.Warnings.Add(f.Message);
                AddEvent(record.SessionId, "warning", $"[{f.Code}] {f.Message}");
            }

            try
            {
                ApplyAndKeepAwake(active, caps);
            }
            catch (PowerSettingAccessDeniedException ex)
            {
                throw FailStart(active, EngineErrorCodes.AccessDenied, $"Windows refused to change power settings: {ex.Message}");
            }
            catch (Exception ex) when (ex is not SessionEngineException)
            {
                throw FailStart(active, EngineErrorCodes.InternalError, $"Session could not start: {ex.Message}");
            }

            SetStatus(active, SessionStatus.Running);
            return Snapshot(active);
        }
    }

    public SessionStatusSnapshot StopSession(ClientIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        lock (_gate)
        {
            var active = RequireActive();
            Authorize(caller, active);
            return EndSession(active, TerminationReason.UserStopped);
        }
    }

    /// <summary>
    /// Pausing hands the device back to normal power behaviour: the power request is released and every
    /// modified setting is restored. The session clock stops. Resume re-applies everything after a new preflight.
    /// </summary>
    public SessionStatusSnapshot PauseSession(ClientIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        lock (_gate)
        {
            var active = RequireActive();
            Authorize(caller, active);
            if (active.Record.SessionStatus != SessionStatus.Running)
            {
                throw new SessionEngineException(EngineErrorCodes.InvalidState, $"Only a running session can be paused (status: {active.Record.SessionStatus}).");
            }
            ReleaseAndRestore(active);
            active.Record = active.Record with { PausedAt = Now };
            active.BatteryWarned = false;
            SetStatus(active, SessionStatus.Paused);
            return Snapshot(active);
        }
    }

    public SessionStatusSnapshot ResumeSession(ClientIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        lock (_gate)
        {
            var active = RequireActive();
            Authorize(caller, active);
            if (active.Record.SessionStatus != SessionStatus.Paused)
            {
                throw new SessionEngineException(EngineErrorCodes.InvalidState, $"Only a paused session can be resumed (status: {active.Record.SessionStatus}).");
            }
            EnsureNoPendingRecovery();
            var caps = _deps.Capabilities.Get();
            var power = _deps.PowerStatus.Get();
            Preflight(caps, power, active.Record.PowerPolicy, active.Record.BatteryCriticalPercent);

            try
            {
                ApplyAndKeepAwake(active, caps);
            }
            catch (PowerSettingAccessDeniedException ex)
            {
                throw new SessionEngineException(EngineErrorCodes.AccessDenied, $"Windows refused to change power settings: {ex.Message} The session stays paused.");
            }

            var now = Now;
            active.Record = active.Record with
            {
                PausedTotal = active.Record.PausedTotal + (now - active.Record.PausedAt!.Value),
                PausedAt = null,
                PowerSource = power.EffectiveSource,
                BatteryLevelCurrent = power.BatteryPercent,
            };
            active.DcSince = power.OnAcPower == false ? now : null;
            active.NextBatteryCheck = now;
            SetStatus(active, SessionStatus.Running);
            return Snapshot(active);
        }
    }

    public SessionStatusSnapshot GetSessionStatus()
    {
        lock (_gate)
        {
            return _active is null ? SessionStatusSnapshot.None : Snapshot(_active);
        }
    }

    /// <summary>The most recent session that has ended, if any.</summary>
    public SessionStatusSnapshot? GetLastSession()
    {
        lock (_gate)
        {
            var last = _deps.Repository.ListRecent(5).FirstOrDefault(s => !s.SessionStatus.IsActive());
            return last is null ? null : Snapshot(new ActiveSession(last, Now), live: false);
        }
    }

    /// <summary>Remaining time of a timed session (excluding paused time); null for unlimited or no session.</summary>
    public TimeSpan? GetRemainingTime()
    {
        lock (_gate)
        {
            return _active is null ? null : Remaining(_active.Record, Now);
        }
    }

    /// <summary>Active (not paused) time of the current session.</summary>
    public TimeSpan GetElapsedTime()
    {
        lock (_gate)
        {
            return _active is null ? TimeSpan.Zero : Elapsed(_active.Record, Now);
        }
    }

    /// <summary>Called when the host shuts down cleanly: ends the session and restores settings.</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_active is { } a)
            {
                EndSession(a, TerminationReason.ServiceStopping);
            }
        }
    }

    // ------------------------------------------------------------------ monitoring

    /// <summary>Periodic monitoring step (the service calls it about once per second).</summary>
    public void Tick()
    {
        lock (_gate)
        {
            var now = Now;
            LastTick = now;
            try
            {
                if (_active is not { } a)
                {
                    RetryPendingRestore(now);
                    ConsecutiveTickFailures = 0;
                    return;
                }

                if (a.Record.SessionStatus == SessionStatus.Running && MonitorRunning(a, now))
                {
                    ConsecutiveTickFailures = 0;
                    return; // session ended
                }

                if (now - a.LastPersist >= _options.HeartbeatPersistInterval)
                {
                    a.Record = a.Record with { LastHeartbeat = now };
                    Persist(a);
                }
                ConsecutiveTickFailures = 0;
            }
            catch (Exception ex)
            {
                ConsecutiveTickFailures++;
                LastError = ex.Message;
                _logger.LogError(ex, "Monitoring step failed ({Count} consecutive)", ConsecutiveTickFailures);
                if (ConsecutiveTickFailures >= _options.MaxConsecutiveTickFailures && _active is { } failing)
                {
                    try
                    {
                        TryAddEvent(failing.Record.SessionId, "error", $"Monitoring failed {ConsecutiveTickFailures} times in a row ({ex.Message}). Stopping safely.");
                        EndSession(failing, TerminationReason.MonitoringFailure);
                    }
                    catch (Exception stopEx)
                    {
                        _logger.LogCritical(stopEx, "Safe stop after monitoring failures failed; startup recovery will restore settings.");
                    }
                }
            }
        }
    }

    public void HandlePowerEvent(PowerEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        lock (_gate)
        {
            if (_active is not { } a)
            {
                return;
            }
            var id = a.Record.SessionId;
            switch (e.Kind)
            {
                case PowerEventKind.LidClosed:
                    a.LidClosed = true;
                    AddEvent(id, "power", e.Detail ?? "Lid closed", nameof(SessionState.LidClosed));
                    break;
                case PowerEventKind.LidOpened:
                    a.LidClosed = false;
                    AddEvent(id, "power", e.Detail ?? "Lid opened", nameof(SessionState.LidOpened));
                    break;
                case PowerEventKind.PowerSourceDc:
                    a.Record = a.Record with { PowerSource = PowerSource.DC };
                    a.DcSince ??= e.Time;
                    a.NextBatteryCheck = Now;
                    AddEvent(id, "power", a.Record.PowerPolicy == PowerPolicy.AcOnly
                        ? $"Charger disconnected. AC-only session: stops safely in {_options.AcLossGracePeriod.TotalSeconds:0}s unless reconnected."
                        : "Charger disconnected: running on battery; battery thresholds now apply.");
                    Persist(a);
                    break;
                case PowerEventKind.PowerSourceAc:
                    a.Record = a.Record with { PowerSource = PowerSource.AC };
                    a.DcSince = null;
                    a.NextBatteryCheck = Now;
                    if (a.BatteryWarned)
                    {
                        a.BatteryWarned = false;
                    }
                    AddEvent(id, "power", "Charger connected: running on AC power.");
                    Persist(a);
                    break;
                case PowerEventKind.SystemSuspending:
                    a.Suspends++;
                    AddEvent(id, "error", "Windows is suspending during the session: applications will pause until resume.");
                    break;
                case PowerEventKind.SystemResumed:
                    a.NextBatteryCheck = Now;
                    AddEvent(id, "power", e.Detail ?? "System resumed");
                    break;
                case PowerEventKind.DisplayOff:
                case PowerEventKind.DisplayOn:
                case PowerEventKind.DisplayDimmed:
                    AddEvent(id, "power", e.Detail ?? e.Kind.ToString());
                    break;
                case PowerEventKind.BatteryPercentChanged:
                    break; // sampled by the battery poll; not logged to keep history small
            }
        }
    }

    // ------------------------------------------------------------------ internals

    private bool MonitorRunning(ActiveSession a, DateTimeOffset now)
    {
        if (!_deps.KeepAwake.IsHeld)
        {
            _deps.KeepAwake.Acquire();
            AddEvent(a.Record.SessionId, "warning", "Power request was not held; re-acquired.");
        }

        if (a.Record.Duration is { } d && Elapsed(a.Record, now) >= d)
        {
            AddEvent(a.Record.SessionId, "info", "Session duration elapsed.");
            EndSession(a, TerminationReason.DurationElapsed);
            return true;
        }

        if (now >= a.NextBatteryCheck)
        {
            a.NextBatteryCheck = now + _options.BatteryPollInterval;
            if (CheckBattery(a))
            {
                EndSession(a, TerminationReason.BatteryCritical);
                return true;
            }
        }

        if (a.Record.PowerPolicy == PowerPolicy.AcOnly && a.DcSince is { } since && now - since >= _options.AcLossGracePeriod)
        {
            AddEvent(a.Record.SessionId, "warning", "Charger stayed disconnected beyond the grace period (AC-only session). Stopping safely.");
            EndSession(a, TerminationReason.AcPowerLost);
            return true;
        }

        foreach (var app in a.Record.SelectedApplications)
        {
            if (app.ProcessId is { } pid && !a.Exited.Contains(pid) && !_deps.IsProcessAlive(pid))
            {
                a.Exited.Add(pid);
                AddEvent(a.Record.SessionId, "warning", $"Selected application '{app.Name}' (PID {pid}) has exited. The session continues.");
            }
        }
        return false;
    }

    private bool CheckBattery(ActiveSession a)
    {
        var p = _deps.PowerStatus.Get();
        a.Record = a.Record with { BatteryLevelCurrent = p.BatteryPercent, PowerSource = p.EffectiveSource };
        if (p.OnAcPower != false || p.BatteryPercent is not { } pct)
        {
            a.BatteryWarned = false;
            a.DcSince = null;
            return false;
        }
        a.DcSince ??= Now;
        if (pct <= a.Record.BatteryCriticalPercent)
        {
            AddEvent(a.Record.SessionId, "warning", $"Battery critical ({pct}%). Stopping the session safely and restoring settings.");
            return true;
        }
        if (pct <= a.Record.BatteryWarningPercent && !a.BatteryWarned)
        {
            a.BatteryWarned = true;
            AddEvent(a.Record.SessionId, "warning", $"Battery low ({pct}%). The session stops at {a.Record.BatteryCriticalPercent}%.", nameof(SessionState.Warning));
        }
        return false;
    }

    private void ApplyAndKeepAwake(ActiveSession a, SystemCapabilities caps)
    {
        var method = a.Record.TurnOffDisplay ? DisplayOffMethod.IdleTimeout : DisplayOffMethod.None;
        var plan = SessionPlanner.Plan(new SessionOptions
        {
            ConfigureLid = a.Record.ConfigureLid,
            DisplayIdleTimeoutSeconds = _options.DisplayIdleTimeoutSeconds,
        }, caps, method);

        var recovery = new RecoveryRecord { SessionId = a.Record.SessionId, StartedAt = Now, OwnerProcessId = Environment.ProcessId };
        a.Recovery = _configurator.Apply(recovery, plan); // rolls itself back on failure
        var (original, modified) = SessionRecord.FromChanges(a.Recovery.Changes);
        a.Record = a.Record with { OriginalPowerSettings = original, ModifiedPowerSettings = modified };
        foreach (var c in a.Recovery.Changes)
        {
            AddEvent(a.Record.SessionId, "change", $"{c.Name} [{c.Source}] {c.OriginalValue} -> {c.AppliedValue} (original saved)");
        }
        Persist(a);

        try
        {
            _deps.KeepAwake.Acquire();
        }
        catch
        {
            ReleaseAndRestore(a);
            throw;
        }
        AddEvent(a.Record.SessionId, "info", "System-required power request acquired: automatic sleep blocked, display may turn off.");
    }

    /// <summary>Releases the power request and restores settings. Never throws.</summary>
    private RestoreReport? ReleaseAndRestore(ActiveSession a)
    {
        var id = a.Record.SessionId;
        try
        {
            if (_deps.KeepAwake.IsHeld)
            {
                _deps.KeepAwake.Release();
                TryAddEvent(id, "info", "Power request released.");
            }
        }
        catch (Exception ex)
        {
            TryAddEvent(id, "error", $"Releasing the power request failed: {ex.Message}");
        }

        if (a.Recovery is null)
        {
            return null;
        }
        RestoreReport report;
        try
        {
            report = _configurator.Restore(a.Recovery);
        }
        catch (Exception ex)
        {
            TryAddEvent(id, "error", $"Restore failed: {ex.Message}. It will be retried.");
            return null;
        }
        foreach (var item in report.Items)
        {
            TryAddEvent(id, item.Outcome == RestoreOutcome.Failed ? "error" : "restore", Describe(item));
        }
        TryAddEvent(id, report.Success ? "info" : "error", report.Success
            ? "Original power configuration restored."
            : "Restore incomplete; the recovery record was kept and will be retried.");
        a.Recovery = null;
        return report;
    }

    private SessionStatusSnapshot EndSession(ActiveSession a, TerminationReason reason)
    {
        var wasPaused = a.Record.SessionStatus == SessionStatus.Paused;
        try
        {
            SetStatus(a, SessionStatus.Stopping);
        }
        catch (Exception ex)
        {
            // Restoring the system matters more than recording it: continue regardless.
            _logger.LogError(ex, "Could not persist Stopping for session {SessionId}", a.Record.SessionId);
        }
        var report = ReleaseAndRestore(a);
        var now = Now;
        var status = reason switch
        {
            TerminationReason.DurationElapsed => SessionStatus.Completed,
            TerminationReason.BatteryCritical or TerminationReason.AcPowerLost or TerminationReason.MonitoringFailure => SessionStatus.SafeStopped,
            TerminationReason.StartupFailed => SessionStatus.Failed,
            _ => SessionStatus.Stopped,
        };
        a.Record = a.Record with
        {
            EndTime = now,
            TerminationReason = reason,
            LastHeartbeat = now,
            PausedTotal = wasPaused && a.Record.PausedAt is { } p ? a.Record.PausedTotal + (now - p) : a.Record.PausedTotal,
            PausedAt = null,
            Error = report?.Success == false || _deps.Recovery.Exists ? "Settings restore incomplete; retrying automatically." : a.Record.Error,
        };
        _active = null;
        try
        {
            SetStatus(a, status, $"Session ended: {reason}.");
        }
        catch (Exception ex)
        {
            // The history row could not be written; settings are already restored.
            _logger.LogError(ex, "Could not persist the end of session {SessionId}", a.Record.SessionId);
        }
        return Snapshot(a, live: false);
    }

    private SessionEngineException FailStart(ActiveSession a, string code, string message)
    {
        ReleaseAndRestore(a);
        a.Record = a.Record with { EndTime = Now, TerminationReason = TerminationReason.StartupFailed, Error = message };
        _active = null;
        try
        {
            SetStatus(a, SessionStatus.Failed, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist start failure");
        }
        return new SessionEngineException(code, message);
    }

    private CompatibilityReport Preflight(SystemCapabilities caps, PowerStatus power, PowerPolicy policy, int criticalPercent)
    {
        var scheme = _deps.Settings.GetActiveSchemeId();
        var report = CompatibilityAssessor.Assess(caps, power,
            _deps.Settings.Read(scheme, PowerSettingKeys.LidCloseAction, PowerSource.AC),
            _deps.Settings.Read(scheme, PowerSettingKeys.LidCloseAction, PowerSource.DC));
        if (!report.CanStart)
        {
            throw new SessionEngineException(EngineErrorCodes.UnsupportedSystem,
                string.Join(" ", report.Findings.Where(f => f.Severity == FindingSeverity.Blocker).Select(f => f.Message)));
        }
        if (power.OnAcPower == false && power.BatteryPercent is { } pct && pct <= criticalPercent)
        {
            throw new SessionEngineException(EngineErrorCodes.BatteryTooLow, $"Battery is at {pct}% (critical threshold {criticalPercent}%). Connect the charger.");
        }
        if (policy == PowerPolicy.AcOnly && power.OnAcPower == false)
        {
            throw new SessionEngineException(EngineErrorCodes.AcPowerRequired, "This session requires AC power. Connect the charger.");
        }
        return report;
    }

    private void EnsureNoPendingRecovery()
    {
        if (!_deps.Recovery.Exists)
        {
            return;
        }
        RetryPendingRestore(Now, force: true);
        if (_deps.Recovery.Exists)
        {
            throw new SessionEngineException(EngineErrorCodes.RecoveryPending,
                "Settings from an earlier session could not be restored yet. See the service log.");
        }
    }

    private void RetryPendingRestore(DateTimeOffset now, bool force = false)
    {
        if (!_deps.Recovery.Exists || (!force && now < _nextPendingRestore))
        {
            return;
        }
        _nextPendingRestore = now + _options.PendingRestoreRetryInterval;
        var record = _deps.Recovery.Load();
        if (record is null)
        {
            return;
        }
        var report = _configurator.Restore(record);
        AddEvent(record.SessionId, report.Success ? "recovery" : "error", report.Success
            ? "Pending settings restore completed."
            : "Pending settings restore failed again; will retry.");
    }

    private static void Validate(StartSessionRequest r)
    {
        if (!r.VentilationAcknowledged)
        {
            throw new SessionEngineException(EngineErrorCodes.VentilationAckRequired,
                "The ventilation warning must be acknowledged before a closed-lid session can start.");
        }
        if (r.Mode == SessionMode.Timed && (r.Duration is not { } d || d <= TimeSpan.Zero || d > TimeSpan.FromDays(7)))
        {
            throw new SessionEngineException(EngineErrorCodes.InvalidRequest, "Timed sessions need a duration between 1 second and 7 days.");
        }
        if (r.Mode == SessionMode.Unlimited && r.Duration is not null)
        {
            throw new SessionEngineException(EngineErrorCodes.InvalidRequest, "Unlimited sessions must not have a duration.");
        }
        if (r.BatteryCriticalPercent is < 5 or > 50 || r.BatteryWarningPercent <= r.BatteryCriticalPercent || r.BatteryWarningPercent > 95)
        {
            throw new SessionEngineException(EngineErrorCodes.InvalidRequest,
                "Battery thresholds: critical 5–50 %, warning above critical and at most 95 %.");
        }
    }

    private static void Authorize(ClientIdentity caller, ActiveSession a)
    {
        if (caller.IsAdministrator || string.Equals(caller.UserSid, a.Record.OwnerSid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new SessionEngineException(EngineErrorCodes.NotAuthorized, "Only the user who started the session, or an administrator, can control it.");
    }

    private ActiveSession RequireActive() =>
        _active ?? throw new SessionEngineException(EngineErrorCodes.NoActiveSession, "There is no active session.");

    private void SetStatus(ActiveSession a, SessionStatus status, string? message = null)
    {
        var previous = a.Record.SessionStatus;
        a.Record = a.Record with { SessionStatus = status };
        Persist(a);
        if (previous != status)
        {
            AddEvent(a.Record.SessionId, "state", message ?? $"{previous} -> {status}", status.ToString());
        }
    }

    private void Persist(ActiveSession a)
    {
        _deps.Repository.Update(a.Record);
        a.LastPersist = Now;
    }

    private void AddEvent(Guid? id, string type, string message, string? state = null)
    {
        _deps.Repository.AddEvent(id, type, message, state);
        var level = type switch { "error" => LogLevel.Error, "warning" => LogLevel.Warning, _ => LogLevel.Information };
#pragma warning disable CA1848, CA2254 // event text is already formatted for the user-facing history
        _logger.Log(level, "[{Session}] {Message}", id, message);
#pragma warning restore CA1848, CA2254
    }

    private void TryAddEvent(Guid? id, string type, string message)
    {
        try
        {
            AddEvent(id, type, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record event: {Message}", message);
        }
    }

    private SessionStatusSnapshot Snapshot(ActiveSession a, bool live = true)
    {
        var r = a.Record;
        var now = r.EndTime ?? Now;
        return new SessionStatusSnapshot
        {
            SessionId = r.SessionId,
            Status = r.SessionStatus,
            State = !live ? SessionState.Idle : r.SessionStatus switch
            {
                SessionStatus.Starting => SessionState.Preflight,
                SessionStatus.Paused => SessionState.Paused,
                SessionStatus.Stopping => SessionState.Restoring,
                _ when a.BatteryWarned => SessionState.Warning,
                _ when a.LidClosed => SessionState.LidClosed,
                _ => SessionState.Running,
            },
            Mode = r.SessionMode,
            StartTime = r.StartTime,
            EndTime = r.EndTime,
            Duration = r.Duration,
            Elapsed = Elapsed(r, now),
            Remaining = Remaining(r, now),
            PowerPolicy = r.PowerPolicy,
            PowerSource = r.PowerSource,
            BatteryLevelAtStart = r.BatteryLevelAtStart,
            BatteryLevelCurrent = r.BatteryLevelCurrent,
            KeepAwakeHeld = live && _deps.KeepAwake.IsHeld,
            LidClosed = live && a.LidClosed,
            SuspendCount = a.Suspends,
            Applications = [.. r.SelectedApplications.Select(app => new ApplicationStatus(app,
                app.ProcessId is { } pid && live ? !a.Exited.Contains(pid) && _deps.IsProcessAlive(pid) : null))],
            OriginalPowerSettings = r.OriginalPowerSettings,
            ModifiedPowerSettings = r.ModifiedPowerSettings,
            LastHeartbeat = r.LastHeartbeat,
            TerminationReason = r.TerminationReason,
            OwnerName = r.OwnerName,
            Warnings = [.. a.Warnings],
        };
    }

    private static TimeSpan Elapsed(SessionRecord r, DateTimeOffset now)
    {
        var end = r.EndTime ?? now;
        var paused = r.PausedTotal + (r.PausedAt is { } p ? end - p : TimeSpan.Zero);
        var elapsed = end - r.StartTime - paused;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private static TimeSpan? Remaining(SessionRecord r, DateTimeOffset now)
    {
        if (r.Duration is not { } d)
        {
            return null;
        }
        var left = d - Elapsed(r, now);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    private static string Describe(RestoreItem i) =>
        $"{i.Change.Name} [{i.Change.Source}]: {i.Outcome}" +
        (i.Outcome == RestoreOutcome.SkippedModifiedByOthers ? $" (now {i.ValueFound}; changed by someone else, left untouched)" : string.Empty) +
        (i.Error is null ? string.Empty : $" — {i.Error}");

    private static string DescribeMode(SessionRecord r) =>
        r.Duration is { } d ? $"timed {d.ToString("c", CultureInfo.InvariantCulture)}" : "unlimited";

    private static string Fmt(DateTimeOffset? t) => t?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown";

    private sealed class ActiveSession(SessionRecord record, DateTimeOffset now)
    {
        public SessionRecord Record { get; set; } = record;
        public RecoveryRecord? Recovery { get; set; }
        public bool LidClosed { get; set; }
        public bool BatteryWarned { get; set; }
        public DateTimeOffset? DcSince { get; set; }
        public int Suspends { get; set; }
        public DateTimeOffset LastPersist { get; set; } = now;
        public DateTimeOffset NextBatteryCheck { get; set; } = now;
        public HashSet<int> Exited { get; } = [];
        public List<string> Warnings { get; } = [];
    }
}
