using Nightshift.Core.Power;

namespace Nightshift.Core.Recovery;

/// <summary>
/// One setting NIGHTSHIFT intends to change (or has changed), with its original value.
/// Persisted BEFORE the write happens so a crash at any point can be undone.
/// </summary>
public sealed record TrackedChange
{
    public required Guid Scheme { get; init; }
    public required Guid Subgroup { get; init; }
    public required Guid Setting { get; init; }
    public required string Name { get; init; }
    public required PowerSource Source { get; init; }
    public required uint OriginalValue { get; init; }
    public required uint AppliedValue { get; init; }

    /// <summary>False while the write is pending; the write may still have happened if we crashed.</summary>
    public bool WriteConfirmed { get; init; }

    public PowerSettingKey Key => new(Subgroup, Setting, Name);
}

/// <summary>Durable record of an active session's system modifications.</summary>
public sealed record RecoveryRecord
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required Guid SessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required int OwnerProcessId { get; init; }
    public string? EventLogPath { get; init; }
    public List<TrackedChange> Changes { get; init; } = [];
}
