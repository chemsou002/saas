using Nightshift.Core.Power;

namespace Nightshift.Core.Tests;

internal sealed class FakePowerStore : IPowerSettingsStore
{
    public Guid Active { get; set; } = Guid.NewGuid();
    public Dictionary<(Guid Scheme, Guid Setting, PowerSource Source), uint> Values { get; } = [];
    public List<string> Writes { get; } = [];
    public int Activations { get; private set; }

    /// <summary>Throw access-denied on the Nth write (1-based); 0 = never.</summary>
    public int DenyWriteNumber { get; set; }

    /// <summary>Simulate a crash: throw a non-API exception on the Nth write, AFTER the value was written.</summary>
    public int CrashAfterWriteNumber { get; set; }

    public FakePowerStore Set(PowerSettingKey key, uint ac, uint dc, Guid? scheme = null)
    {
        Values[(scheme ?? Active, key.Setting, PowerSource.AC)] = ac;
        Values[(scheme ?? Active, key.Setting, PowerSource.DC)] = dc;
        return this;
    }

    public uint? Get(PowerSettingKey key, PowerSource s, Guid? scheme = null) =>
        Values.TryGetValue((scheme ?? Active, key.Setting, s), out var v) ? v : null;

    public Guid GetActiveSchemeId() => Active;

    public uint? Read(Guid scheme, PowerSettingKey key, PowerSource source) =>
        Values.TryGetValue((scheme, key.Setting, source), out var v) ? v : null;

    public Action<PowerSettingKey, PowerSource>? BeforeWrite { get; set; }

    public void Write(Guid scheme, PowerSettingKey key, PowerSource source, uint value)
    {
        BeforeWrite?.Invoke(key, source);
        var n = Writes.Count + 1;
        if (n == DenyWriteNumber)
        {
            DenyWriteNumber = 0;
            throw new PowerSettingAccessDeniedException("denied");
        }
        Values[(scheme, key.Setting, source)] = value;
        Writes.Add($"{key.Name}/{source}={value}");
        if (n == CrashAfterWriteNumber)
        {
            CrashAfterWriteNumber = 0;
            throw new SimulatedCrashException();
        }
    }

    public void Activate(Guid scheme) => Activations++;
}

internal sealed class SimulatedCrashException : Exception;

internal sealed class FakeKeepAwake : IKeepAwakeRequest
{
    public bool IsHeld { get; private set; }
    public int Acquired { get; private set; }

    public void Acquire()
    {
        IsHeld = true;
        Acquired++;
    }

    public void Release() => IsHeld = false;

    public void Dispose() => Release();
}

internal sealed class FakeCaps(SystemCapabilities caps) : ISystemCapabilitiesProvider
{
    public SystemCapabilities Caps { get; set; } = caps;

    public SystemCapabilities Get() => Caps;
}

internal sealed class FakePowerStatus(PowerStatus status) : IPowerStatusProvider
{
    public PowerStatus Status { get; set; } = status;

    public PowerStatus Get() => Status;
}

internal sealed class FakeDisplay : IDisplayController
{
    public int OffCount { get; private set; }

    public void TurnOffDisplay() => OffCount++;
}

internal sealed class FakeEvents : IPowerEventSource
{
    public event Action<PowerEvent>? PowerEventReceived;

    public bool Started { get; private set; }

    public void Start() => Started = true;

    public void Raise(PowerEventKind kind) => PowerEventReceived?.Invoke(new PowerEvent(DateTimeOffset.UtcNow, kind));

    public void Dispose()
    {
    }
}

internal static class TestData
{
    public static SystemCapabilities Laptop(bool modernStandby = false) => new()
    {
        OsDescription = "Microsoft Windows 10.0.22631",
        OsBuild = 22631,
        LidPresent = true,
        SleepS3 = !modernStandby,
        ModernStandby = modernStandby,
        HibernateS4 = true,
        BatteryPresent = true,
        ThermalControl = true,
        IsElevated = true,
    };

    public static SystemCapabilities Desktop() => Laptop() with { LidPresent = false, BatteryPresent = false };

    public static PowerStatus OnAc() => new(true, 80, true, false);

    public static PowerStatus OnBattery(int pct) => new(false, pct, true, false);
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nightshift-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
