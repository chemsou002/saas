# Testing

## Automated tests

```bash
dotnet test        # 127 tests; the 7 Windows integration tests are skipped on non-Windows hosts
```

| Project | Count | What it covers | How |
|---|---|---|---|
| `Nightshift.Core.Tests` | 52 | Phase 1: backup-before-write, rollback, recovery, user-change protection, battery stop, heartbeat analysis | fakes |
| `Nightshift.Engine.Tests` | 68 | Phase 2: timed/unlimited sessions, stop, pause/resume, persistence of every model field, duplicate prevention (in memory **and** by the SQLite unique index), authorization, AC→battery / battery→AC, AC-only grace, suspend/resume, service restart, unexpected termination, restore conflicts, pending-restore retry, monitoring-failure safe stop, IPC round trip, IPC validation and failure cases (malformed/oversized frames, abrupt disconnects, silent clients, missing service, unauthenticated callers, error-detail leakage) | fake power layer + `FakeTimeProvider`, **real** SQLite files, **real** named pipes, real `SessionWorker` |
| `Nightshift.Windows.IntegrationTests` | 7 | real PowrProf read/write/restore through SQLite recovery, power request, window-less notifications, secured pipe + real caller identity, pipe-squatting detection, pipe ACL | real Windows APIs (CI `windows-latest`) |

The CI Windows job additionally installs the real service, drives it with `nightshiftctl`
(start → `powercfg /requests` check → pause → resume → stop, verifying settings after each), kills the service
process, waits for the SCM restart and verifies recovery (`Interrupted`, settings identical, unclean shutdown
detected), then uninstalls.

**What automated tests do not prove:** that a given laptop keeps running with the lid closed, that recovery
works after a real power loss, or anything about heat. A GitHub VM has no lid, battery or Modern Standby.
Hardware-level recovery has **not** been validated by these tests.

# Phase 1 — Hardware Test Procedure & Compatibility Report

The procedure below must be run on each physical laptop.

## Prerequisites

* Windows 10 or 11 laptop, charger connected for the first run.
* Hard, flat, ventilated surface. Stay near the device during the test.
* `nightshift-poc.exe` (from CI artifacts or `dotnet publish src/Nightshift.Poc -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`).

## T1 — Detection (read-only)

```powershell
.\nightshift-poc.exe detect
```
Expected: capabilities match `powercfg /a`; current lid action, sleep, hibernate and display timeouts are
shown; a compatibility verdict is printed. Save the output (`detect --json > detect.json`).

## T2 — Closed-lid run with test application (the core proof)

```powershell
.\nightshift-poc.exe session --duration 10m --launch-heartbeat
```
1. Confirm the ventilation warning (first run only).
2. Within ~15 s the display turns off (S3 devices) or after 60 s idle (Modern Standby).
3. **Close the lid for at least 5 minutes.** Do not touch the device.
4. Open the lid. Wait for the session to end (or press Ctrl+C after reopening).
5. The tool prints a verification block. `RESULT PASS` means the heartbeat never paused > 5 s while the
   lid was closed, no suspend was reported and all settings were restored.

Optional while the session runs (elevated terminal): `powercfg /requests` must list NIGHTSHIFT under SYSTEM.

## T3 — Restoration check

```powershell
.\nightshift-poc.exe detect   # compare lid action + display timeout with the T1 output: must be identical
.\nightshift-poc.exe status   # must say "No active or unfinished session."
```

## T4 — Crash recovery

1. Start `session --duration 30m`, then kill it: `taskkill /F /IM nightshift-poc.exe`.
2. `detect` now shows lid action *DoNothing* (expected — the process was killed hard).
3. `status` shows the pending record. Run `restore` → original values return. Run `detect` to confirm.
4. Repeat, but instead of `restore` start a new `session`: it must restore the old record first.
5. Power-loss variant: start a session, hold the power button to force off, boot, run `restore`.

## T5 — Console close

Start a session and close the terminal window with the X button. Run `detect`: values must be original
(the close handler restores synchronously). If not, `status` must show a record that `restore` fixes.

## T6 — Battery

Unplug the charger, run `session --duration unlimited --battery-critical 95 --battery-warning 97` on a
device above 97 %, and let it drain: expect a WARNING state, then SAFE_STOP and restore once the battery
reaches the threshold. Plug/unplug events appear in the log as power-source changes.

## T7 — Access denied path

On a managed/OEM image where a non-elevated write is refused: the session must report the need for an
elevated terminal and `detect` must show unchanged values.

# Phase 2 — Service tests on real hardware (manual)

Build and install (elevated PowerShell):
```powershell
dotnet publish src/Nightshift.Service -c Release -r win-x64 --self-contained -o out/service
dotnet publish src/Nightshift.Ctl -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out/ctl
.\installer\install-service.ps1
$ctl = "$env:ProgramFiles\Nightshift\nightshiftctl.exe"
```
Then, from a **normal (non-elevated)** terminal:

| # | Test | Expected |
|---|---|---|
| S1 | `& $ctl system` | healthy, capabilities match `nightshift-poc detect` |
| S2 | `& $ctl start --duration 10m --ack-ventilation`, **close the terminal**, close the lid 5 min, reopen, `& $ctl status` | session kept running without any client; lid events in history; completes and restores at 10 min |
| S3 | `start --duration unlimited`, pause, wait, resume, stop | settings original while paused and after stop; elapsed excludes pause |
| S4 | Unplug the charger during a session; plug it back | `powerSource` follows; on `--ac-only` the session stops after 60 s unplugged |
| S5 | Battery: `start --duration unlimited --battery-warning 95 --battery-critical 94` unplugged | Warning state, then SafeStopped + restore |
| S6 | `taskkill /F /IM Nightshift.Service.exe` during a session | SCM restarts within ~5 s; last session `Interrupted`; settings original |
| S7 | During a session, change the lid action in Control Panel, then kill the service | after restart your change is kept (not overwritten) |
| S8 | Hard power-off during a session (hold power button), boot | after boot, service recovers: `Interrupted`, settings original |
| S9 | Sleep via Start menu during a session, wake | suspend + resume recorded; expired timed session ends after resume |
| S10 | Second user (fast user switching) runs `& $ctl stop` | `NOT_AUTHORIZED` (unless administrator) |
| S11 | `.\installer\uninstall-service.ps1` during a session | settings restored, service removed |

Record results in the table below (add a "Service" column note).

## Compatibility report (fill in one row per device)

| Device / model | Windows build | Sleep model (S3 / Modern Standby) | Thermal zone | T2 result | Longest gap | T3 restore | T4 recovery | Notes |
|---|---|---|---|---|---|---|---|---|
| _no device tested yet_ | | | | | | | | Phase 1 code has not yet been run on physical hardware. |

Do not mark a device or a sleep model as supported until its row shows PASS for T2, T3 and T4.
