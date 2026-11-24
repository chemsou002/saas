# NIGHTSHIFT Service API (IPC protocol v1)

The background service exposes a **local named pipe**: `\\.\pipe\NightshiftService.v1`.
There is no TCP/HTTP endpoint. Remote (SMB) clients are rejected by the pipe itself
(`PIPE_REJECT_REMOTE_CLIENTS`) and by its ACL (NETWORK is denied).

The .NET client is `Nightshift.Ipc.NightshiftClient` (used by `nightshiftctl` now and by the tray UI in Phase 5).

## Transport

| Item | Value |
|---|---|
| Framing | 4-byte little-endian length, then UTF-8 JSON. 1 ≤ length ≤ 65 536 bytes |
| Connection | Several requests per connection allowed; the server drops a connection that does not deliver a complete request within 30 s |
| Concurrency | 4 simultaneous connections |
| Client impersonation level | `Identification` (the service can read who you are, but can never act as you) |
| Caller identity | Taken from the pipe client token (SID, name, Administrators membership). **Never** from the request body |

### Who may connect (pipe ACL)

| Principal | Rights |
|---|---|
| NETWORK | **Denied** |
| SYSTEM, Administrators, the service account | Full control |
| INTERACTIVE (logged-on users) | Read/Write — cannot create pipe instances |
| Everyone else | No access |

## Envelope

Request:
```json
{ "version": 1, "requestId": "c0ffee-01", "command": "START_SESSION", "payload": { } }
```
* `version` must be `1`. `requestId`: 1–64 chars `[A-Za-z0-9_-]`, echoed back.
* Unknown top-level fields are rejected. Commands other than `START_SESSION` accept no payload (absent, `null` or `{}`).

Response:
```json
{ "requestId": "c0ffee-01", "ok": true,  "result": { } }
{ "requestId": "c0ffee-01", "ok": false, "error": { "code": "SESSION_ALREADY_ACTIVE", "message": "…" } }
```
Enums are strings; timestamps are ISO-8601 UTC; durations are `TimeSpan` strings (`"01:30:00"`).

## Commands

### START_SESSION
Payload (unknown fields are rejected):

| Field | Type | Default | Rules |
|---|---|---|---|
| `mode` | `"timed"` \| `"unlimited"` | — | required |
| `durationSeconds` | number | — | required for timed, 1–604 800; must be absent for unlimited |
| `applications` | array | `[]` | ≤ 16 items: `{ "name": 1–128 printable chars, "executablePath"?: local fully-qualified path ≤ 1024, "processId"?: > 4 }` |
| `powerPolicy` | `"automatic"` \| `"ac-only"` | automatic | ac-only stops safely when unplugged longer than the grace period (60 s) |
| `batteryWarningPercent` | int | 25 | > critical, ≤ 95 |
| `batteryCriticalPercent` | int | 15 | 5–50; on battery the session stops safely at or below it |
| `configureLid` | bool | true | set lid-close action to "Do nothing" (AC + battery) for the session |
| `turnOffDisplay` | bool | true | temporarily set "Turn off display after" to 60 s (tracked & restored) |
| `ventilationAcknowledged` | bool | false | **must be true** — the client must have shown the ventilation warning |

Applications are **recorded and monitored only** (liveness by PID). The service never launches, injects into,
suspends or kills them — it runs as LocalSystem and must not execute user-supplied paths.

Result: `SessionStatusSnapshot` (below).

### STOP_SESSION · PAUSE_SESSION · RESUME_SESSION
No payload. Allowed for the user who started the session or an administrator. Result: `SessionStatusSnapshot`.

* **Pause** releases the power request and restores every modified setting (the laptop behaves normally); the
  session clock stops.
* **Resume** runs the preflight again (battery, AC policy, compatibility) and re-applies the configuration.
* **Stop** releases the power request, restores settings and ends the session (`Stopped`/`UserStopped`).

### GET_SESSION_STATUS
Result: `{ "active": SessionStatusSnapshot, "last": SessionStatusSnapshot | null }` —
`active.sessionId` is absent when no session is active; `last` is the most recent ended session.

### GET_SYSTEM_STATUS
Result:
```json
{
  "capabilities": { "lidPresent": true, "modernStandby": true, "sleepS3": false, "thermalControl": true, "batteryPresent": true, "osBuild": 22631, "...": "…" },
  "power": { "onAcPower": true, "batteryPercent": 81, "batteryPresent": true, "batterySaverOn": false },
  "compatibility": { "verdict": "ConfigurableWithWarnings", "findings": [ { "severity": "Warning", "code": "modern-standby", "message": "…" } ] },
  "service": { "instanceId": "…", "version": "0.1.0-poc", "startedAt": "…", "uptime": "00:12:03", "lastTick": "…",
               "healthy": true, "consecutiveTickFailures": 0, "recoveryPending": false, "previousShutdownClean": true, "lastError": null }
}
```
`healthy` = the monitoring loop ran within the last few seconds and is not failing.

## SessionStatusSnapshot

| Field | Meaning |
|---|---|
| `sessionId`, `status` | `Starting, Running, Paused, Stopping, Completed, Stopped, SafeStopped, Failed, Interrupted` |
| `state` | fine-grained: `Preflight, Running, LidClosed, Warning, Paused, Restoring, Idle` |
| `mode`, `duration`, `elapsed`, `remaining` | elapsed/remaining exclude paused time; `remaining` is null for unlimited |
| `startTime`, `endTime`, `lastHeartbeat` | UTC |
| `powerPolicy`, `powerSource`, `batteryLevelAtStart`, `batteryLevelCurrent` | |
| `keepAwakeHeld`, `lidClosed`, `suspendCount` | live values |
| `applications[]` | `{ application, running: true/false/null (no PID) }` |
| `originalPowerSettings[]`, `modifiedPowerSettings[]` | `{ name, source: AC/DC, value }` |
| `terminationReason` | `DurationElapsed, UserStopped, BatteryCritical, AcPowerLost, MonitoringFailure, ServiceStopping, ServiceTerminatedUnexpectedly, StartupFailed` |
| `ownerName`, `warnings[]` | |

## Error codes

| Code | When |
|---|---|
| `MALFORMED_REQUEST` | not JSON, unknown fields, bad requestId, truncated frame |
| `FRAME_TOO_LARGE` | length outside 1..65 536 (connection is closed) |
| `UNSUPPORTED_VERSION`, `UNKNOWN_COMMAND` | |
| `UNAUTHENTICATED` | caller token could not be read (e.g. anonymous) |
| `INVALID_REQUEST` | payload failed validation |
| `VENTILATION_ACK_REQUIRED` | `ventilationAcknowledged` was not true |
| `SESSION_ALREADY_ACTIVE` | one session per machine (power settings are machine-wide) |
| `NO_ACTIVE_SESSION`, `INVALID_STATE` | e.g. resume while running |
| `NOT_AUTHORIZED` | another non-admin user tried to control the session |
| `BATTERY_TOO_LOW`, `AC_POWER_REQUIRED` | preflight refused |
| `UNSUPPORTED_SYSTEM` | blocker such as a Group-Policy-managed lid action |
| `ACCESS_DENIED` | Windows refused a power-setting change (rolled back) |
| `RECOVERY_PENDING` | an earlier restore has not completed; retried automatically every minute |
| `INTERNAL_ERROR` | unexpected failure; details only in the service log |

## Example (PowerShell, via the CLI)

```powershell
nightshiftctl start --duration 2h --pid (Get-Process claude).Id --ack-ventilation
nightshiftctl status
nightshiftctl pause ; nightshiftctl resume ; nightshiftctl stop
nightshiftctl system --json
```
