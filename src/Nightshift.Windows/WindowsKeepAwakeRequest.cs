using System.ComponentModel;
using System.Runtime.InteropServices;
using Nightshift.Core.Power;
using Nightshift.Windows.Native;

namespace Nightshift.Windows;

/// <summary>
/// PowerCreateRequest/PowerSetRequest(PowerRequestSystemRequired): blocks idle sleep without forcing the display on.
/// The request is visible in "powercfg /requests" and is released automatically by Windows if the process dies,
/// so a crash can never leave the system permanently awake.
/// </summary>
public sealed class WindowsKeepAwakeRequest : IKeepAwakeRequest
{
    private const string Reason = "NIGHTSHIFT work session: keeping the system awake for selected applications.";
    private nint _handle;

    public bool IsHeld { get; private set; }

    public void Acquire()
    {
        if (IsHeld)
        {
            return;
        }
        if (_handle == 0)
        {
            var text = Marshal.StringToHGlobalUni(Reason);
            try
            {
                var ctx = new Kernel32.ReasonContext
                {
                    Version = Kernel32.PowerRequestContextVersion,
                    Flags = Kernel32.PowerRequestContextSimpleString,
                    SimpleReasonString = text,
                };
                var h = Kernel32.PowerCreateRequest(ctx);
                if (h == 0 || h == -1)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "PowerCreateRequest failed");
                }
                _handle = h;
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }
        if (!Kernel32.PowerSetRequest(_handle, Kernel32.PowerRequestType.SystemRequired))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "PowerSetRequest(SystemRequired) failed");
        }
        IsHeld = true;
    }

    public void Release()
    {
        if (!IsHeld)
        {
            return;
        }
        if (!Kernel32.PowerClearRequest(_handle, Kernel32.PowerRequestType.SystemRequired))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "PowerClearRequest failed");
        }
        IsHeld = false;
    }

    public void Dispose()
    {
        try
        {
            Release();
        }
        finally
        {
            if (_handle != 0)
            {
                Kernel32.CloseHandle(_handle);
                _handle = 0;
            }
        }
    }
}
