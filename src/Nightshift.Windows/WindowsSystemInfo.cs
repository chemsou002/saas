using System.Runtime.InteropServices;
using Microsoft.Win32;
using Nightshift.Core.Power;
using Nightshift.Windows.Native;

namespace Nightshift.Windows;

/// <summary>Reads SYSTEM_POWER_CAPABILITIES via CallNtPowerInformation and the battery state via GetSystemPowerStatus.</summary>
public sealed class WindowsSystemInfo : ISystemCapabilitiesProvider, IPowerStatusProvider
{
    // SYSTEM_POWER_CAPABILITIES is 76 bytes: 22 BOOLEAN/BYTE flags, 8 bytes (HiberFileType, AoAcConnectivitySupported,
    // spare[6]), 2 battery BOOLEANs, BATTERY_REPORTING_SCALE[3] (24 bytes), 5 SYSTEM_POWER_STATE enums (20 bytes).
    private const int CapabilitiesSize = 76;
    private const int OffPowerButtonPresent = 0;
    private const int OffLidPresent = 2;
    private const int OffS1 = 3;
    private const int OffS2 = 4;
    private const int OffS3 = 5;
    private const int OffS4 = 6;
    private const int OffS5 = 7;
    private const int OffHiberFilePresent = 8;
    private const int OffThermalControl = 13;
    private const int OffHiberboot = 18;
    private const int OffAoAc = 20;
    private const int OffAoAcConnectivitySupported = 23;
    private const int OffSystemBatteriesPresent = 30;

    public unsafe SystemCapabilities Get()
    {
        var buf = stackalloc byte[CapabilitiesSize];
        var status = PowrProf.CallNtPowerInformation(PowrProf.SystemPowerCapabilitiesLevel, 0, 0, buf, CapabilitiesSize);
        if (status != 0)
        {
            throw new PowerApiException("CallNtPowerInformation(SystemPowerCapabilities) failed", status);
        }
        bool B(int offset) => buf[offset] != 0;

        return new SystemCapabilities
        {
            OsDescription = RuntimeInformation.OSDescription,
            OsBuild = Environment.OSVersion.Version.Build,
            PowerButtonPresent = B(OffPowerButtonPresent),
            LidPresent = B(OffLidPresent),
            SleepS1 = B(OffS1),
            SleepS2 = B(OffS2),
            SleepS3 = B(OffS3),
            HibernateS4 = B(OffS4),
            SoftOffS5 = B(OffS5),
            HiberFilePresent = B(OffHiberFilePresent),
            ThermalControl = B(OffThermalControl),
            FastStartup = B(OffHiberboot),
            ModernStandby = B(OffAoAc),
            ModernStandbyNetworkConnected = B(OffAoAcConnectivitySupported),
            BatteryPresent = B(OffSystemBatteriesPresent),
            IsElevated = Environment.IsPrivilegedProcess,
            LidActionManagedByPolicy = IsLidActionPolicyManaged(),
        };
    }

    PowerStatus IPowerStatusProvider.Get()
    {
        if (!Kernel32.GetSystemPowerStatus(out var s))
        {
            return new PowerStatus(null, null, false, false);
        }
        bool? onAc = s.ACLineStatus switch { 0 => false, 1 => true, _ => null };
        var noBattery = s.BatteryFlag == 128 || s.BatteryFlag == 255;
        int? pct = s.BatteryLifePercent <= 100 && !noBattery ? s.BatteryLifePercent : null;
        return new PowerStatus(onAc, pct, !noBattery, (s.SystemStatusFlag & 1) != 0);
    }

    /// <summary>Group Policy can pin the lid action; scheme writes then have no effect.</summary>
    private static bool IsLidActionPolicyManaged()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\" + PowerSettingKeys.LidCloseAction.Setting.ToString("D"));
            return key is not null && (key.GetValue("ACSettingIndex") is not null || key.GetValue("DCSettingIndex") is not null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
