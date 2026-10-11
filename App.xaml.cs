using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace HaloDock;

public partial class App : Application
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    Mutex? _singleInstance;
    AppSettings _settings = new();
    RadialMenuWindow? _menu;
    EdgeWatcher? _watcher;
    Forms.NotifyIcon? _tray;
    SettingsWindow? _settingsWindow;
    Updater.Release? _pendingRelease;
    Version? _notifiedVersion;

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
        if (e.Args is ["--demo", var demoDir, ..])
        {
            WriteDemo(demoDir, e.Args[2..]);
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\HaloDock.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("HaloDock is already running \u2014 look for its icon in the system tray.", "HaloDock");
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        ApplyStartWithWindows(_settings.StartWithWindows); // also points the entry at wherever this exe now lives
        Theme.Initialize();

        _menu = new RadialMenuWindow(_settings);
        _menu.ItemInvoked += Launch;
        _menu.SettingsRequested += OpenSettings;
        _menu.ActiveRingChanged += ring =>
        {
            _settings.ActiveRing = ring;
            SaveQuietly();
        };
        _menu.RingEdited += () =>
        {
            SaveQuietly();
            _settingsWindow?.ReloadRings(_settings); // keep an open settings window in step with the wheel
        };
        _menu.FilesDropped += (item, files, effect) =>
        {
            try { DropTargets.Perform(item, files, effect); }
            catch (Exception ex) { _tray?.ShowBalloonTip(4000, "Couldn't drop on " + item.DisplayName, ex.Message, Forms.ToolTipIcon.Warning); }
        };

        _watcher = new EdgeWatcher(_menu, () => _settings);
        _watcher.Start();

        _tray = CreateTray();
        StartUpdateChecks();

        if (_settings.Rings.All(r => r.Items.Count == 0))
        {
            OpenSettings();
            _tray.ShowBalloonTip(4000, "HaloDock is running",
                "Add some files or folders, then rest your pointer against the left edge of the screen.", Forms.ToolTipIcon.Info);
        }
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

    // ---- Tray ----

    Forms.NotifyIcon CreateTray()
    {
        var tray = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "HaloDock " + Updater.CurrentText,
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
                ("Settings\u2026", false, OpenSettings),
                ("Check for updates\u2026", false, () => { OpenSettings(); _settingsWindow?.ShowPage("updates"); }),
                ("Pause", _watcher!.Paused, () => _watcher.Paused = !_watcher.Paused),
                (null, false, null),
                ("Exit", false, Shutdown));
        };
        tray.BalloonTipClicked += (_, _) =>
        {
            if (_pendingRelease == null) return;
            OpenSettings();
            _settingsWindow?.ShowUpdate(_pendingRelease);
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

    // ---- Settings ----

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

    void SaveQuietly()
    {
        try { _settings.Save(); }
        catch { /* still applies for this session */ }
    }

    void ApplySettings(AppSettings settings)
    {
        var updatesJustEnabled = settings.CheckForUpdates && !_settings.CheckForUpdates;
        _settings = settings;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't save settings:\n" + ex.Message, "HaloDock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (!ReferenceEquals(_menu!.Settings, settings)) _menu.Settings = settings;
        ApplyStartWithWindows(_settings.StartWithWindows);
        if (updatesJustEnabled) _ = CheckForUpdatesQuietly();
    }

    static void ApplyStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (key.GetValue("EdgeDock") != null) key.DeleteValue("EdgeDock"); // entry from before the rename
            if (enabled) key.SetValue("HaloDock", $"\"{Environment.ProcessPath}\"");
            else if (key.GetValue("HaloDock") != null) key.DeleteValue("HaloDock");
        }
        catch
        {
            // Non-fatal: the setting just won't take effect.
        }
    }

    // ---- Updates (opt-in) ----

    void StartUpdateChecks()
    {
        // Shortly after start-up, then every half hour; each check only goes to GitHub if a day has passed.
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += (_, _) =>
        {
            first.Stop();
            _ = CheckForUpdatesQuietly();
        };
        first.Start();
        var regular = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        regular.Tick += (_, _) => _ = CheckForUpdatesQuietly();
        regular.Start();
    }

    async Task CheckForUpdatesQuietly()
    {
        if (!_settings.CheckForUpdates) return;
        if (_settings.LastUpdateCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
        try
        {
            var release = await Updater.FindNewerAsync();
            _settings.LastUpdateCheck = DateTime.UtcNow;
            SaveQuietly();
            if (release == null || release.Version == _notifiedVersion) return;
            _pendingRelease = release;
            _notifiedVersion = release.Version;
            _tray?.ShowBalloonTip(8000, $"HaloDock {release.Version.ToString(3)} is available",
                "Click here to see what's new and update.", Forms.ToolTipIcon.Info);
        }
        catch
        {
            // Offline or GitHub unreachable: try again later.
        }
    }

    // ---- Launching ----

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

    // ---- Screenshots and demo (for the README) ----

    /// <summary>Pulls <c>--name value</c> out of the arguments; what's left are paths.</summary>
    static string? TakeOption(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        if (i < 0 || i + 1 >= args.Count) return null;
        var value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }

    static ScreenEdge ParseEdge(string? s) => Enum.TryParse<ScreenEdge>(s, true, out var edge) ? edge : ScreenEdge.Left;

    /// <summary>Saved settings, or (when paths are given) demo rings built from them, for screenshots.</summary>
    static AppSettings SnapshotSettings(IReadOnlyList<string> paths)
    {
        var settings = AppSettings.Load();
        if (paths.Count == 0) return settings;

        static List<LaunchItem> Existing(params string[] candidates) =>
            candidates.Where(p => File.Exists(p) || Directory.Exists(p)).Select(p => new LaunchItem { Path = p }).ToList();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sys = Environment.SystemDirectory;
        settings.Rings =
        [
            new RingPreset { Name = "Main", Color = RingPreset.Palette[0], Items = paths.Select(p => new LaunchItem { Path = p }).ToList() },
            new RingPreset
            {
                Name = "Tools", Color = RingPreset.Palette[1],
                Items = Existing(Path.Combine(sys, "Taskmgr.exe"), Path.Combine(sys, "cmd.exe"), Path.Combine(win, "regedit.exe"),
                    Path.Combine(sys, "control.exe"), Path.Combine(sys, "mstsc.exe"), Path.Combine(sys, "charmap.exe")),
            },
            new RingPreset
            {
                Name = "Media", Color = RingPreset.Palette[2],
                Items = Existing(@"C:\Users\Public\Music", @"C:\Users\Public\Pictures", @"C:\Users\Public\Videos",
                    Path.Combine(sys, "mspaint.exe"), Path.Combine(sys, "SnippingTool.exe")),
            },
            new RingPreset { Name = "Games", Color = RingPreset.Palette[3] },
        ];
        settings.ActiveRing = 0;
        settings.Radius = 190;
        settings.ItemSize = 72;
        return settings;
    }

    /// <summary><c>--snapshot out.png [hoverIndex] [--edge Left|Right|Top] [paths…]</c>: renders the wheel to a PNG.</summary>
    static void WriteSnapshot(string outPath, string[] raw)
    {
        var args = raw.ToList();
        var edge = ParseEdge(TakeOption(args, "--edge"));
        var hover = args.Count > 0 && int.TryParse(args[0], out var h) ? h : -1;
        if (args.Count > 0 && int.TryParse(args[0], out _)) args.RemoveAt(0);
        var settings = SnapshotSettings(args);

        var menu = new RadialMenuWindow(settings);
        var (w, ht) = edge == ScreenEdge.Top ? (760, 420) : (560, 560);
        SavePng(menu.RenderSnapshot(w, ht, hover, edge), outPath);
    }

    /// <summary><c>--demo outDir [--edge Left] [--fps 30]</c>: renders the demo walkthrough as numbered PNG frames.</summary>
    static void WriteDemo(string outDir, string[] raw)
    {
        var args = raw.ToList();
        var edge = ParseEdge(TakeOption(args, "--edge"));
        var fps = int.TryParse(TakeOption(args, "--fps"), out var f) ? f : 30;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var paths = args.Count > 0 ? args : new List<string>
        {
            @"C:\Users\Public\Pictures", Path.Combine(win, "notepad.exe"), Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            @"C:\Users\Public\Downloads", Path.Combine(win, "explorer.exe"), Path.Combine(Environment.SystemDirectory, "calc.exe"),
            @"C:\Users\Public\Documents", Path.Combine(win, "regedit.exe"), @"C:\Users\Public\Music",
        };

        Directory.CreateDirectory(outDir);
        var menu = new RadialMenuWindow(SnapshotSettings(paths));
        menu.RenderDemo(760, 560, fps, edge, (i, frame) => SavePng(frame, Path.Combine(outDir, $"frame_{i:D5}.png")));
    }

    static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>
    /// <c>--snapshot-settings out.png [--page rings|edges|behavior|updates] [paths…]</c>: briefly shows the settings
    /// window and screen-captures it, so it looks exactly as it does for real (title bar included).
    /// </summary>
    void WriteSettingsSnapshot(string outPath, string[] raw)
    {
        var args = raw.ToList();
        var page = TakeOption(args, "--page") ?? "rings";
        var window = new SettingsWindow(SnapshotSettings(args), MonitorInfo.GetAll()) { Topmost = true };
        window.Show();
        window.ShowPage(page);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
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
}
