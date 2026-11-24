# Architecture (Phase 1)

```
src/
  Nightshift.Core/        platform-neutral, unit-tested
    Power/                models, abstractions, PowerConfigurator (apply + restore with undo log)
    Recovery/             RecoveryRecord, RecoveryStore (atomic JSON)
    Compatibility/        CompatibilityAssessor (honest verdict + findings)
    Session/              SessionOptions, SessionPlanner, PocSession (state machine), SessionEventLog (JSONL)
    Verification/         HeartbeatWriter (test app), HeartbeatAnalyzer (gap + lid-interval verdict)
    Util/                 DurationParser, AtomicFile, AppPaths, UserSettings, SafetyText
  Nightshift.Windows/     Win32 implementations (SupportedOSPlatform("windows"))
    WindowsPowerSettingsStore   PowrProf read/write/activate
    WindowsSystemInfo           SYSTEM_POWER_CAPABILITIES, GetSystemPowerStatus, lid Group Policy check
    WindowsKeepAwakeRequest     PowerCreateRequest / PowerSetRequest(SystemRequired)
    PowerEventWindow            hidden window: power-setting notifications + SC_MONITORPOWER target
  Nightshift.Poc/         CLI: detect · session · restore · status · heartbeat · verify
tests/Nightshift.Core.Tests/    xUnit, fakes for every Windows dependency
```

## Session flow

```
IDLE → PREFLIGHT ──(blocker / low battery / pending recovery)──→ ERROR → IDLE
          │
          ├─ assess compatibility, choose display-off method
          ├─ for each planned change: read original → persist undo record → write → confirm
          ├─ acquire system-required power request
          └─ start power-event window
       RUNNING ⇄ LID_CLOSED → LID_OPENED → RUNNING
          │  (WARNING on low battery)
          ├─ duration elapsed / Ctrl+C  → COMPLETED
          └─ battery critical            → SAFE_STOP
       RESTORING: release request → restore settings (only if still ours) → delete record → IDLE
```

Every transition is written to the session log.

## Safety invariants

1. The original value of a setting is on disk **before** NIGHTSHIFT writes that setting.
2. Restore only writes a value back if the setting still holds what NIGHTSHIFT wrote — user or admin
   changes made during the session are preserved. The active power scheme is never switched.
3. Sleep and hibernate timeouts are never modified (sleep is blocked by a power request instead, which
   Windows cancels automatically if the process dies).
4. A pending recovery record blocks new sessions until it has been restored; it is never deleted
   unless the restore succeeded.
5. Restore is idempotent and also runs on console close / logoff / shutdown signals.
6. Processes are only observed (liveness). The PoC kills nothing except the heartbeat child it started itself.
7. No application content, terminal output or screenshots are read or stored.

## Deliberately deferred

Windows service + IPC (Phase 2), SQLite history (Phase 2), thermal/network monitoring (Phase 3),
process-tree profiles for Claude Code / Codex (Phase 4), tray UI (Phase 5), installer (Phase 6).
