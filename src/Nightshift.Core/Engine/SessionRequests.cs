using Nightshift.Core.Compatibility;
using Nightshift.Core.Power;
using Nightshift.Core.Session;

namespace Nightshift.Core.Engine;

public sealed record StartSessionRequest
{
    public required ClientIdentity Caller { get; init; }
    public required SessionMode Mode { get; init; }
    public TimeSpan? Duration { get; init; }
    public IReadOnlyList<SelectedApplication> Applications { get; init; } = [];
    public PowerPolicy PowerPolicy { get; init; } = PowerPolicy.Automatic;
    public int BatteryWarningPercent { get; init; } = 25;
    public int BatteryCriticalPercent { get; init; } = 15;
    public bool ConfigureLid { get; init; } = true;
    public bool TurnOffDisplay { get; init; } = true;

    /// <summary>The user confirmed the ventilation warning for this session. Required.</summary>
    public bool VentilationAcknowledged { get; init; }
}

public sealed record ApplicationStatus(SelectedApplication Application, bool? Running);

public sealed record SessionStatusSnapshot
{
    public bool HasSession => SessionId is not null;
    public Guid? SessionId { get; init; }
    public SessionStatus? Status { get; init; }

    /// <summary>Fine-grained state (Running, LidClosed, Warning, Paused, …).</summary>
    public SessionState State { get; init; } = SessionState.Idle;
    public SessionMode? Mode { get; init; }
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public TimeSpan? Duration { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan? Remaining { get; init; }
    public PowerPolicy? PowerPolicy { get; init; }
    public PowerSource? PowerSource { get; init; }
    public int? BatteryLevelAtStart { get; init; }
    public int? BatteryLevelCurrent { get; init; }
    public bool KeepAwakeHeld { get; init; }
    public bool LidClosed { get; init; }
    public int SuspendCount { get; init; }
    public IReadOnlyList<ApplicationStatus> Applications { get; init; } = [];
    public IReadOnlyList<PowerSettingValue> OriginalPowerSettings { get; init; } = [];
    public IReadOnlyList<PowerSettingValue> ModifiedPowerSettings { get; init; } = [];
    public DateTimeOffset? LastHeartbeat { get; init; }
    public TerminationReason? TerminationReason { get; init; }
    public string? OwnerName { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static readonly SessionStatusSnapshot None = new();
}

public sealed record SystemStatusSnapshot
{
    public required SystemCapabilities Capabilities { get; init; }
    public required PowerStatus Power { get; init; }
    public required CompatibilityReport Compatibility { get; init; }
    public required ServiceHealthSnapshot Service { get; init; }
}

public sealed record ServiceHealthSnapshot
{
    public required Guid InstanceId { get; init; }
    public required string Version { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public TimeSpan Uptime { get; init; }
    public DateTimeOffset? LastTick { get; init; }
    public bool Healthy { get; init; }
    public int ConsecutiveTickFailures { get; init; }
    public bool RecoveryPending { get; init; }
    public bool? PreviousShutdownClean { get; init; }
    public string? LastError { get; init; }
}

/// <summary>Engine error with a stable code that the IPC layer forwards to clients.</summary>
public sealed class SessionEngineException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class EngineErrorCodes
{
    public const string SessionAlreadyActive = "SESSION_ALREADY_ACTIVE";
    public const string NoActiveSession = "NO_ACTIVE_SESSION";
    public const string InvalidState = "INVALID_STATE";
    public const string NotAuthorized = "NOT_AUTHORIZED";
    public const string VentilationAckRequired = "VENTILATION_ACK_REQUIRED";
    public const string BatteryTooLow = "BATTERY_TOO_LOW";
    public const string AcPowerRequired = "AC_POWER_REQUIRED";
    public const string UnsupportedSystem = "UNSUPPORTED_SYSTEM";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string RecoveryPending = "RECOVERY_PENDING";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string InternalError = "INTERNAL_ERROR";
}

public sealed record EngineOptions
{
    /// <summary>How often LastHeartbeat and the current battery level are persisted.</summary>
    public TimeSpan HeartbeatPersistInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan BatteryPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>AcOnly policy: how long the charger may be unplugged before a safe stop.</summary>
    public TimeSpan AcLossGracePeriod { get; init; } = TimeSpan.FromSeconds(60);
    public uint DisplayIdleTimeoutSeconds { get; init; } = 60;

    /// <summary>After this many consecutive monitoring failures the session is stopped and settings restored.</summary>
    public int MaxConsecutiveTickFailures { get; init; } = 5;
    public TimeSpan PendingRestoreRetryInterval { get; init; } = TimeSpan.FromMinutes(1);
}
