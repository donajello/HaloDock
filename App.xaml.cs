using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace EdgeDock;

public partial class App : Application
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    Mutex? _singleInstance;
    AppSettings _settings = new();
    RadialMenuWindow? _menu;
    EdgeWatcher? _watcher;
    Forms.NotifyIcon? _tray;
    SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args is ["--snapshot-settings", var settingsOut, ..])
        {
            WriteSettingsSnapshot(settingsOut, e.Args[2..]);
            return;
        }

        if (e.Args is ["--snapshot", var outPath, ..])
        {
            WriteSnapshot(outPath, e.Args[2..]);
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\EdgeDock.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("EdgeDock is already running — look for its icon in the system tray.", "EdgeDock");
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        Theme.Initialize();

        _menu = new RadialMenuWindow(_settings);
        _menu.ItemInvoked += Launch;
        _menu.SettingsRequested += OpenSettings;
        _menu.ActiveRingChanged += ring =>
        {
            _settings.ActiveRing = ring;
            try { _settings.Save(); } catch { /* remembered for this session anyway */ }
        };
        _menu.FilesDropped += (item, files, effect) =>
        {
            try { DropTargets.Perform(item, files, effect); }
            catch (Exception ex) { _tray?.ShowBalloonTip(4000, "Couldn't drop on " + item.DisplayName, ex.Message, Forms.ToolTipIcon.Warning); }
        };

        _watcher = new EdgeWatcher(_menu, () => _settings);
        _watcher.Start();

        _tray = CreateTray();

        if (_settings.Rings.All(r => r.Items.Count == 0))
        {
            OpenSettings();
            _tray.ShowBalloonTip(4000, "EdgeDock is running",
                "Add some files or folders, then rest your pointer against the left edge of the screen.", Forms.ToolTipIcon.Info);
        }
    }

    /// <summary>Saved settings, or (when paths are given) demo rings built from them, for screenshots.</summary>
    static AppSettings SnapshotSettings(string[] paths)
    {
        var settings = AppSettings.Load();
        if (paths.Length == 0) return settings;
        settings.Rings =
        [
            new RingPreset { Name = "Main", Color = RingPreset.Palette[0], Items = paths.Select(p => new LaunchItem { Path = p }).ToList() },
            new RingPreset { Name = "Work", Color = RingPreset.Palette[1] },
            new RingPreset { Name = "Games", Color = RingPreset.Palette[2] },
            new RingPreset { Name = "Media", Color = RingPreset.Palette[3] },
        ];
        settings.ActiveRing = 0;
        return settings;
    }

    /// <summary><c>--snapshot out.png [hoverIndex] [paths…]</c>: renders the menu to a PNG for checking the look.</summary>
    static void WriteSnapshot(string outPath, string[] args)
    {
        var hover = args.Length > 0 && int.TryParse(args[0], out var h) ? h : -1;
        var settings = SnapshotSettings(args.Length > 1 ? args[1..] : []);

        var menu = new RadialMenuWindow(settings);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(menu.RenderSnapshot(560, 560, hover)));
        using var file = File.Create(outPath);
        encoder.Save(file);
    }

    /// <summary>
    /// <c>--snapshot-settings out.png [paths…]</c>: briefly shows the settings window and screen-captures it
    /// (the Fluent backdrop is drawn by DWM, so off-screen rendering can't see it).
    /// </summary>
    void WriteSettingsSnapshot(string outPath, string[] paths)
    {
        var window = new SettingsWindow(SnapshotSettings(paths), MonitorInfo.GetAll()) { Topmost = true };
        window.Show();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // The visible frame, without the invisible resize borders GetWindowRect includes.
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var r,
                    System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) != 0)
                NativeMethods.GetWindowRect(hwnd, out r);
            using (var bmp = new Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top))
            {
                using (var g = Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
                bmp.Save(outPath, Drawing.Imaging.ImageFormat.Png);
            }
            window.Close();
            Shutdown();
        };
        timer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false; // otherwise a ghost icon lingers until hovered
            _tray.Dispose();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    Forms.NotifyIcon CreateTray()
    {
        var tray = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "EdgeDock",
            Visible = true,
        };
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                OpenSettings();
                return;
            }
            // Native menu rather than a WinForms one, so it follows the light/dark theme.
            ShellContextMenu.ShowSimpleMenu(
                ("Settings…", false, OpenSettings),
                ("Pause", _watcher!.Paused, () => _watcher.Paused = !_watcher.Paused),
                (null, false, null),
                ("Exit", false, Shutdown));
        };
        return tray;
    }

    /// <summary>A little arc of squares, like the menu itself.</summary>
    static Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xFF, 0xCE, 0x89));
            foreach (var (deg, size) in new[] { (-62, 6f), (-31, 8f), (0, 10f), (31, 8f), (62, 6f) })
            {
                var rad = deg * Math.PI / 180;
                var x = 2 + 17 * Math.Cos(rad);
                var y = 16 + 17 * Math.Sin(rad);
                g.FillRectangle(brush, (float)(x - size / 2), (float)(y - size / 2), size, size);
            }
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings.Clone(), _watcher!.Monitors);
        _settingsWindow.Saved += ApplySettings;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't save settings:\n" + ex.Message, "EdgeDock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ShellIcons.Clear();
        _menu!.Settings = _settings;
        ApplyStartWithWindows(_settings.StartWithWindows);
    }

    static void ApplyStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled) key.SetValue("EdgeDock", $"\"{Environment.ProcessPath}\"");
            else if (key.GetValue("EdgeDock") != null) key.DeleteValue("EdgeDock");
        }
        catch
        {
            // Non-fatal: the setting just won't take effect.
        }
    }

    void Launch(LaunchItem item)
    {
        try
        {
            NativeMethods.AllowSetForegroundWindow(-1); // let the launched app/Explorer window come to the front
            var info = new ProcessStartInfo(item.Path) { UseShellExecute = true };
            if (File.Exists(item.Path)) info.WorkingDirectory = Path.GetDirectoryName(item.Path) ?? "";
            Process.Start(info);
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloonTip(4000, "Couldn't open " + item.DisplayName, ex.Message, Forms.ToolTipIcon.Warning);
        }
    }
}
