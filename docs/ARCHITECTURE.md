# Architecture

## Components

```
            ┌──────────────────────────┐        named pipe \\.\pipe\NightshiftService.v1
 user  ───▶ │ nightshiftctl (Phase 2)  │ ─────┐  (local only, ACL'd, Identification level)
 session    │ tray UI      (Phase 5)   │      │
            └──────────────────────────┘      ▼
                                   ┌──────────────────────────────────────────────┐
  LocalSystem, session 0           │ Nightshift.Service (Windows Worker Service)   │
                                   │  SessionWorker ── IpcServer ── SessionCommand │
                                   │       │                          Handler      │
                                   │       ▼                             │         │
                                   │  SessionManager ◀───────────────────┘         │
                                   │   │   │    │                                  │
                                   │   │   │    └─ CallbackPowerEventSource (lid,  │
                                   │   │   │       AC/DC, display, suspend/resume) │
                                   │   │   └─ PowerConfigurator (Phase 1) ─ PowrProf│
                                   │   │      WindowsKeepAwakeRequest (Phase 1)     │
                                   │   └─ SQLite: sessions · events · recovery ·    │
                                   │              service_instances                 │
                                   └──────────────────────────────────────────────┘
 nightshift-poc (Phase 1) — standalone CLI; refuses to run while the service has a session.
```

```
src/
  Nightshift.Core/        platform-neutral, unit-tested
    Power/                models, abstractions, PowerConfigurator (apply + restore with undo log)
    Recovery/             RecoveryRecord, IRecoveryStore, RecoveryStore (JSON, Phase 1 CLI)
    Compatibility/        CompatibilityAssessor
    Session/              Phase 1 PocSession, SessionPlanner, SessionState, SessionEventLog
    Engine/               Phase 2: SessionManager, session model, repository contract, ProcessProbe
    Verification/, Util/
  Nightshift.Windows/     Win32: PowrProf store, capabilities, power request, PowerEventWindow (CLI),
                          CallbackPowerEventSource (service, no window)
  Nightshift.Storage/     SQLite: Database (WAL, synchronous=FULL, user_version), SqliteSessionRepository,
                          SqliteRecoveryStore
  Nightshift.Ipc/         protocol, framing, validation (SessionCommandHandler), IpcServer, NightshiftClient,
                          client identity resolvers
  Nightshift.Service/     worker host, SessionWorker, ServiceHealth, DataDirectorySecurity, composition
  Nightshift.Ctl/         nightshiftctl CLI client
  Nightshift.Poc/         Phase 1 CLI (unchanged behaviour + service-conflict guard)
tests/
  Nightshift.Core.Tests/              Phase 1 logic (52)
  Nightshift.Engine.Tests/            engine, SQLite, real pipes, in-process service host (68)
  Nightshift.Windows.IntegrationTests real Win32 APIs; skipped off Windows (7)
installer/                            install-service.ps1 / uninstall-service.ps1 (developer installer)
```

## Why a LocalSystem service

* Power schemes and the lid action are **machine-wide**; the service can change them without asking the user
  for elevation, so the UI never needs admin rights.
* It keeps running when the UI closes, the user logs off, or the console is closed.
* Power requests from a service are honoured like any other (`powercfg /requests` shows `[SERVICE]`).
* Session 0 has no user desktop, so the service uses callback-based power notifications (no window) and turns
  the display off only through the tracked display-idle timeout (never `SC_MONITORPOWER`).

Least privilege: the installer restricts the service token to `SeChangeNotifyPrivilege`,
`SeImpersonatePrivilege`, `SeCreateGlobalPrivilege` (`sc.exe privs`); the service never launches user-supplied
programs; clients connect at Identification level; the data directory is SYSTEM/Administrators only.

## Session lifecycle (service)

```
            START (validated, ventilation ack, preflight)
                │ insert row (is_active=1; unique index ⇒ one active session)
                ▼
            Starting ── apply: for each change read original → save recovery → write → confirm
                │       acquire system-required power request
                ▼                    PAUSE: release request + restore all ──▶ Paused
            Running ◀──────────────────────────────────────── RESUME: preflight + re-apply
     (LidClosed / Warning are fine-grained states of Running)
                │ duration elapsed · STOP · battery critical · AC lost (ac-only) · 5 monitoring failures · service stop
                ▼
            Stopping: release request → restore (only values still ours) → delete recovery
                ▼
   Completed · Stopped · SafeStopped     (Failed if start failed; Interrupted if the service died)
```

Elapsed and remaining time exclude paused time. Every transition is written to `session_events`.

## Persistence (SQLite, `%ProgramData%\Nightshift\nightshift.db`)

| Table | Purpose |
|---|---|
| `sessions` | the session model: id, owner, selected applications, start/end (UTC), mode, duration, status, power policy/source, battery at start/current, original & modified settings (JSON), last heartbeat, termination reason, paused time, thresholds, `is_active` (partial unique index) |
| `session_events` | state transitions, power events, changes, restores, recovery (bounded messages; no app content) |
| `recovery` | single-row undo log for the settings currently modified (written before each change) |
| `service_instances` | start / heartbeat / clean-stop per service process ⇒ detects unexpected termination |

WAL + `synchronous=FULL`: a committed write survives a crash or power loss. Schema version in `PRAGMA user_version`;
a newer database is refused rather than guessed at.

## Crash recovery

1. **Before** any power-setting write, its original value is committed to `recovery` (Phase 1 invariant, now in SQLite).
2. The power request needs no recovery: Windows drops it when the process dies.
3. On every service start, **before the pipe opens**: the previous instance's clean-shutdown flag is checked;
   the recovery record is restored — **only settings that still hold NIGHTSHIFT's value** are written back
   (newer user/admin changes are kept and reported); active sessions are marked `Interrupted`
   (`ServiceTerminatedUnexpectedly`) and the event is recorded.
4. If a restore fails, the record is kept, new sessions are refused (`RECOVERY_PENDING`) and the restore is retried every minute.
5. The SCM restarts the service after a crash (5 s / 15 s / 60 s), so recovery does not wait for a reboot.

## Reliability

* `Tick()` every second: duration, battery (30 s), AC-only grace (60 s), watched PIDs, power-request integrity
  (re-acquired if lost), session heartbeat persisted every 10 s.
* Monitoring exceptions are counted; after 5 consecutive failures the session is stopped safely and settings restored.
  Restoring never depends on the database write succeeding.
* Service heartbeat every 10 s in `service_instances`; `GET_SYSTEM_STATUS` reports health.
* Clean shutdown (service stop, OS shutdown, uninstall): IPC closes, the session ends with `ServiceStopping`, settings
  are restored, the instance is marked clean. Host shutdown timeout: 20 s.

## Safety invariants (unchanged from Phase 1, now enforced by the service)

1. Original values are durable before a setting is written.
2. Restore only reverts values NIGHTSHIFT still owns; the active power scheme is never switched.
3. Sleep/hibernate timeouts are never modified.
4. A pending recovery blocks new sessions.
5. Unlimited mode keeps every safety check (battery, AC policy, monitoring failures).
6. Applications are only observed.
7. No application content, terminal output or screenshots are read or stored.

## Deferred

Thermal & network monitoring, ventilation UI (Phase 3) · process-tree profiles for Claude Code / Codex (Phase 4) ·
tray UI & dashboard (Phase 5) · MSI installer, startup integration (Phase 6).
