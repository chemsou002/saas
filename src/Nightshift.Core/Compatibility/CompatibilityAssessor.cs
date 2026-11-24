using Nightshift.Core.Power;

namespace Nightshift.Core.Compatibility;

public enum FindingSeverity
{
    Info,
    Warning,
    Blocker,
}

public sealed record Finding(FindingSeverity Severity, string Code, string Message);

public enum CompatibilityVerdict
{
    /// <summary>Closed-lid mode can be configured. Real behaviour still needs a hardware test.</summary>
    Configurable,

    /// <summary>Configurable, but known risk factors apply.</summary>
    ConfigurableWithWarnings,

    /// <summary>No lid reported: only keep-awake mode applies.</summary>
    KeepAwakeOnly,

    Unsupported,
}

public sealed record CompatibilityReport(CompatibilityVerdict Verdict, IReadOnlyList<Finding> Findings)
{
    public bool CanStart => Verdict != CompatibilityVerdict.Unsupported;
}

/// <summary>
/// Turns OS-reported capabilities into an honest compatibility statement.
/// It never claims that closed-lid operation WILL work — only whether it can be configured, and what may break it.
/// </summary>
public static class CompatibilityAssessor
{
    public const int MinimumSupportedBuild = 10240; // Windows 10 RTM

    public static CompatibilityReport Assess(SystemCapabilities caps, PowerStatus power, uint? lidActionAc, uint? lidActionDc)
    {
        ArgumentNullException.ThrowIfNull(caps);
        ArgumentNullException.ThrowIfNull(power);
        var f = new List<Finding>();

        if (caps.OsBuild < MinimumSupportedBuild)
        {
            f.Add(new(FindingSeverity.Blocker, "os-unsupported", $"Windows build {caps.OsBuild} is older than Windows 10."));
        }

        var lidUsable = caps.LidPresent && (lidActionAc is not null || lidActionDc is not null);
        if (!caps.LidPresent)
        {
            f.Add(new(FindingSeverity.Info, "no-lid",
                "Windows reports no lid switch (desktop PC, or firmware does not expose it). Only keep-awake mode applies."));
        }
        else if (!lidUsable)
        {
            f.Add(new(FindingSeverity.Warning, "lid-setting-missing",
                "A lid is reported but the active power scheme has no lid-close setting. It may be hidden or managed by policy."));
        }

        if (caps.LidPresent && caps.LidActionManagedByPolicy)
        {
            f.Add(new(FindingSeverity.Blocker, "lid-policy",
                "The lid-close action is set by Group Policy on this device. NIGHTSHIFT will not override administrator policy."));
        }

        if (caps.ModernStandby)
        {
            f.Add(new(FindingSeverity.Warning, "modern-standby",
                "Modern Standby (S0 Low Power Idle) system. With lid action 'Do nothing' the system should stay in the " +
                "active state, but desktop apps can be throttled or paused if Windows enters standby for another reason " +
                "(power button, idle policy, manufacturer utility). Must be verified with the heartbeat test on this device."));
            f.Add(new(FindingSeverity.Info, "display-off-method",
                "Display will be turned off with a temporary display-idle timeout instead of a direct 'monitor off' " +
                "command, because the direct command can trigger standby on some Modern Standby devices."));
            if (!caps.ModernStandbyNetworkConnected)
            {
                f.Add(new(FindingSeverity.Info, "ms-disconnected",
                    "Modern Standby here is 'disconnected': if standby is entered, network access stops."));
            }
        }
        else if (!caps.SleepS3 && !caps.SleepS1 && !caps.SleepS2)
        {
            f.Add(new(FindingSeverity.Info, "no-sleep-states", "No sleep states are reported (sleep may be disabled by firmware or policy)."));
        }

        if (caps.LidPresent && (lidActionAc == (uint)LidCloseAction.DoNothing && lidActionDc == (uint)LidCloseAction.DoNothing))
        {
            f.Add(new(FindingSeverity.Info, "lid-already-nothing", "Lid-close action is already 'Do nothing' for AC and battery; it will not be modified."));
        }

        if (!caps.ThermalControl)
        {
            f.Add(new(FindingSeverity.Warning, "no-thermal-zone",
                "Windows reports no ACPI thermal control. NIGHTSHIFT cannot read reliable temperatures on this device " +
                "(thermal monitoring is a Phase 3 feature). Do not leave the device unattended in an enclosed space."));
        }

        if (caps.BatteryPresent && power.OnAcPower == false)
        {
            f.Add(new(FindingSeverity.Warning, "on-battery",
                $"Running on battery ({power.BatteryPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}%). The session stops automatically at the critical threshold."));
        }

        if (!caps.IsElevated)
        {
            f.Add(new(FindingSeverity.Info, "not-elevated",
                "Not running elevated. Changing the lid action works for most user-owned schemes; if Windows refuses, " +
                "run the PoC from an elevated terminal. No other step needs elevation."));
        }

        f.Add(new(FindingSeverity.Info, "wifi-driver",
            "Wi-Fi drivers and manufacturer utilities may power down the adapter independently of NIGHTSHIFT; connectivity is not guaranteed."));

        CompatibilityVerdict verdict;
        if (f.Any(x => x.Severity == FindingSeverity.Blocker))
        {
            verdict = CompatibilityVerdict.Unsupported;
        }
        else if (!lidUsable)
        {
            verdict = CompatibilityVerdict.KeepAwakeOnly;
        }
        else
        {
            verdict = f.Any(x => x.Severity == FindingSeverity.Warning)
                ? CompatibilityVerdict.ConfigurableWithWarnings
                : CompatibilityVerdict.Configurable;
        }
        return new CompatibilityReport(verdict, f);
    }
}
