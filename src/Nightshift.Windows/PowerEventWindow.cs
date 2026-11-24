using System.ComponentModel;
using System.Runtime.InteropServices;
using Nightshift.Core.Power;
using Nightshift.Windows.Native;

namespace Nightshift.Windows;

/// <summary>
/// Hidden (never shown) top-level window on a dedicated thread. It receives WM_POWERBROADCAST, including
/// registered power-setting notifications (lid switch, console display state, AC/DC, battery %), and is also
/// the target window for the SC_MONITORPOWER display-off command.
/// </summary>
public sealed class PowerEventWindow : IPowerEventSource, IDisplayController
{
    private static readonly Guid LidSwitchStateChange = new("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");
    private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    private static readonly Guid AcDcPowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");
    private static readonly Guid BatteryPercentageRemaining = new("a7ad8041-b45a-4cae-87a3-eecbb468a9e1");

    private readonly string _className = "NightshiftPowerEvents_" + Guid.NewGuid().ToString("N");
    private readonly User32.WndProc _wndProc; // must stay referenced while the window exists
    private readonly List<nint> _notifications = [];
    private readonly ManualResetEventSlim _ready = new();
    private readonly TimeProvider _time;
    private Thread? _thread;
    private Exception? _startError;
    private nint _hwnd;

    public PowerEventWindow(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _wndProc = WindowProc;
    }

    public event Action<PowerEvent>? PowerEventReceived;

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "NIGHTSHIFT power events" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startError is not null)
        {
            throw _startError;
        }
    }

    public void TurnOffDisplay()
    {
        if (_hwnd == 0)
        {
            throw new InvalidOperationException("Power event window is not running.");
        }
        // Handled by DefWindowProc on our own window: no broadcast to other applications.
        if (!User32.PostMessage(_hwnd, User32.WmSysCommand, User32.ScMonitorPower, User32.MonitorOff))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "PostMessage(SC_MONITORPOWER) failed");
        }
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            User32.PostMessage(_hwnd, User32.WmClose, 0, 0);
        }
        _thread?.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    private void MessageLoop()
    {
        var hInstance = Kernel32.GetModuleHandle(0);
        var classNamePtr = Marshal.StringToHGlobalUni(_className);
        try
        {
            var wc = new User32.WndClassEx
            {
                CbSize = (uint)Marshal.SizeOf<User32.WndClassEx>(),
                LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                HInstance = hInstance,
                LpszClassName = classNamePtr,
            };
            if (User32.RegisterClassEx(wc) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx failed");
            }
            _hwnd = User32.CreateWindowEx(0, _className, "NIGHTSHIFT", 0, 0, 0, 0, 0, 0, 0, hInstance, 0);
            if (_hwnd == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowEx failed");
            }
            foreach (var g in new[] { LidSwitchStateChange, ConsoleDisplayState, AcDcPowerSource, BatteryPercentageRemaining })
            {
                var h = User32.RegisterPowerSettingNotification(_hwnd, g, User32.DeviceNotifyWindowHandle);
                if (h != 0)
                {
                    _notifications.Add(h);
                }
            }
        }
        catch (Exception ex)
        {
            _startError = ex;
            Marshal.FreeHGlobal(classNamePtr);
            _ready.Set();
            return;
        }

        _ready.Set();
        while (User32.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(msg);
            User32.DispatchMessage(msg);
        }
        User32.UnregisterClass(_className, hInstance);
        Marshal.FreeHGlobal(classNamePtr);
    }

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case User32.WmPowerBroadcast:
                    HandlePowerBroadcast(wParam, lParam);
                    return 1;
                case User32.WmClose:
                    foreach (var h in _notifications)
                    {
                        User32.UnregisterPowerSettingNotification(h);
                    }
                    _notifications.Clear();
                    User32.DestroyWindow(hWnd);
                    return 0;
                case User32.WmDestroy:
                    _hwnd = 0;
                    User32.PostQuitMessage(0);
                    return 0;
            }
        }
#pragma warning disable CA1031 // An exception must never unwind through a native callback.
        catch (Exception)
#pragma warning restore CA1031
        {
            return 0;
        }
        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandlePowerBroadcast(nint wParam, nint lParam)
    {
        switch (wParam)
        {
            case User32.PbtApmSuspend:
                Raise(PowerEventKind.SystemSuspending, "System is entering sleep (PBT_APMSUSPEND)");
                break;
            case User32.PbtApmResumeAutomatic:
                Raise(PowerEventKind.SystemResumed, "System resumed (PBT_APMRESUMEAUTOMATIC)");
                break;
            case User32.PbtApmResumeSuspend:
                Raise(PowerEventKind.SystemResumed, "System resumed by user input (PBT_APMRESUMESUSPEND)");
                break;
            case User32.PbtPowerSettingChange when lParam != 0:
                // POWERBROADCAST_SETTING: GUID PowerSetting; DWORD DataLength; UCHAR Data[DataLength]
                var setting = Marshal.PtrToStructure<Guid>(lParam);
                var length = Marshal.ReadInt32(lParam, 16);
                if (length < 4)
                {
                    return;
                }
                var data = Marshal.ReadInt32(lParam, 20);
                if (setting == LidSwitchStateChange)
                {
                    Raise(data == 0 ? PowerEventKind.LidClosed : PowerEventKind.LidOpened, data == 0 ? "Lid closed" : "Lid opened");
                }
                else if (setting == ConsoleDisplayState)
                {
                    Raise(data switch { 0 => PowerEventKind.DisplayOff, 2 => PowerEventKind.DisplayDimmed, _ => PowerEventKind.DisplayOn },
                        data switch { 0 => "Display off", 2 => "Display dimmed", _ => "Display on" });
                }
                else if (setting == AcDcPowerSource)
                {
                    Raise(data == 0 ? PowerEventKind.PowerSourceAc : PowerEventKind.PowerSourceDc,
                        data switch { 0 => "Power source: AC", 1 => "Power source: battery", _ => "Power source: short-term (UPS)" });
                }
                else if (setting == BatteryPercentageRemaining)
                {
                    Raise(PowerEventKind.BatteryPercentChanged, $"Battery {data}%");
                }
                break;
        }
    }

    private void Raise(PowerEventKind kind, string detail) =>
        PowerEventReceived?.Invoke(new PowerEvent(_time.GetUtcNow(), kind, detail));
}
