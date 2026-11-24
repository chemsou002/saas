using System.Runtime.InteropServices;
using Nightshift.Core.Power;
using Nightshift.Windows.Native;

namespace Nightshift.Windows;

/// <summary>Power-scheme access through the documented PowrProf API (the same API powercfg.exe uses).</summary>
public sealed class WindowsPowerSettingsStore : IPowerSettingsStore
{
    public Guid GetActiveSchemeId()
    {
        var rc = PowrProf.PowerGetActiveScheme(0, out var ptr);
        Check(rc, "PowerGetActiveScheme");
        try
        {
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            Kernel32.LocalFree(ptr);
        }
    }

    public uint? Read(Guid scheme, PowerSettingKey key, PowerSource source)
    {
        ArgumentNullException.ThrowIfNull(key);
        uint value;
        var rc = source == PowerSource.AC
            ? PowrProf.PowerReadACValueIndex(0, scheme, key.Subgroup, key.Setting, out value)
            : PowrProf.PowerReadDCValueIndex(0, scheme, key.Subgroup, key.Setting, out value);
        if (rc == PowrProf.ErrorFileNotFound)
        {
            return null;
        }
        Check(rc, $"PowerRead{source}ValueIndex({key.Name})");
        return value;
    }

    public void Write(Guid scheme, PowerSettingKey key, PowerSource source, uint value)
    {
        ArgumentNullException.ThrowIfNull(key);
        var rc = source == PowerSource.AC
            ? PowrProf.PowerWriteACValueIndex(0, scheme, key.Subgroup, key.Setting, value)
            : PowrProf.PowerWriteDCValueIndex(0, scheme, key.Subgroup, key.Setting, value);
        if (rc == PowrProf.ErrorAccessDenied)
        {
            throw new PowerSettingAccessDeniedException($"Access denied writing {key.Name} [{source}].");
        }
        Check(rc, $"PowerWrite{source}ValueIndex({key.Name})");
    }

    public void Activate(Guid scheme)
    {
        var rc = PowrProf.PowerSetActiveScheme(0, scheme);
        if (rc == PowrProf.ErrorAccessDenied)
        {
            throw new PowerSettingAccessDeniedException("Access denied re-applying the active power scheme.");
        }
        Check(rc, "PowerSetActiveScheme");
    }

    private static void Check(uint rc, string what)
    {
        if (rc != PowrProf.ErrorSuccess)
        {
            throw new PowerApiException($"{what} failed", (int)rc);
        }
    }
}
