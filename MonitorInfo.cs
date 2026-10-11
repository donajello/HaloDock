using System.Collections.Generic;
using System.Linq;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace EdgeDock;

public sealed class MonitorInfo
{
    public required string DeviceName { get; init; }

    /// <summary>Physical-pixel bounds in virtual-desktop coordinates.</summary>
    public required Drawing.Rectangle Bounds { get; init; }

    public required bool Primary { get; init; }

    /// <summary>Scale factor (1.0 = 96 DPI, 1.5 = 150%).</summary>
    public required double Scale { get; init; }

    public string Label
    {
        get
        {
            var number = DeviceName.Replace(@"\\.\DISPLAY", "");
            var label = $"Display {number}  —  {Bounds.Width}×{Bounds.Height} at ({Bounds.Left}, {Bounds.Top}), {Scale * 100:0}%";
            return Primary ? label + "  ·  Primary" : label;
        }
    }

    public static List<MonitorInfo> GetAll() =>
        Forms.Screen.AllScreens
            .Select(s => new MonitorInfo
            {
                DeviceName = s.DeviceName,
                Bounds = s.Bounds,
                Primary = s.Primary,
                Scale = ScaleFor(s.Bounds),
            })
            .OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top)
            .ToList();

    static double ScaleFor(Drawing.Rectangle b)
    {
        var center = new NativeMethods.POINT(b.Left + b.Width / 2, b.Top + b.Height / 2);
        var hmon = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return NativeMethods.GetDpiForMonitor(hmon, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
    }
}
