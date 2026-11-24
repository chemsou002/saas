namespace Nightshift.Core.Power;

/// <summary>Which half of a power-scheme setting is addressed: plugged in (AC) or on battery (DC).</summary>
public enum PowerSource
{
    AC,
    DC,
}

/// <summary>Windows "When I close the lid" values (GUID_LIDCLOSE_ACTION).</summary>
public enum LidCloseAction : uint
{
    DoNothing = 0,
    Sleep = 1,
    Hibernate = 2,
    ShutDown = 3,
}

/// <summary>Identifies one setting inside a Windows power scheme (subgroup GUID + setting GUID).</summary>
public sealed record PowerSettingKey(Guid Subgroup, Guid Setting, string Name);

/// <summary>Well-known power setting GUIDs (from winnt.h).</summary>
public static class PowerSettingKeys
{
    public static readonly Guid ButtonsSubgroup = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    public static readonly Guid SleepSubgroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    public static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");

    /// <summary>GUID_LIDCLOSE_ACTION. Values: see <see cref="LidCloseAction"/>.</summary>
    public static readonly PowerSettingKey LidCloseAction =
        new(ButtonsSubgroup, new Guid("5ca83367-6e45-459f-a27b-476b1d01c936"), "LidCloseAction");

    /// <summary>GUID_STANDBY_TIMEOUT ("Sleep after"), seconds; 0 = never. Read-only in Phase 1.</summary>
    public static readonly PowerSettingKey StandbyTimeout =
        new(SleepSubgroup, new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), "StandbyTimeout");

    /// <summary>GUID_HIBERNATE_TIMEOUT ("Hibernate after"), seconds; 0 = never. Read-only in Phase 1.</summary>
    public static readonly PowerSettingKey HibernateTimeout =
        new(SleepSubgroup, new Guid("9d7815a6-7ee4-497e-8888-515a05f02364"), "HibernateTimeout");

    /// <summary>GUID_VIDEO_POWERDOWN_TIMEOUT ("Turn off display after"), seconds; 0 = never.</summary>
    public static readonly PowerSettingKey VideoPowerDownTimeout =
        new(VideoSubgroup, new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"), "VideoPowerDownTimeout");

    public static IReadOnlyList<PowerSettingKey> All { get; } =
        [LidCloseAction, StandbyTimeout, HibernateTimeout, VideoPowerDownTimeout];

    public static PowerSettingKey? Find(Guid subgroup, Guid setting) =>
        All.FirstOrDefault(k => k.Subgroup == subgroup && k.Setting == setting);
}

/// <summary>
/// Hardware/OS power capabilities as reported by Windows (SYSTEM_POWER_CAPABILITIES).
/// Every flag is what the OS reports — NIGHTSHIFT never infers or invents capabilities.
/// </summary>
public sealed record SystemCapabilities
{
    public required string OsDescription { get; init; }
    public required int OsBuild { get; init; }
    public bool IsWindows11 => OsBuild >= 22000;

    public bool LidPresent { get; init; }
    public bool PowerButtonPresent { get; init; }
    public bool SleepS1 { get; init; }
    public bool SleepS2 { get; init; }
    public bool SleepS3 { get; init; }
    public bool HibernateS4 { get; init; }
    public bool SoftOffS5 { get; init; }
    public bool HiberFilePresent { get; init; }
    public bool FastStartup { get; init; }

    /// <summary>"Always On, Always Connected" = Modern Standby (S0 Low Power Idle).</summary>
    public bool ModernStandby { get; init; }
    public bool ModernStandbyNetworkConnected { get; init; }
    public bool ThermalControl { get; init; }
    public bool BatteryPresent { get; init; }

    public bool IsElevated { get; init; }

    /// <summary>Group Policy pins the lid-close action; scheme changes would have no effect.</summary>
    public bool LidActionManagedByPolicy { get; init; }
}

/// <summary>Current power-supply status (GetSystemPowerStatus).</summary>
public sealed record PowerStatus(bool? OnAcPower, int? BatteryPercent, bool BatteryPresent, bool BatterySaverOn)
{
    public PowerSource EffectiveSource => OnAcPower == false ? PowerSource.DC : PowerSource.AC;
}
