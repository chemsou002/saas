namespace Nightshift.Core.Session;

/// <summary>Session states (Phase 1 uses the subset needed by the proof of concept; all transitions are logged).</summary>
public enum SessionState
{
    Idle,
    Preflight,
    Running,
    LidClosed,
    LidOpened,
    Warning,
    Paused,
    SafeStop,
    Completed,
    Restoring,
    Error,
}

public enum StopReason
{
    DurationElapsed,
    UserStopped,
    BatteryCritical,
    ConsoleClosed,
    StartupFailed,
}
