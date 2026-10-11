using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace EdgeDock;

/// <summary>Shows Explorer's own right-click menu for a file or folder.</summary>
public static class ShellContextMenu
{
    const uint CmdFirst = 1, CmdLast = 0x7FFF;
    const uint CMF_NORMAL = 0x0, CMF_EXTENDEDVERBS = 0x100;
    const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
    const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000, CMIC_MASK_PTINVOKE = 0x20000000;
    const int WM_INITMENUPOPUP = 0x0117, WM_DRAWITEM = 0x002B, WM_MEASUREITEM = 0x002C, WM_MENUCHAR = 0x0120, WM_NULL = 0x0000;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;

    static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");
    static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    static HwndSource? _owner;
    static IContextMenu2? _menu2;
    static IContextMenu3? _menu3;

    /// <summary>Shows the menu at a screen point (physical pixels). Returns true if a command was chosen.</summary>
    public static bool Show(string path, int x, int y)
    {
        var owner = Owner();
        object? itemObj = null;
        IContextMenu? menu = null;
        var hmenu = IntPtr.Zero;
        try
        {
            var iidItem = typeof(IShellItem).GUID;
            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out itemObj);
            var bhid = BHID_SFUIObject;
            var iidMenu = IID_IContextMenu;
            ((IShellItem)itemObj).BindToHandler(IntPtr.Zero, ref bhid, ref iidMenu, out var menuObj);
            menu = (IContextMenu)menuObj;
            _menu2 = menuObj as IContextMenu2; // needed so "Send to" / "Open with" submenus can fill themselves in
            _menu3 = menuObj as IContextMenu3;

            hmenu = CreatePopupMenu();
            var shift = (NativeMethods.GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            menu.QueryContextMenu(hmenu, 0, CmdFirst, CmdLast, CMF_NORMAL | (shift ? CMF_EXTENDEDVERBS : 0));

            // A popup menu only dismisses properly when its owner is the foreground window.
            SetForegroundWindow(owner);
            var cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, owner, IntPtr.Zero);
            PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd == 0) return false;

            var mask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
            if ((NativeMethods.GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) mask |= CMIC_MASK_SHIFT_DOWN;
            if ((NativeMethods.GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) mask |= CMIC_MASK_CONTROL_DOWN;
            var verb = new IntPtr(cmd - CmdFirst); // MAKEINTRESOURCE(offset)
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = mask,
                hwnd = owner,
                lpVerb = verb,
                lpVerbW = verb,
                nShow = 1, // SW_SHOWNORMAL
                ptInvoke = new NativeMethods.POINT(x, y),
            };
            NativeMethods.AllowSetForegroundWindow(-1);
            try { menu.InvokeCommand(ref info); }
            catch (COMException) { /* cancelled, e.g. the user said no to a delete prompt */ }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _menu2 = null;
            _menu3 = null;
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (menu != null) Marshal.ReleaseComObject(menu);
            if (itemObj != null) Marshal.ReleaseComObject(itemObj);
        }
    }

    /// <summary>A plain Win32 popup menu at the pointer (used for the tray icon, so it matches the system theme).</summary>
    public static void ShowSimpleMenu(params (string? Text, bool Checked, Action? OnClick)[] entries)
    {
        const uint MF_STRING = 0x0, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
        var owner = Owner();
        var hmenu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < entries.Length; i++)
            {
                var (text, isChecked, _) = entries[i];
                if (text == null) AppendMenu(hmenu, MF_SEPARATOR, UIntPtr.Zero, null);
                else AppendMenu(hmenu, MF_STRING | (isChecked ? MF_CHECKED : 0), (UIntPtr)(uint)(i + 1), text);
            }

            NativeMethods.GetCursorPos(out var p);
            SetForegroundWindow(owner);
            var cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, owner, IntPtr.Zero);
            PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd > 0) entries[cmd - 1].OnClick?.Invoke();
        }
        finally
        {
            DestroyMenu(hmenu);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr id, string? text);

    /// <summary>Hidden window that owns the menu and forwards owner-draw/submenu messages to the shell.</summary>
    static IntPtr Owner()
    {
        if (_owner == null)
        {
            _owner = new HwndSource(new HwndSourceParameters("EdgeDockMenuOwner")
            {
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
                ExtendedWindowStyle = (int)NativeMethods.WS_EX_TOOLWINDOW,
                Width = 0,
                Height = 0,
            });
            _owner.AddHook(OwnerHook);
        }
        return _owner.Handle;
    }

    static IntPtr OwnerHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
        {
            try
            {
                if (_menu3 != null && _menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
                {
                    handled = true;
                    return result;
                }
                if (_menu2 != null && _menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                {
                    handled = true;
                    return msg == WM_INITMENUPOPUP ? IntPtr.Zero : new IntPtr(1);
                }
            }
            catch
            {
                // Misbehaving shell extensions shouldn't take the app down.
            }
        }
        return IntPtr.Zero;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        // Remaining methods unused.
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        void InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        void InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        void InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public NativeMethods.POINT ptInvoke;
    }

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    static extern uint TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
