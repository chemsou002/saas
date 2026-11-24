using Nightshift.Core.Recovery;

namespace Nightshift.Core.Power;

/// <summary>A change NIGHTSHIFT wants to make to the active power scheme.</summary>
public sealed record PlannedChange(PowerSettingKey Key, PowerSource Source, uint Value);

public enum RestoreOutcome
{
    /// <summary>The original value was written back.</summary>
    Restored,

    /// <summary>The setting already holds its original value (e.g. the write never happened).</summary>
    AlreadyOriginal,

    /// <summary>Someone else changed the setting during the session; NIGHTSHIFT leaves it alone.</summary>
    SkippedModifiedByOthers,

    /// <summary>The setting no longer exists in the scheme.</summary>
    SkippedMissing,

    Failed,
}

public sealed record RestoreItem(TrackedChange Change, RestoreOutcome Outcome, uint? ValueFound, string? Error = null);

public sealed record RestoreReport(IReadOnlyList<RestoreItem> Items)
{
    public bool Success => Items.All(i => i.Outcome != RestoreOutcome.Failed);
}

/// <summary>
/// Applies power-scheme changes with a durable undo log, and restores them.
/// Invariant: the original value of a setting is on disk before that setting is written.
/// </summary>
public sealed class PowerConfigurator(IPowerSettingsStore store, IRecoveryStore recovery)
{
    /// <summary>
    /// Applies <paramref name="plan"/> to the active scheme. On failure every change already made is
    /// rolled back before the exception propagates.
    /// </summary>
    public RecoveryRecord Apply(RecoveryRecord record, IEnumerable<PlannedChange> plan)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(plan);
        var scheme = store.GetActiveSchemeId();
        var current = record;
        recovery.Save(current);

        try
        {
            foreach (var change in plan)
            {
                var original = store.Read(scheme, change.Key, change.Source);
                if (original is null)
                {
                    // Setting not present in this scheme (e.g. no lid). Nothing to change or restore.
                    continue;
                }
                if (original.Value == change.Value)
                {
                    continue; // Already what we need; never touch it, never "restore" it.
                }

                var tracked = new TrackedChange
                {
                    Scheme = scheme,
                    Subgroup = change.Key.Subgroup,
                    Setting = change.Key.Setting,
                    Name = change.Key.Name,
                    Source = change.Source,
                    OriginalValue = original.Value,
                    AppliedValue = change.Value,
                    WriteConfirmed = false,
                };
                current = current with { Changes = [.. current.Changes, tracked] };
                recovery.Save(current); // durable BEFORE the write

                store.Write(scheme, change.Key, change.Source, change.Value);

                current = current with
                {
                    Changes = [.. current.Changes.Take(current.Changes.Count - 1), tracked with { WriteConfirmed = true }],
                };
                recovery.Save(current);
            }

            if (current.Changes.Count > 0)
            {
                store.Activate(scheme);
            }
            return current;
        }
        catch
        {
            Restore(current);
            throw;
        }
    }

    /// <summary>
    /// Restores every tracked change, newest first. Only settings that still hold the value NIGHTSHIFT wrote
    /// are reverted, so changes made by the user or an administrator during the session are preserved.
    /// The recovery record is deleted only when every item was handled without failure.
    /// </summary>
    public RestoreReport Restore(RecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var items = new List<RestoreItem>();
        var touchedSchemes = new HashSet<Guid>();

        foreach (var change in Enumerable.Reverse(record.Changes))
        {
            try
            {
                var now = store.Read(change.Scheme, change.Key, change.Source);
                if (now is null)
                {
                    items.Add(new RestoreItem(change, RestoreOutcome.SkippedMissing, null));
                }
                else if (now.Value == change.OriginalValue)
                {
                    items.Add(new RestoreItem(change, RestoreOutcome.AlreadyOriginal, now));
                }
                else if (now.Value == change.AppliedValue)
                {
                    store.Write(change.Scheme, change.Key, change.Source, change.OriginalValue);
                    touchedSchemes.Add(change.Scheme);
                    items.Add(new RestoreItem(change, RestoreOutcome.Restored, now));
                }
                else
                {
                    items.Add(new RestoreItem(change, RestoreOutcome.SkippedModifiedByOthers, now));
                }
            }
            catch (Exception ex)
            {
                items.Add(new RestoreItem(change, RestoreOutcome.Failed, null, ex.Message));
            }
        }

        // Re-apply only if the modified scheme is the one in use; never switch the user's active scheme.
        try
        {
            var active = store.GetActiveSchemeId();
            if (touchedSchemes.Contains(active))
            {
                store.Activate(active);
            }
        }
        catch (Exception ex)
        {
            items.Add(new RestoreItem(record.Changes.LastOrDefault() ?? Placeholder, RestoreOutcome.Failed, null,
                $"Could not re-apply the active scheme: {ex.Message}"));
        }

        var report = new RestoreReport(items);
        if (report.Success)
        {
            recovery.Delete();
        }
        return report;
    }

    private static readonly TrackedChange Placeholder = new()
    {
        Scheme = Guid.Empty,
        Subgroup = Guid.Empty,
        Setting = Guid.Empty,
        Name = "ActiveScheme",
        Source = PowerSource.AC,
        OriginalValue = 0,
        AppliedValue = 0,
    };
}
