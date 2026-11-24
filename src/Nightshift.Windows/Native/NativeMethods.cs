using System.Runtime.InteropServices;

namespace Nightshift.Windows.Native;

internal static partial class PowrProf
{
    public const uint ErrorSuccess = 0;
    public const uint ErrorFileNotFound = 2;
    public const uint ErrorAccessDenied = 5;
    public const int SystemPowerCapabilitiesLevel = 4; // POWER_INFORMATION_LEVEL.SystemPowerCapabilities

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSetActiveScheme(nint userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadACValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid, out uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadDCValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid, out uint dcValueIndex);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteACValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid, uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteDCValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid, uint dcValueIndex);

    /// <returns>NTSTATUS (0 = success).</returns>
    [LibraryImport("powrprof.dll")]
    public static unsafe partial int CallNtPowerInformation(int informationLevel, nint inputBuffer, uint inputBufferLength, byte* outputBuffer, uint outputBufferLength);
}

internal static partial class Kernel32
{
    public const uint PowerRequestContextVersion = 0;
    public const uint PowerRequestContextSimpleString = 0x1;

    public enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3,
    }

    /// <summary>REASON_CONTEXT with the SimpleReasonString arm of the union (union padded to its largest member).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
        public nint Reserved1;
        public nint Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemPowerStatus
    {
        public byte ACLineStatus;       // 0 offline, 1 online, 255 unknown
        public byte BatteryFlag;        // 128 = no system battery, 255 = unknown
        public byte BatteryLifePercent; // 0-100, 255 unknown
        public byte SystemStatusFlag;   // 1 = battery saver on
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint PowerCreateRequest(in ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PowerSetRequest(nint powerRequest, PowerRequestType requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PowerClearRequest(nint powerRequest, PowerRequestType requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint mem);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
    public static partial nint GetModuleHandle(nint moduleName);
}

internal static partial class User32
{
    public const uint WmClose = 0x0010;
    public const uint WmDestroy = 0x0002;
    public const uint WmSysCommand = 0x0112;
    public const uint WmPowerBroadcast = 0x0218;
    public const nint ScMonitorPower = 0xF170;
    public const nint MonitorOff = 2;

    public const nint PbtApmSuspend = 0x0004;
    public const nint PbtApmResumeSuspend = 0x0007;
    public const nint PbtApmPowerStatusChange = 0x000A;
    public const nint PbtApmResumeAutomatic = 0x0012;
    public const nint PbtPowerSettingChange = 0x8013;

    public const uint DeviceNotifyWindowHandle = 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public nint LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public nint LpszMenuName;
        public nint LpszClassName;
        public nint HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint HWnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PtX;
        public int PtY;
        public uint LPrivate;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(in WndClassEx wndClass);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClass(string className, nint hInstance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out Msg msg, nint hWnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Msg msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in Msg msg);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint RegisterPowerSettingNotification(nint recipient, in Guid powerSettingGuid, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterPowerSettingNotification(nint handle);
}
