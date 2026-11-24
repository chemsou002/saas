namespace Nightshift.Core.Power;

/// <summary>Reads and writes values of the user's power schemes.</summary>
public interface IPowerSettingsStore
{
    Guid GetActiveSchemeId();

    /// <summary>Returns null if the setting does not exist in the scheme (e.g. lid setting on a desktop).</summary>
    uint? Read(Guid scheme, PowerSettingKey key, PowerSource source);

    /// <exception cref="PowerSettingAccessDeniedException">The write requires elevation.</exception>
    void Write(Guid scheme, PowerSettingKey key, PowerSource source, uint value);

    /// <summary>Re-applies the scheme so changed values take effect immediately.</summary>
    void Activate(Guid scheme);
}

public sealed class PowerSettingAccessDeniedException(string message) : Exception(message);

public sealed class PowerApiException(string message, int errorCode) : Exception($"{message} (Win32 error {errorCode})")
{
    public int ErrorCode { get; } = errorCode;
}

/// <summary>A system-required power request ("keep the system awake, display may turn off").</summary>
public interface IKeepAwakeRequest : IDisposable
{
    bool IsHeld { get; }
    void Acquire();
    void Release();
}

public interface ISystemCapabilitiesProvider
{
    SystemCapabilities Get();
}

public interface IPowerStatusProvider
{
    PowerStatus Get();
}

public interface IDisplayController
{
    /// <summary>Asks Windows to turn the display(s) off. The system itself stays awake.</summary>
    void TurnOffDisplay();
}

public enum PowerEventKind
{
    LidClosed,
    LidOpened,
    DisplayOff,
    DisplayOn,
    DisplayDimmed,
    PowerSourceAc,
    PowerSourceDc,
    BatteryPercentChanged,
    SystemSuspending,
    SystemResumed,
}

public sealed record PowerEvent(DateTimeOffset Time, PowerEventKind Kind, string? Detail = null);

/// <summary>OS power notifications (lid switch, display state, AC/DC, suspend/resume).</summary>
public interface IPowerEventSource : IDisposable
{
    event Action<PowerEvent>? PowerEventReceived;
    void Start();
}
