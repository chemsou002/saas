# NIGHTSHIFT

**Keep working. Even when closed.**

NIGHTSHIFT lets selected long-running work (Claude Code, Codex, dev servers, scripts, downloads) keep
running on a Windows laptop while the lid is closed — by keeping the system in its normal working state,
managing the lid action and sleep safely, and always restoring the original settings.

> **Status: Phase 1 — technical proof of concept.** This repository currently contains a command-line PoC
> that proves (or disproves) closed-lid operation on a given laptop. The tray app, dashboard, Windows
> service and installer come in later phases. See [docs/FEASIBILITY.md](docs/FEASIBILITY.md).

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

## Quick start (Windows 10/11)

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
dotnet test           # 52 unit tests for the platform-neutral core
```

Layout and safety invariants: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
State is stored in `%LOCALAPPDATA%\Nightshift\` (`recovery.json`, `settings.json`, `logs\`).
