# NIGHTSHIFT — Technical Feasibility Report (Phase 1)

_Date: 2026-11-24 · Scope: closed-lid work sessions on Windows 10/11 laptops_

## 1. Development environment that was inspected

| Item | Finding |
|---|---|
| Build host | Linux container (Ubuntu 24.04), **no Windows and no laptop hardware** |
| Toolchain | .NET SDK 10.0.115 (installed from Ubuntu packages), Node 22, Rust/Cargo available |
| Windows build | `net10.0` projects cross-compile; `dotnet publish -r win-x64` produces a self-contained `nightshift-poc.exe` (PE32+ console) |
| Tests | 52 unit tests for the platform-neutral core run and pass on Linux |
| What could **not** be done here | Executing any Win32 power API, closing a lid, measuring heat, testing Wi-Fi drivers |

**Consequence:** the Phase 1 code is complete and compiles for Windows, but every hardware statement in
this report is based on Microsoft's documented API behaviour, *not* on a test. The PoC exists so that
those statements can be proven or disproven on real laptops (see [TESTING.md](TESTING.md)).
A CI workflow runs the real Win32 calls on a GitHub-hosted Windows VM (no lid, no battery) as a first check.

## 2. Is the core idea feasible?

Yes, with clearly bounded guarantees.

| Requirement | Mechanism | Feasible? | Notes / risks |
|---|---|---|---|
| Detect sleep capabilities | `CallNtPowerInformation(SystemPowerCapabilities)` → S1–S4, hiberfile, `AoAc` (Modern Standby), lid present, thermal zone, battery | **Yes** | Read-only, no elevation. Cross-checked against `powercfg /a`. |
| Read/modify lid-close action | `PowerRead/WriteACValueIndex`, `…DCValueIndex` on `GUID_LIDCLOSE_ACTION`, then `PowerSetActiveScheme` | **Yes** | Same API as `powercfg`/Control Panel. Some systems return *access denied* without elevation → PoC reports it and rolls back. Group Policy can pin the value → detected and treated as a blocker. |
| Prevent automatic sleep | `PowerCreateRequest` + `PowerSetRequest(PowerRequestSystemRequired)` | **Yes** | Does not change any timeout. Released automatically by Windows if the process dies, so a crash cannot leave the PC awake forever. Visible in `powercfg /requests`. |
| Let the display turn off | S3 systems: `SC_MONITORPOWER` to our own hidden window. Modern Standby: temporary, tracked display-idle timeout | **Yes** | `SC_MONITORPOWER` can push some Modern Standby devices into standby, so it is not used there by default. Closing the lid normally switches the internal panel off anyway. |
| Detect lid close/open | `RegisterPowerSettingNotification(GUID_LIDSWITCH_STATE_CHANGE)` | **Yes** | Also logs display state, AC/DC, battery %, suspend/resume. |
| App keeps running with lid closed | Lid = *Do nothing* + system-required request | **Likely on most devices — must be proven per device** | This is the central unknown. Proven objectively by the heartbeat test (gap analysis). |
| Restore original settings | Durable undo log written *before* each change; restore only values still equal to what NIGHTSHIFT wrote | **Yes** | Crash/power-loss recovery on next run (`restore` or automatic before the next session). |

## 3. Hardware realities (must be communicated to users)

1. **Sleep and Hibernate stop all user programs.** No software can run Claude Code, Codex or any desktop
   app *inside* S3/S4. NIGHTSHIFT works by keeping the machine in the normal working state (S0) with the
   lid closed — it never claims execution during sleep.
2. **Modern Standby (S0 Low Power Idle) devices** — most laptops sold since ~2019. With lid action
   *Do nothing* the system should stay active, but if standby is entered for another reason (power
   button, manufacturer utility, policy), the Desktop Activity Moderator pauses desktop apps and the
   network may drop. These devices get a warning and must pass the heartbeat test.
3. **Firmware / OEM utilities** (e.g. vendor power managers) can override Windows settings.
4. **Thermals:** many laptops vent through the keyboard area; a closed lid reduces airflow. Windows
   reports only whether ACPI thermal control exists — it does not give reliable temperatures through a
   documented API on most consumer laptops. Phase 1 therefore implements **no temperature readings** and
   shows the mandatory ventilation warning; Phase 3 adds monitoring where sensors are genuinely available.
5. **Wi-Fi drivers** may power down the adapter independently; connectivity cannot be guaranteed.
6. **Group Policy / MDM** may lock power settings — NIGHTSHIFT detects the lid policy and will not override it.

## 4. Privileges

| Operation | Elevation needed? |
|---|---|
| Capability detection, power status, notifications | No |
| System-required power request | No |
| Changing the lid action on the user's active scheme | Usually no; some OEM/managed images require it. Handled. |
| `powercfg /requests` (manual verification only) | Yes |

Phase 2 will move session management into a Windows service so the tray UI never needs elevation.

## 5. Stack decision for the full product

* **C# / .NET 10** for the session engine and Windows service — confirmed workable; all required APIs are
  reachable via source-generated P/Invoke (`LibraryImport`) with no third-party dependencies.
* **Tauri + React + TypeScript** for the tray/dashboard (Phase 5) talking to the service over a named pipe
  with an ACL restricted to the interactive user.
* **SQLite** for history (Phase 2+). Phase 1 uses a JSON recovery record and JSON-lines logs.

## 6. Go / no-go for Phase 2

Proceed to Phase 2 **only after** the PoC reports `RESULT PASS` on at least one S3 laptop and one Modern
Standby laptop (procedure in [TESTING.md](TESTING.md)), and the restore check confirms settings were
returned to their original values. Record results in the compatibility table in TESTING.md.
