# NIGHTSHIFT

**Keep working. Even when closed.**

NIGHTSHIFT lets selected long-running work (Claude Code, Codex, dev servers, scripts, downloads) keep
running on a Windows laptop while the lid is closed — by keeping the system in its normal working state,
managing the lid action and sleep safely, and always restoring the original settings.

> **Status: Phase 2 — background session engine.** A Windows service now owns sessions (timed and unlimited,
> pause/resume, SQLite persistence, crash recovery) and is controlled over a secured local named pipe with
> `nightshiftctl`. The Phase 1 proof-of-concept CLI is still included. The tray app and dashboard (Phase 5),
> thermal/network monitoring (Phase 3) and the MSI installer (Phase 6) are not built yet.
> Nothing has been validated on physical laptop hardware yet — see [docs/TESTING.md](docs/TESTING.md).

## What Phase 1 does

| Capability | Command |
|---|---|
| Detect supported power states (S1–S4, Modern Standby, lid, thermal zone, battery, Group Policy) | `detect` |
| Back up and set lid-close action to *Do nothing* (AC + battery) | `session` |
| Block automatic sleep without changing any timeout (system-required power request) | `session` |
| Turn the display off while the system stays awake | `session` |
| Log lid close/open, display, AC/DC, battery and suspend events | `session` |
| Run a test application and prove it kept running with the lid closed | `session --launch-heartbeat`, `verify` |
| Restore original settings — also after crash, power loss or closed console | automatic, `restore` |
| Safe stop at critical battery on battery power (default 15 %) | `session` |

What it does **not** claim: execution during Sleep/Hibernate (impossible), universal hardware
compatibility, temperature readings (no reliable sensor API — Phase 3), or guaranteed Wi-Fi.

## Phase 2 — background service

### Install (elevated PowerShell, Windows 10/11)

```powershell
dotnet publish src/Nightshift.Service -c Release -r win-x64 --self-contained -o out/service
dotnet publish src/Nightshift.Ctl -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out/ctl
.\installer\install-service.ps1          # installs "NightshiftService" (auto start, restart on failure)
```
(or use the `nightshift-win-x64` CI artifact). Uninstall: `.\installer\uninstall-service.ps1 [-RemoveData]`.

### Use (normal, non-elevated terminal)

```powershell
$ctl = "$env:ProgramFiles\Nightshift\nightshiftctl.exe"
& $ctl start --duration 2h --pid (Get-Process node | Select-Object -First 1).Id   # shows the ventilation warning
& $ctl status          # elapsed / remaining / power source / battery / modified settings
& $ctl pause           # hands the laptop back to normal power behaviour, clock stops
& $ctl resume
& $ctl stop
& $ctl system          # capabilities, compatibility, service health
```
The session lives in the service: closing the terminal (or, later, the tray UI) does not end it.
Protocol reference: [docs/API.md](docs/API.md).

### Permissions

| Component | Runs as | Notes |
|---|---|---|
| `NightshiftService` | LocalSystem, token reduced to `SeChangeNotify`, `SeImpersonate`, `SeCreateGlobal` | needed because power schemes are machine-wide; never launches user programs |
| `nightshiftctl` / future UI | the logged-on user, no elevation | pipe ACL: interactive users read/write, NETWORK denied, users cannot create pipe instances |
| `%ProgramData%\Nightshift` | SYSTEM + Administrators only | refuses to start if the folder is owned by someone else |
| Installer | Administrator | one time |

Only the user who started a session (or an administrator) can stop, pause or resume it.
One session per machine: power settings are machine-wide.

### Recovery

Original values are committed to SQLite before every change. If the service dies, Windows drops its power request
immediately, the SCM restarts the service, and startup recovery restores only the settings NIGHTSHIFT still owns
(changes you made in the meantime are kept), marks the session `Interrupted` and records it. A failed restore is
retried every minute and blocks new sessions until it succeeds.

### Troubleshooting

| Symptom | Check |
|---|---|
| `The NIGHTSHIFT service is not running` | `sc.exe query NightshiftService`; Event Viewer → Windows Logs → Application, source `NightshiftService` |
| `ACCESS_DENIED` on start | power settings locked by OEM tooling/MDM; the change was rolled back |
| `UNSUPPORTED_SYSTEM` | lid action managed by Group Policy (`nightshiftctl system` lists blockers) |
| `RECOVERY_PENDING` | an earlier restore failed; it is retried every minute — see `nightshiftctl system` → lastError |
| Service won't start, log says data directory owned by another account | someone pre-created `%ProgramData%\Nightshift`; delete it as admin and start the service |
| `nightshift-poc session` refuses to run | the service has an active session; stop it first (they must not run together) |
| Is sleep really blocked? | elevated `powercfg /requests` → `[SERVICE] … Nightshift.Service.exe` under SYSTEM |

## Phase 1 — proof-of-concept CLI

### Quick start (Windows 10/11)

```powershell
# build (or download the CI artifact "nightshift-poc-win-x64")
dotnet publish src/Nightshift.Poc -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out

.\out\nightshift-poc.exe detect                                  # read-only report
.\out\nightshift-poc.exe session --duration 10m --launch-heartbeat
#   → confirm the ventilation warning, close the lid ≥ 5 min, reopen; the tool prints RESULT PASS/FAIL
.\out\nightshift-poc.exe detect                                  # confirm settings are back to original
```

Watch a real application (monitoring only — it is never controlled or killed):

```powershell
.\out\nightshift-poc.exe session --duration 2h --watch-pid (Get-Process node | Select-Object -First 1).Id
```

Full hardware procedure and the compatibility report template: [docs/TESTING.md](docs/TESTING.md).

## Safety

Every session shows this warning, and the first session requires explicit confirmation:

> IMPORTANT: Your laptop will continue operating while its lid is closed. Place your device on a hard,
> flat, well-ventilated surface. Never place it on a bed, pillow, blanket, carpet, or any surface that
> blocks its ventilation openings. Avoid enclosed spaces. Excessive heat may damage your device.

NIGHTSHIFT does not guarantee protection against overheating. All data stays local; nothing is uploaded.

## Development

```bash
dotnet build          # all projects (Windows layer compiles on any OS)
dotnet test           # 127 tests (7 real-Windows integration tests skip on other OSes)
```

Layout and safety invariants: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
Phase 1 CLI state: `%LOCALAPPDATA%\Nightshift\` (`recovery.json`, `settings.json`, `logs\`).
Service state: `%ProgramData%\Nightshift\nightshift.db`.
