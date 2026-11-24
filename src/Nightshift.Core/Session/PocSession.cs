using System.Collections.Concurrent;
using System.Globalization;
using Nightshift.Core.Compatibility;
using Nightshift.Core.Power;
using Nightshift.Core.Recovery;

namespace Nightshift.Core.Session;

public sealed record SessionDependencies
{
    public required IPowerSettingsStore Settings { get; init; }
    public required IKeepAwakeRequest KeepAwake { get; init; }
    public required ISystemCapabilitiesProvider Capabilities { get; init; }
    public required IPowerStatusProvider PowerStatus { get; init; }
    public required IDisplayController Display { get; init; }
    public required IPowerEventSource PowerEvents { get; init; }
    public required RecoveryStore Recovery { get; init; }
    public required SessionEventLog Log { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Liveness probe for watched processes. Monitoring only — never used to control a process.</summary>
    public Func<int, bool> IsProcessAlive { get; init; } = _ => true;
}

public sealed record SessionResult(
    Guid SessionId,
    StopReason Reason,
    TimeSpan Elapsed,
    int LidCloseCount,
    int SuspendCount,
    RestoreReport? Restore,
    IReadOnlyList<int> ExitedProcessIds,
    string? Error = null);

/// <summary>
/// Phase 1 in-process session: preflight → back up + apply → keep awake → monitor → restore.
/// Phase 2 moves this into the Windows service; the logic is deliberately host-independent.
/// </summary>
public sealed class PocSession(SessionOptions options, SessionDependencies deps)
{
    private readonly ConcurrentQueue<PowerEvent> _events = new();
    private readonly Lock _restoreGate = new();
    private readonly HashSet<int> _exited = [];
    private RecoveryRecord? _record;
    private RestoreReport? _restoreReport;
    private bool _restored;
    private int _lidCloses;
    private int _suspends;
    private bool _batteryWarned;

    public Guid SessionId { get; } = Guid.NewGuid();
    public SessionState State { get; private set; } = SessionState.Idle;
    public CompatibilityReport? Compatibility { get; private set; }
    public DisplayOffMethod DisplayMethod { get; private set; }

    public async Task<SessionResult> RunAsync(CancellationToken cancellationToken)
    {
        var log = deps.Log;
        var startedAt = deps.Time.GetUtcNow();
        StopReason reason;
        string? error = null;

        try
        {
            Transition(SessionState.Preflight);
            if (!Preflight(out var startupError))
            {
                return Fail(startupError);
            }

            var caps = deps.Capabilities.Get();
            DisplayMethod = SessionOptions.Resolve(options.DisplayOff, caps);
            var plan = SessionPlanner.Plan(options, caps, DisplayMethod);

            var configurator = new PowerConfigurator(deps.Settings, deps.Recovery);
            var record = new RecoveryRecord
            {
                SessionId = SessionId,
                StartedAt = startedAt,
                OwnerProcessId = Environment.ProcessId,
                EventLogPath = log.Path,
            };
            try
            {
                _record = configurator.Apply(record, plan);
            }
            catch (PowerSettingAccessDeniedException ex)
            {
                return Fail($"Windows refused to change power settings: {ex.Message} Run from an elevated terminal and try again. " +
                            "Any partial change was rolled back.");
            }
            foreach (var c in _record.Changes)
            {
                log.Write("change", $"{c.Name} [{c.Source}] {c.OriginalValue} -> {c.AppliedValue} (original backed up)", evt: "SettingChanged");
            }
            if (_record.Changes.Count == 0)
            {
                log.Write("info", "No power-scheme changes were needed.");
            }

            deps.KeepAwake.Acquire();
            log.Write("info", "System-required power request acquired: automatic sleep is blocked, display may turn off.", evt: "KeepAwakeAcquired");

            deps.PowerEvents.PowerEventReceived += _events.Enqueue;
            deps.PowerEvents.Start();

            Transition(SessionState.Running);
            log.Write("info", options.Duration is null
                ? "Unlimited session: runs until stopped manually or a safety condition occurs."
                : $"Timed session: {options.Duration.Value.ToString("c", CultureInfo.InvariantCulture)}.");

            reason = await MonitorAsync(startedAt, cancellationToken).ConfigureAwait(false);
            Transition(reason is StopReason.BatteryCritical ? SessionState.SafeStop : SessionState.Completed);
        }
        catch (Exception ex)
        {
            reason = StopReason.StartupFailed;
            error = ex.Message;
            log.Write("error", $"Session failed: {ex.Message}");
            Transition(SessionState.Error);
        }
        finally
        {
            deps.PowerEvents.PowerEventReceived -= _events.Enqueue;
        }

        RestoreNow();
        return new SessionResult(SessionId, reason, deps.Time.GetUtcNow() - startedAt, _lidCloses, _suspends,
            _restoreReport, [.. _exited], error);

        SessionResult Fail(string message)
        {
            log.Write("error", message);
            Transition(SessionState.Error);
            RestoreNow();
            Transition(SessionState.Idle);
            return new SessionResult(SessionId, StopReason.StartupFailed, TimeSpan.Zero, 0, 0, _restoreReport, [], message);
        }
    }

    /// <summary>
    /// Synchronous, idempotent restore. Safe to call from a console-close/shutdown handler while
    /// <see cref="RunAsync"/> is still running.
    /// </summary>
    public RestoreReport? RestoreNow()
    {
        lock (_restoreGate)
        {
            if (_restored)
            {
                return _restoreReport;
            }
            _restored = true;
            if (State is not SessionState.Idle)
            {
                Transition(SessionState.Restoring);
            }

            try
            {
                if (deps.KeepAwake.IsHeld)
                {
                    deps.KeepAwake.Release();
                    deps.Log.Write("info", "Power request released.", evt: "KeepAwakeReleased");
                }
            }
            catch (Exception ex)
            {
                deps.Log.Write("error", $"Releasing the power request failed: {ex.Message}");
            }

            if (_record is not null)
            {
                _restoreReport = new PowerConfigurator(deps.Settings, deps.Recovery).Restore(_record);
                foreach (var item in _restoreReport.Items)
                {
                    deps.Log.Write(item.Outcome == RestoreOutcome.Failed ? "error" : "restore",
                        $"{item.Change.Name} [{item.Change.Source}]: {item.Outcome}" +
                        (item.Outcome == RestoreOutcome.SkippedModifiedByOthers
                            ? $" (found {item.ValueFound}, left untouched because it was changed by someone else)"
                            : string.Empty) +
                        (item.Error is null ? string.Empty : $" — {item.Error}"),
                        evt: "SettingRestored");
                }
                deps.Log.Write(_restoreReport.Success ? "info" : "error",
                    _restoreReport.Success
                        ? "Original power configuration restored."
                        : "Restoration incomplete. The recovery record was kept; run 'nightshift-poc restore' to retry.");
            }
            // No record of ours: never delete a record left by an earlier session — it is needed to restore.

            Transition(_restoreReport?.Success == false ? SessionState.Error : SessionState.Idle);
            return _restoreReport;
        }
    }

    private bool Preflight(out string error)
    {
        error = string.Empty;
        if (deps.Recovery.Exists)
        {
            error = "A previous session did not finish restoring the power configuration. Run 'nightshift-poc restore' first.";
            return false;
        }

        var caps = deps.Capabilities.Get();
        var power = deps.PowerStatus.Get();
        var scheme = deps.Settings.GetActiveSchemeId();
        var lidAc = deps.Settings.Read(scheme, PowerSettingKeys.LidCloseAction, PowerSource.AC);
        var lidDc = deps.Settings.Read(scheme, PowerSettingKeys.LidCloseAction, PowerSource.DC);
        Compatibility = CompatibilityAssessor.Assess(caps, power, lidAc, lidDc);
        deps.Log.Write("info", $"Compatibility: {Compatibility.Verdict}");
        foreach (var finding in Compatibility.Findings)
        {
            deps.Log.Write(finding.Severity == FindingSeverity.Info ? "info" : "warning", $"[{finding.Code}] {finding.Message}");
        }

        if (!Compatibility.CanStart)
        {
            error = "This system is not supported.";
            return false;
        }
        if (power.OnAcPower == false && power.BatteryPercent is { } pct && pct <= options.BatteryCriticalPercent)
        {
            error = $"Battery is at {pct}% (critical threshold {options.BatteryCriticalPercent}%). Connect the charger first.";
            return false;
        }
        return true;
    }

    private async Task<StopReason> MonitorAsync(DateTimeOffset startedAt, CancellationToken ct)
    {
        var log = deps.Log;
        var displayOffDue = DisplayMethod == DisplayOffMethod.MonitorPower ? startedAt + options.DisplayOffDelay : (DateTimeOffset?)null;
        var nextBatteryCheck = startedAt;

        while (true)
        {
            try
            {
                await Task.Delay(options.TickInterval, deps.Time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                DrainEvents();
                log.Write("info", "Session stopped by user.");
                return StopReason.UserStopped;
            }

            var now = deps.Time.GetUtcNow();
            DrainEvents();

            if (displayOffDue is { } due && now >= due)
            {
                displayOffDue = null;
                try
                {
                    deps.Display.TurnOffDisplay();
                    log.Write("info", "Display-off command sent (SC_MONITORPOWER). The system stays awake.", evt: "DisplayOffRequested");
                }
                catch (Exception ex)
                {
                    log.Write("warning", $"Display-off command failed: {ex.Message}");
                }
            }

            foreach (var pid in options.WatchedProcessIds)
            {
                if (!_exited.Contains(pid) && !deps.IsProcessAlive(pid))
                {
                    _exited.Add(pid);
                    log.Write("warning", $"Watched process {pid} has exited. The session continues (monitoring only).", evt: "ProcessExited");
                }
            }

            if (now >= nextBatteryCheck)
            {
                nextBatteryCheck = now + options.BatteryPollInterval;
                var stop = CheckBattery();
                if (stop)
                {
                    return StopReason.BatteryCritical;
                }
            }

            if (options.Duration is { } d && now - startedAt >= d)
            {
                log.Write("info", "Session duration elapsed.");
                return StopReason.DurationElapsed;
            }
        }
    }

    private bool CheckBattery()
    {
        var p = deps.PowerStatus.Get();
        if (p.OnAcPower != false || p.BatteryPercent is not { } pct)
        {
            if (_batteryWarned && State == SessionState.Warning)
            {
                _batteryWarned = false;
                Transition(SessionState.Running);
            }
            return false;
        }
        if (pct <= options.BatteryCriticalPercent)
        {
            deps.Log.Write("warning", $"Battery critical ({pct}%). Stopping the session safely and restoring settings.", evt: "BatteryCritical");
            return true;
        }
        if (pct <= options.BatteryWarningPercent && !_batteryWarned)
        {
            _batteryWarned = true;
            Transition(SessionState.Warning);
            deps.Log.Write("warning", $"Battery low ({pct}%). The session will stop at {options.BatteryCriticalPercent}%.", evt: "BatteryLow");
        }
        return false;
    }

    private void DrainEvents()
    {
        while (_events.TryDequeue(out var e))
        {
            deps.Log.Write("power", e.Detail ?? e.Kind.ToString(), evt: e.Kind.ToString());
            switch (e.Kind)
            {
                case PowerEventKind.LidClosed:
                    _lidCloses++;
                    Transition(SessionState.LidClosed);
                    break;
                case PowerEventKind.LidOpened when State == SessionState.LidClosed:
                    Transition(SessionState.LidOpened);
                    Transition(SessionState.Running);
                    break;
                case PowerEventKind.SystemSuspending:
                    _suspends++;
                    deps.Log.Write("error",
                        "Windows is suspending despite the session. On this device the configuration did NOT keep the system awake.",
                        evt: "UnexpectedSuspend");
                    break;
            }
        }
    }

    private void Transition(SessionState next)
    {
        if (State == next)
        {
            return;
        }
        var previous = State;
        State = next;
        deps.Log.Write("state", $"{previous} -> {next}", state: next.ToString());
    }
}
