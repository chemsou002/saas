# Phase 1 — Hardware Test Procedure & Compatibility Report

Automated tests (`dotnet test`) cover the logic: backup-before-write, rollback on failure, crash recovery,
not overwriting user changes, battery safe-stop, state logging and heartbeat analysis.
**They cannot prove anything about real hardware.** The procedure below must be run on each physical laptop.

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

## Compatibility report (fill in one row per device)

| Device / model | Windows build | Sleep model (S3 / Modern Standby) | Thermal zone | T2 result | Longest gap | T3 restore | T4 recovery | Notes |
|---|---|---|---|---|---|---|---|---|
| _no device tested yet_ | | | | | | | | Phase 1 code has not yet been run on physical hardware. |

Do not mark a device or a sleep model as supported until its row shows PASS for T2, T3 and T4.
