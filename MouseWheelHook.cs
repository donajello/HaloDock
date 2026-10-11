using System;
using System.Runtime.InteropServices;

namespace HaloDock;

/// <summary>
/// Low-level mouse hook, installed only while the menu is open. The overlay never takes focus,
/// so this is the reliable way to get wheel input — and to stop the app underneath from scrolling too.
/// </summary>
public sealed class MouseWheelHook : IDisposable
{
    readonly NativeMethods.LowLevelMouseProc _proc; // kept alive so the GC can't collect the callback
    IntPtr _handle;

    /// <summary>Receives the wheel delta; return true to swallow the event.</summary>
    public Func<int, bool>? Wheel { get; set; }

    public MouseWheelHook() => _proc = Callback;

    public void Install()
    {
        if (_handle != IntPtr.Zero) return;
        _handle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
    }

    public void Uninstall()
    {
        if (_handle == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == NativeMethods.WM_MOUSEWHEEL && Wheel != null)
        {
            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var delta = (short)((info.mouseData >> 16) & 0xFFFF);
            if (Wheel(delta)) return new IntPtr(1);
        }
        return NativeMethods.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
