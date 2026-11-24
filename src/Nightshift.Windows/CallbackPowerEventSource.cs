using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nightshift.Core.Power;
using Nightshift.Windows.Native;

namespace Nightshift.Windows;

/// <summary>
/// Window-less power notifications for the Windows service (session 0 has no user desktop):
/// PowerSettingRegisterNotification and PowerRegisterSuspendResumeNotification with DEVICE_NOTIFY_CALLBACK
/// (Windows 8+). Delivers lid switch, console display, AC/DC, battery % and suspend/resume.
/// </summary>
public sealed unsafe class CallbackPowerEventSource : IPowerEventSource
{
    private static readonly Guid LidSwitchStateChange = new("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");
    private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    private static readonly Guid AcDcPowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");
    private static readonly Guid BatteryPercentageRemaining = new("a7ad8041-b45a-4cae-87a3-eecbb468a9e1");

    private readonly TimeProvider _time;
    private readonly List<nint> _settingHandles = [];
    private nint _suspendHandle;
    private GCHandle _self;
    private PowrProfNotify.DeviceNotifySubscribeParameters* _params;

    public CallbackPowerEventSource(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public event Action<PowerEvent>? PowerEventReceived;

    /// <summary>Registrations that failed (logged by the host; the session still works without them).</summary>
    public IReadOnlyList<string> RegistrationErrors => _errors;

    private readonly List<string> _errors = [];

    public void Start()
    {
        if (_params is not null)
        {
            return;
        }
        _self = GCHandle.Alloc(this);
        // Must stay valid for the lifetime of the registrations.
        _params = (PowrProfNotify.DeviceNotifySubscribeParameters*)NativeMemory.Alloc((nuint)sizeof(PowrProfNotify.DeviceNotifySubscribeParameters));
        _params->Callback = &OnNotification;
        _params->Context = GCHandle.ToIntPtr(_self);

        foreach (var (guid, name) in new[]
                 {
                     (LidSwitchStateChange, "lid switch"), (ConsoleDisplayState, "console display"),
                     (AcDcPowerSource, "power source"), (BatteryPercentageRemaining, "battery percentage"),
                 })
        {
            var rc = PowrProfNotify.PowerSettingRegisterNotification(guid, PowrProfNotify.DeviceNotifyCallback, _params, out var h);
            if (rc == 0)
            {
                _settingHandles.Add(h);
            }
            else
            {
                _errors.Add($"{name}: error {rc}");
            }
        }
        var src = PowrProfNotify.PowerRegisterSuspendResumeNotification(PowrProfNotify.DeviceNotifyCallback, _params, out _suspendHandle);
        if (src != 0)
        {
            _errors.Add($"suspend/resume: error {src}");
        }
    }

    public void Dispose()
    {
        foreach (var h in _settingHandles)
        {
            PowrProfNotify.PowerSettingUnregisterNotification(h);
        }
        _settingHandles.Clear();
        if (_suspendHandle != 0)
        {
            PowrProfNotify.PowerUnregisterSuspendResumeNotification(_suspendHandle);
            _suspendHandle = 0;
        }
        if (_params is not null)
        {
            NativeMemory.Free(_params);
            _params = null;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnNotification(nint context, uint type, nint setting)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is CallbackPowerEventSource self)
            {
                self.Dispatch(type, setting);
            }
        }
#pragma warning disable CA1031 // never let an exception cross into native code
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        return 0;
    }

    private void Dispatch(uint type, nint setting)
    {
        switch ((nint)type)
        {
            case User32.PbtApmSuspend:
                Raise(PowerEventKind.SystemSuspending, "System is entering sleep (PBT_APMSUSPEND)");
                return;
            case User32.PbtApmResumeAutomatic:
                Raise(PowerEventKind.SystemResumed, "System resumed (PBT_APMRESUMEAUTOMATIC)");
                return;
            case User32.PbtApmResumeSuspend:
                Raise(PowerEventKind.SystemResumed, "System resumed by user input (PBT_APMRESUMESUSPEND)");
                return;
            case User32.PbtPowerSettingChange when setting != 0:
                break;
            default:
                return;
        }

        // POWERBROADCAST_SETTING: GUID PowerSetting; DWORD DataLength; UCHAR Data[]
        var guid = Marshal.PtrToStructure<Guid>(setting);
        if (Marshal.ReadInt32(setting, 16) < 4)
        {
            return;
        }
        var data = Marshal.ReadInt32(setting, 20);
        if (guid == LidSwitchStateChange)
        {
            Raise(data == 0 ? PowerEventKind.LidClosed : PowerEventKind.LidOpened, data == 0 ? "Lid closed" : "Lid opened");
        }
        else if (guid == ConsoleDisplayState)
        {
            Raise(data switch { 0 => PowerEventKind.DisplayOff, 2 => PowerEventKind.DisplayDimmed, _ => PowerEventKind.DisplayOn },
                data switch { 0 => "Display off", 2 => "Display dimmed", _ => "Display on" });
        }
        else if (guid == AcDcPowerSource)
        {
            Raise(data == 0 ? PowerEventKind.PowerSourceAc : PowerEventKind.PowerSourceDc,
                data switch { 0 => "Power source: AC", 1 => "Power source: battery", _ => "Power source: short-term (UPS)" });
        }
        else if (guid == BatteryPercentageRemaining)
        {
            Raise(PowerEventKind.BatteryPercentChanged, $"Battery {data}%");
        }
    }

    private void Raise(PowerEventKind kind, string detail) =>
        PowerEventReceived?.Invoke(new PowerEvent(_time.GetUtcNow(), kind, detail));
}
