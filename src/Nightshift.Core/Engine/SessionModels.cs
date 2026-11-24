using Nightshift.Core.Power;
using Nightshift.Core.Recovery;

namespace Nightshift.Core.Engine;

public enum SessionMode
{
    Timed,
    Unlimited,
}

/// <summary>Persistent lifecycle status of a session.</summary>
public enum SessionStatus
{
    Starting,
    Running,
    Paused,
    Stopping,

    /// <summary>Timed session reached its duration.</summary>
    Completed,

    /// <summary>Stopped by the user or because the service shut down cleanly.</summary>
    Stopped,

    /// <summary>Stopped by a safety condition (battery, AC loss, monitoring failure).</summary>
    SafeStopped,

    /// <summary>Could not start; nothing remains applied.</summary>
    Failed,

    /// <summary>The service terminated unexpectedly; settings were recovered on the next start.</summary>
    Interrupted,
}

public static class SessionStatusExtensions
{
    public static bool IsActive(this SessionStatus s) =>
        s is SessionStatus.Starting or SessionStatus.Running or SessionStatus.Paused or SessionStatus.Stopping;
}

public enum TerminationReason
{
    DurationElapsed,
    UserStopped,
    BatteryCritical,
    AcPowerLost,
    MonitoringFailure,
    ServiceStopping,
    ServiceTerminatedUnexpectedly,
    StartupFailed,
}

/// <summary>What to do with the power source.</summary>
public enum PowerPolicy
{
    /// <summary>Run on AC or battery; battery thresholds apply on battery.</summary>
    Automatic,

    /// <summary>Requires the charger; stops safely if it stays unplugged longer than the grace period.</summary>
    AcOnly,
}

/// <summary>An application the user selected. Phase 2 records it and watches its PID (if given) — it never launches or controls it.</summary>
public sealed record SelectedApplication(string Name, string? ExecutablePath = null, int? ProcessId = null);

public sealed record PowerSettingValue(string Name, PowerSource Source, uint Value);

/// <summary>Identity of the local caller, established by the transport (never by the request body).</summary>
public sealed record ClientIdentity(string UserSid, string UserName, bool IsAdministrator)
{
    public static readonly ClientIdentity Service = new("S-1-5-18", "NT AUTHORITY\\SYSTEM", true);
}

/// <summary>The persistent session model (one row in the sessions table). All times are UTC.</summary>
public sealed record SessionRecord
{
    public required Guid SessionId { get; init; }
    public required string OwnerSid { get; init; }
    public required string OwnerName { get; init; }
    public required IReadOnlyList<SelectedApplication> SelectedApplications { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public required SessionMode SessionMode { get; init; }

    /// <summary>Null for unlimited sessions.</summary>
    public TimeSpan? Duration { get; init; }
    public required SessionStatus SessionStatus { get; init; }
    public required PowerPolicy PowerPolicy { get; init; }
    public required PowerSource PowerSource { get; init; }
    public int? BatteryLevelAtStart { get; init; }
    public int? BatteryLevelCurrent { get; init; }
    public IReadOnlyList<PowerSettingValue> OriginalPowerSettings { get; init; } = [];
    public IReadOnlyList<PowerSettingValue> ModifiedPowerSettings { get; init; } = [];
    public DateTimeOffset? LastHeartbeat { get; init; }
    public TerminationReason? TerminationReason { get; init; }
    public TimeSpan PausedTotal { get; init; }
    public DateTimeOffset? PausedAt { get; init; }
    public required int BatteryWarningPercent { get; init; }
    public required int BatteryCriticalPercent { get; init; }
    public bool ConfigureLid { get; init; } = true;
    public bool TurnOffDisplay { get; init; } = true;
    public string? Error { get; init; }

    public static (IReadOnlyList<PowerSettingValue> Original, IReadOnlyList<PowerSettingValue> Modified) FromChanges(IEnumerable<TrackedChange> changes)
    {
        var list = changes.ToList();
        return ([.. list.Select(c => new PowerSettingValue(c.Name, c.Source, c.OriginalValue))],
                [.. list.Select(c => new PowerSettingValue(c.Name, c.Source, c.AppliedValue))]);
    }
}

public sealed record SessionEventRecord(long Id, Guid? SessionId, DateTimeOffset Time, string Type, string Message, string? State);

public sealed record ServiceInstanceRecord(Guid InstanceId, DateTimeOffset Started, DateTimeOffset LastHeartbeat, DateTimeOffset? Stopped, bool CleanShutdown, string? Version);

public sealed class DuplicateActiveSessionException() : Exception("Another session is already active.");

/// <summary>Durable session storage. Implementations must make every write durable before returning.</summary>
public interface ISessionRepository
{
    /// <exception cref="DuplicateActiveSessionException">An active session already exists.</exception>
    void Insert(SessionRecord session);

    void Update(SessionRecord session);

    SessionRecord? Get(Guid sessionId);

    IReadOnlyList<SessionRecord> GetActive();

    IReadOnlyList<SessionRecord> ListRecent(int count);

    void AddEvent(Guid? sessionId, string type, string message, string? state = null);

    IReadOnlyList<SessionEventRecord> GetEvents(Guid? sessionId, int max = 500);

    /// <summary>Registers a service start and returns the previous instance (if any).</summary>
    ServiceInstanceRecord? RecordServiceStart(Guid instanceId, DateTimeOffset now, string? version);

    void RecordServiceHeartbeat(Guid instanceId, DateTimeOffset now);

    void RecordServiceStop(Guid instanceId, DateTimeOffset now);
}
