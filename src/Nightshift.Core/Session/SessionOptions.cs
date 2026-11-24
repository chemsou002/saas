using Nightshift.Core.Power;

namespace Nightshift.Core.Session;

public enum DisplayOffMethod
{
    /// <summary>IdleTimeout on Modern Standby devices, MonitorPower otherwise.</summary>
    Auto,

    /// <summary>Send SC_MONITORPOWER once after a delay. Immediate, but risky on Modern Standby.</summary>
    MonitorPower,

    /// <summary>Temporarily shorten "Turn off display after" (tracked and restored).</summary>
    IdleTimeout,

    /// <summary>Leave the display alone (closing the lid normally switches off the internal panel anyway).</summary>
    None,
}

public sealed record SessionOptions
{
    /// <summary>Null = unlimited (ends on manual stop or a safety condition).</summary>
    public TimeSpan? Duration { get; init; }

    public bool ConfigureLid { get; init; } = true;
    public DisplayOffMethod DisplayOff { get; init; } = DisplayOffMethod.Auto;
    public TimeSpan DisplayOffDelay { get; init; } = TimeSpan.FromSeconds(15);
    public uint DisplayIdleTimeoutSeconds { get; init; } = 60;

    /// <summary>On battery, the session safely stops at or below this percentage.</summary>
    public int BatteryCriticalPercent { get; init; } = 15;
    public int BatteryWarningPercent { get; init; } = 25;
    public TimeSpan BatteryPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Processes to watch for liveness (monitoring only — never controlled or killed).</summary>
    public IReadOnlyList<int> WatchedProcessIds { get; init; } = [];

    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    public static DisplayOffMethod Resolve(DisplayOffMethod requested, SystemCapabilities caps)
    {
        ArgumentNullException.ThrowIfNull(caps);
        return requested == DisplayOffMethod.Auto
            ? caps.ModernStandby ? DisplayOffMethod.IdleTimeout : DisplayOffMethod.MonitorPower
            : requested;
    }
}

public static class SessionPlanner
{
    /// <summary>
    /// Minimal set of scheme changes. Sleep prevention uses a power request, not a timeout change, so the
    /// user's sleep/hibernate timeouts are never modified.
    /// </summary>
    public static IReadOnlyList<PlannedChange> Plan(SessionOptions options, SystemCapabilities caps, DisplayOffMethod displayMethod)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(caps);
        var plan = new List<PlannedChange>();
        if (options.ConfigureLid && caps.LidPresent)
        {
            // Both halves: the charger may be plugged or unplugged mid-session.
            plan.Add(new(PowerSettingKeys.LidCloseAction, PowerSource.AC, (uint)LidCloseAction.DoNothing));
            plan.Add(new(PowerSettingKeys.LidCloseAction, PowerSource.DC, (uint)LidCloseAction.DoNothing));
        }
        if (displayMethod == DisplayOffMethod.IdleTimeout)
        {
            plan.Add(new(PowerSettingKeys.VideoPowerDownTimeout, PowerSource.AC, options.DisplayIdleTimeoutSeconds));
            plan.Add(new(PowerSettingKeys.VideoPowerDownTimeout, PowerSource.DC, options.DisplayIdleTimeoutSeconds));
        }
        return plan;
    }
}
