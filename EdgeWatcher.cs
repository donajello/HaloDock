using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;

namespace HaloDock;

/// <summary>Polls the pointer and opens the menu when it rests against an enabled edge of an enabled monitor.</summary>
public sealed class EdgeWatcher
{
    readonly RadialMenuWindow _menu;
    readonly Func<AppSettings> _settings;
    readonly DispatcherTimer _timer;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    List<MonitorInfo> _monitors = MonitorInfo.GetAll();
    volatile bool _monitorsDirty;
    bool _armed = true;
    long _dwellStart = -1;

    public bool Paused { get; set; }

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    public EdgeWatcher(RadialMenuWindow menu, Func<AppSettings> settings)
    {
        _menu = menu;
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += Tick;
        SystemEvents.DisplaySettingsChanged += (_, _) => _monitorsDirty = true;
        // After the menu closes, the pointer must leave the edge before it can open again.
        _menu.Hidden += () => { _armed = false; _dwellStart = -1; };
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    void Tick(object? sender, EventArgs e)
    {
        if (_monitorsDirty)
        {
            _monitorsDirty = false;
            _monitors = MonitorInfo.GetAll();
        }
        if (_menu.IsOpen || !NativeMethods.GetCursorPos(out var p)) return;

        var s = _settings();
        var monitor = _monitors.FirstOrDefault(m => m.Bounds.Contains(p.X, p.Y));
        var edge = monitor == null ? null : EdgeUnder(monitor, p, s);
        if (edge == null)
        {
            _armed = true;
            _dwellStart = -1;
            return;
        }

        if (MouseButtonDown() && WindowBeingMovedOrResized())
        {
            // Dragging a window into the edge (Snap) shouldn't pop the menu. Dragging files should, so they can be dropped on an icon.
            _armed = false;
            _dwellStart = -1;
            return;
        }

        if (Paused || !_armed || !IsEnabled(monitor!, s)
            || (s.OuterEdgesOnly && HasNeighborBeyond(monitor!, edge.Value, p))
            || (s.IgnoreFullscreen && FullscreenAppOn(monitor!)))
        {
            _dwellStart = -1;
            return;
        }

        if (_dwellStart < 0) _dwellStart = _clock.ElapsedMilliseconds;
        if (_clock.ElapsedMilliseconds - _dwellStart >= s.HoverDelayMs)
        {
            _dwellStart = -1;
            _armed = false;
            _menu.Open(monitor!, new Drawing.Point(p.X, p.Y), edge.Value);
        }
    }

    /// <summary>The enabled edge the pointer is resting against, if any. Sides win over the top in a corner.</summary>
    static ScreenEdge? EdgeUnder(MonitorInfo monitor, NativeMethods.POINT p, AppSettings s)
    {
        var b = monitor.Bounds;
        var w = Math.Max(1, s.EdgeWidthPx);
        if (s.Edges.Contains(ScreenEdge.Left) && p.X < b.Left + w) return ScreenEdge.Left;
        if (s.Edges.Contains(ScreenEdge.Right) && p.X >= b.Right - w) return ScreenEdge.Right;
        if (s.Edges.Contains(ScreenEdge.Top) && p.Y < b.Top + w) return ScreenEdge.Top;
        return null;
    }

    static bool IsEnabled(MonitorInfo monitor, AppSettings s) => s.MonitorMode switch
    {
        MonitorMode.Primary => monitor.Primary,
        MonitorMode.Selected => s.SelectedMonitors.Contains(monitor.DeviceName, StringComparer.OrdinalIgnoreCase),
        _ => true,
    };

    /// <summary>True if another monitor continues past this edge, i.e. it's a seam rather than a wall.</summary>
    bool HasNeighborBeyond(MonitorInfo monitor, ScreenEdge edge, NativeMethods.POINT p)
    {
        var b = monitor.Bounds;
        var (x, y) = edge switch
        {
            ScreenEdge.Right => (b.Right, p.Y),
            ScreenEdge.Top => (p.X, b.Top - 1),
            _ => (b.Left - 1, p.Y),
        };
        return _monitors.Any(o => o != monitor && o.Bounds.Contains(x, y));
    }

    static bool MouseButtonDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0
        || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON) & 0x8000) != 0
        || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MBUTTON) & 0x8000) != 0;

    static bool WindowBeingMovedOrResized()
    {
        var info = new NativeMethods.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        return NativeMethods.GetGUIThreadInfo(0, ref info)
               && ((info.flags & NativeMethods.GUI_INMOVESIZE) != 0 || info.hwndMoveSize != IntPtr.Zero);
    }

    static bool FullscreenAppOn(MonitorInfo monitor)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return false;

        var cls = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // desktop / taskbar

        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
        var b = monitor.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }
}
