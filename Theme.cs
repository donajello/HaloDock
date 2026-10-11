using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EdgeDock;

/// <summary>Follows the Windows light/dark app theme.</summary>
public static class Theme
{
    public static bool IsDark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Raised (on a background thread) when the user switches between light and dark.</summary>
    public static event Action? Changed;

    public static void Initialize()
    {
        ApplyToNativeMenus();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            ApplyToNativeMenus();
            Changed?.Invoke();
        };
    }

    /// <summary>
    /// Win32 popup menus (shell right-click menu, tray menu) are light unless the process opts in. This is the
    /// same undocumented uxtheme switch Explorer, Notepad++ etc. use; it's a no-op on Windows versions without it.
    /// </summary>
    static void ApplyToNativeMenus()
    {
        try
        {
            SetPreferredAppMode(IsDark ? ForceDark : ForceLight);
            FlushMenuThemes();
        }
        catch
        {
            // Older Windows: menus just stay light.
        }
    }

    const int ForceDark = 2, ForceLight = 3;

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    static extern void FlushMenuThemes();
}
