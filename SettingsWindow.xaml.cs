using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace HaloDock;

/// <summary>
/// Settings, applied as you go: every change is saved a moment after you make it, so there's no Save button.
/// </summary>
public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly ObservableCollection<RingRow> _rings = new();
    readonly ObservableCollection<MonitorRow> _monitors = new();
    readonly List<SwatchRow> _swatches = RingPreset.Palette.Select(c => new SwatchRow(c)).ToList();
    readonly List<SymbolRow> _symbols;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    bool _loading = true;
    bool _showingRing;
    Point _dragStart;
    ItemRow? _dragRow;
    Updater.Release? _available;

    /// <summary>Raised (debounced) whenever something changes, with the up-to-date settings.</summary>
    public event Action<AppSettings>? Saved;

    RingRow? CurrentRing => RingList.SelectedItem as RingRow;

    public SettingsWindow(AppSettings settings, IReadOnlyList<MonitorInfo> monitors)
    {
        InitializeComponent();
        _settings = settings;
        ApplyTheme(Theme.IsDark);
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) => Theme.Changed -= OnThemeChanged;
        SourceInitialized += (_, _) => ApplyTitleBar(Theme.IsDark);

        var version = Updater.CurrentText;
        Title = $"HaloDock {version}";
        VersionText.Text = "Version " + version;
        UpdatesVersion.Text = $"You're running HaloDock {version}.";

        // Rings
        ItemRow.Symbol = settings.RemoveSymbol;
        LoadRings(settings.Rings, settings.ActiveRing);
        Swatches.ItemsSource = _swatches;

        // Screen edges
        EdgeLeft.IsChecked = settings.Edges.Contains(ScreenEdge.Left);
        EdgeRight.IsChecked = settings.Edges.Contains(ScreenEdge.Right);
        EdgeTop.IsChecked = settings.Edges.Contains(ScreenEdge.Top);
        UpdateEdgeSummary();
        foreach (var m in monitors)
        {
            var isChecked = settings.SelectedMonitors.Contains(m.DeviceName, StringComparer.OrdinalIgnoreCase)
                            || (settings.SelectedMonitors.Count == 0 && m.Primary);
            _monitors.Add(new MonitorRow(m.DeviceName, m.Label) { IsChecked = isChecked });
        }
        MonitorList.ItemsSource = _monitors;
        (settings.MonitorMode switch
        {
            MonitorMode.Primary => ModePrimary,
            MonitorMode.Selected => ModeSelected,
            _ => ModeAll,
        }).IsChecked = true;
        MonitorList.IsEnabled = settings.MonitorMode == MonitorMode.Selected;
        OuterOnly.IsChecked = settings.OuterEdgesOnly;

        // Behavior
        DelaySlider.Value = settings.HoverDelayMs;
        EdgeSlider.Value = settings.EdgeWidthPx;
        RadiusSlider.Value = settings.Radius;
        SizeSlider.Value = settings.ItemSize;
        _symbols = Enum.GetValues<RemoveSymbol>().Select(s => new SymbolRow(s) { IsSelected = s == settings.RemoveSymbol }).ToList();
        SymbolList.ItemsSource = _symbols;
        InvertScroll.IsChecked = settings.InvertScroll;
        IgnoreFullscreen.IsChecked = settings.IgnoreFullscreen;
        StartWithWindows.IsChecked = settings.StartWithWindows;

        // Updates
        CheckForUpdates.IsChecked = settings.CheckForUpdates;
        if (settings.LastUpdateCheck is { } last) UpdateStatus.Text = $"Last checked {last.ToLocalTime():g}.";

        _saveTimer.Tick += (_, _) => Commit();
        Closing += (_, _) => { if (_saveTimer.IsEnabled) Commit(); };
        _loading = false;
    }

    // ---- Theme ----

    void OnThemeChanged() => Dispatcher.BeginInvoke(() =>
    {
        ApplyTheme(Theme.IsDark);
        ApplyTitleBar(Theme.IsDark);
    });

    void ApplyTheme(bool dark)
    {
        void Set(string key, uint argb) => Resources[key] = Brush(argb);
        Set("WinBg", dark ? 0xFF1C1C1F : 0xFFF4F4F6);
        Set("NavBg", dark ? 0xFF232327 : 0xFFEBEBEF);
        Set("CardBg", dark ? 0xFF2A2A2F : 0xFFFFFFFF);
        Set("CardBorder", dark ? 0xFF3A3A40 : 0xFFDCDCE2);
        Set("Hover", dark ? 0xFF33333A : 0xFFEDEDF2);
        Set("Pressed", dark ? 0xFF3C3C44 : 0xFFE1E1E8);
        Set("TextMain", dark ? 0xFFF2F2F4 : 0xFF1A1A1C);
        Set("TextDim", dark ? 0xFFA2A2AC : 0xFF62626C);
        Set("Accent", dark ? 0xFF4C9AFF : 0xFF2F6FEB);
        Set("AccentHover", dark ? 0xFF66AAFF : 0xFF4A82F0);
        Set("AccentSoft", dark ? 0x334C9AFFu : 0x222F6FEBu);
        Set("InputBg", dark ? 0xFF1F1F23 : 0xFFFFFFFF);
        Set("TrackBg", dark ? 0xFF4A4A52 : 0xFFC8C8D0);
        Set("Danger", 0xFFE5484D);
    }

    static SolidColorBrush Brush(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    void ApplyTitleBar(bool dark)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    // ---- Navigation ----

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageRings == null) return; // during InitializeComponent
        PageRings.Visibility = NavRings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageEdges.Visibility = NavEdges.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageBehavior.Visibility = NavBehavior.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageUpdates.Visibility = NavUpdates.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Jumps to a page by name: rings, edges, behavior or updates.</summary>
    public void ShowPage(string page)
    {
        (page.ToLowerInvariant() switch
        {
            "edges" => NavEdges,
            "behavior" => NavBehavior,
            "updates" => NavUpdates,
            _ => NavRings,
        }).IsChecked = true;
    }

    void RepoLink_Click(object sender, RoutedEventArgs e) => Updater.OpenReleasesPage("https://github.com/" + Updater.Repo);

    // ---- Saving ----

    void Changed(object sender, RoutedEventArgs e)
    {
        if (sender == ModeSelected || sender == ModeAll || sender == ModePrimary) return;
        ScheduleSave();
    }

    void SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ScheduleSave();

    void ScheduleSave()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void Commit()
    {
        _saveTimer.Stop();
        var s = _settings;
        s.Rings = _rings.Select(r => r.ToPreset()).ToList();
        s.ActiveRing = Math.Clamp(s.ActiveRing, 0, s.Rings.Count - 1);

        s.Edges = new List<ScreenEdge>();
        if (EdgeLeft.IsChecked == true) s.Edges.Add(ScreenEdge.Left);
        if (EdgeRight.IsChecked == true) s.Edges.Add(ScreenEdge.Right);
        if (EdgeTop.IsChecked == true) s.Edges.Add(ScreenEdge.Top);
        s.MonitorMode = ModePrimary.IsChecked == true ? MonitorMode.Primary
            : ModeSelected.IsChecked == true ? MonitorMode.Selected
            : MonitorMode.All;
        s.SelectedMonitors = _monitors.Where(m => m.IsChecked).Select(m => m.DeviceName).ToList();
        s.OuterEdgesOnly = OuterOnly.IsChecked == true;

        s.HoverDelayMs = (int)DelaySlider.Value;
        s.EdgeWidthPx = (int)EdgeSlider.Value;
        s.Radius = RadiusSlider.Value;
        s.ItemSize = SizeSlider.Value;
        s.RemoveSymbol = _symbols.FirstOrDefault(x => x.IsSelected)?.Value ?? RemoveSymbol.Unpin;
        s.InvertScroll = InvertScroll.IsChecked == true;
        s.IgnoreFullscreen = IgnoreFullscreen.IsChecked == true;
        s.StartWithWindows = StartWithWindows.IsChecked == true;
        s.CheckForUpdates = CheckForUpdates.IsChecked == true;

        Saved?.Invoke(s);
    }

    // ---- Rings ----

    void LoadRings(IEnumerable<RingPreset> rings, int select)
    {
        foreach (var r in _rings) Unwatch(r);
        _rings.Clear();
        foreach (var ring in rings)
        {
            var row = new RingRow(ring);
            Watch(row);
            _rings.Add(row);
        }
        RingList.ItemsSource = _rings;
        RingList.SelectedIndex = Math.Clamp(select, 0, _rings.Count - 1);
    }

    /// <summary>The wheel changed a ring (rearranged, added, unpinned): show that here too.</summary>
    public void ReloadRings(AppSettings latest)
    {
        _saveTimer.Stop();
        var index = RingList.SelectedIndex;
        LoadRings(latest.Rings, index);
    }

    void Watch(RingRow ring)
    {
        ring.PropertyChanged += OnRowChanged;
        ring.Items.CollectionChanged += OnItemsChanged;
        foreach (var item in ring.Items) item.PropertyChanged += OnRowChanged;
    }

    void Unwatch(RingRow ring)
    {
        ring.PropertyChanged -= OnRowChanged;
        ring.Items.CollectionChanged -= OnItemsChanged;
        foreach (var item in ring.Items) item.PropertyChanged -= OnRowChanged;
    }

    void OnRowChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (ItemRow item in e.NewItems) item.PropertyChanged += OnRowChanged;
        if (e.OldItems != null) foreach (ItemRow item in e.OldItems) item.PropertyChanged -= OnRowChanged;
        UpdateEmptyHint();
        ScheduleSave();
    }

    void RingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CurrentRing is not { } ring) return;
        _showingRing = true;
        RingName.Text = ring.Name;
        _showingRing = false;
        ItemsList.ItemsSource = ring.Items;
        foreach (var s in _swatches) s.IsSelected = string.Equals(s.Color, ring.Color, StringComparison.OrdinalIgnoreCase);
        UpdateEmptyHint();
    }

    void UpdateEmptyHint() =>
        EmptyHint.Visibility = CurrentRing is { Items.Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

    void RingName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_showingRing || CurrentRing is not { } ring) return;
        ring.Name = RingName.Text;
    }

    void NewRing_Click(object sender, RoutedEventArgs e)
    {
        // Next colour nobody is using yet, so the dots stay distinguishable.
        var used = _rings.Select(r => r.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var color = RingPreset.Palette.FirstOrDefault(c => !used.Contains(c)) ?? RingPreset.Palette[_rings.Count % RingPreset.Palette.Length];
        var ring = new RingRow(new RingPreset { Name = $"Ring {_rings.Count + 1}", Color = color });
        Watch(ring);
        _rings.Add(ring);
        RingList.SelectedItem = ring;
        RingName.Focus();
        RingName.SelectAll();
        ScheduleSave();
    }

    void DeleteRing_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRing is not { } ring) return;
        if (_rings.Count == 1)
        {
            MessageBox.Show(this, "You need at least one ring.", "HaloDock");
            return;
        }
        if (ring.Items.Count > 0 &&
            MessageBox.Show(this, $"Delete \"{ring.Name}\" and its {ring.Items.Count} item(s)? The files themselves aren't touched.",
                "HaloDock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var index = _rings.IndexOf(ring);
        Unwatch(ring);
        _rings.Remove(ring);
        RingList.SelectedIndex = Math.Min(index, _rings.Count - 1);
        ScheduleSave();
    }

    void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SwatchRow swatch } || CurrentRing is not { } ring) return;
        ring.Color = swatch.Color;
        foreach (var s in _swatches) s.IsSelected = s == swatch;
    }

    // ---- Items ----

    void AddPaths(IEnumerable<string> paths, int at = -1)
    {
        if (CurrentRing is not { } ring) return;
        var items = ring.Items;
        if (at < 0 || at > items.Count) at = items.Count;
        ItemRow? last = null;
        foreach (var path in paths)
        {
            if (items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            last = new ItemRow(path, "");
            items.Insert(at++, last);
        }
        if (last != null) ItemsList.ScrollIntoView(last);
    }

    void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, DereferenceLinks = false, Title = "Add files to HaloDock" };
        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Multiselect = true, Title = "Add folders to HaloDock" };
        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FolderNames);
    }

    void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemRow row }) CurrentRing?.Items.Remove(row);
    }

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    ItemRow? RowAt(Point position) =>
        FindAncestor<ListBoxItem>(ItemsList.InputHitTest(position) as DependencyObject)?.DataContext as ItemRow;

    void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Rows can be dragged from anywhere except the name box and the buttons.
        var source = e.OriginalSource as DependencyObject;
        _dragRow = FindAncestor<TextBox>(source) == null && FindAncestor<Button>(source) == null ? RowAt(e.GetPosition(ItemsList)) : null;
        _dragStart = e.GetPosition(ItemsList);
    }

    void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow == null || e.LeftButton != MouseButtonState.Pressed) return;
        if ((e.GetPosition(ItemsList) - _dragStart).Length < SystemParameters.MinimumVerticalDragDistance + 2) return;
        var row = _dragRow;
        _dragRow = null;
        DragDrop.DoDragDrop(ItemsList, new DataObject(typeof(ItemRow), row), DragDropEffects.Move);
    }

    void ItemsList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ItemRow)) is ItemRow row && CurrentRing is { } ring)
        {
            // Rows slide into place as you drag, like the wheel does.
            var target = RowAt(e.GetPosition(ItemsList));
            if (target != null && target != row)
            {
                var from = ring.Items.IndexOf(row);
                var to = ring.Items.IndexOf(target);
                if (from >= 0 && to >= 0) ring.Items.Move(from, to);
            }
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        }
        e.Handled = true;
    }

    void ItemsList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || CurrentRing is not { } ring) return;
        var target = RowAt(e.GetPosition(ItemsList));
        AddPaths(paths, target == null ? -1 : ring.Items.IndexOf(target));
    }

    // ---- Screen edges ----

    void Edge_Changed(object sender, RoutedEventArgs e)
    {
        if (EdgeSummary == null) return;
        UpdateEdgeSummary();
        ScheduleSave();
    }

    void UpdateEdgeSummary()
    {
        var on = new List<string>();
        if (EdgeLeft.IsChecked == true) on.Add("left");
        if (EdgeTop.IsChecked == true) on.Add("top");
        if (EdgeRight.IsChecked == true) on.Add("right");
        EdgeSummary.Text = on.Count switch
        {
            0 => "No edges: the wheel won't open",
            1 => $"Opens from the {on[0]} edge",
            _ => $"Opens from the {string.Join(", ", on.Take(on.Count - 1))} and {on[^1]} edges",
        };
    }

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (MonitorList == null) return;
        MonitorList.IsEnabled = ModeSelected.IsChecked == true;
        ScheduleSave();
    }

    // ---- Behavior ----

    void Symbol_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SymbolRow chosen }) return;
        foreach (var s in _symbols) s.IsSelected = s == chosen;
        ItemRow.Symbol = chosen.Value;
        foreach (var ring in _rings)
        foreach (var item in ring.Items) item.RefreshSymbol();
        ScheduleSave();
    }

    // ---- Updates ----

    /// <summary>Jumps to the Updates page showing a release found by the background check.</summary>
    public void ShowUpdate(Updater.Release release)
    {
        NavUpdates.IsChecked = true;
        ShowAvailable(release);
    }

    void ShowAvailable(Updater.Release release)
    {
        _available = release;
        UpdateStatus.Text = $"HaloDock {release.Version.ToString(3)} is available.";
        InstallButton.Content = Updater.CanSelfUpdate && release.ExeUrl != null ? "Download and install" : "Open download page";
        InstallButton.IsEnabled = true;
        UpdatePanel.Visibility = Visibility.Visible;
    }

    async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        UpdatePanel.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "Checking…";
        try
        {
            var release = await Updater.FindNewerAsync();
            _settings.LastUpdateCheck = DateTime.UtcNow;
            if (release == null) UpdateStatus.Text = $"You're up to date ({Updater.CurrentText}).";
            else ShowAvailable(release);
            ScheduleSave();
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Couldn't reach GitHub: " + ex.Message;
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_available is not { } release) return;
        if (!Updater.CanSelfUpdate || release.ExeUrl == null)
        {
            Updater.OpenReleasesPage(release.PageUrl);
            return;
        }

        InstallButton.IsEnabled = false;
        CheckNowButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateStatus.Text = $"Downloading {release.Version.ToString(3)}… {p:P0}");
            await Updater.InstallAsync(release, progress);
            UpdateStatus.Text = "Restarting…";
            if (_saveTimer.IsEnabled) Commit();
            Application.Current.Shutdown(); // the update script swaps the exe once we've exited, then starts it
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Update failed: " + ex.Message;
            InstallButton.IsEnabled = true;
            CheckNowButton.IsEnabled = true;
        }
    }

    void ReleaseNotes_Click(object sender, RoutedEventArgs e) => Updater.OpenReleasesPage(_available?.PageUrl);
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public sealed class RingRow : Observable
{
    string _name;
    string _color;

    public RingRow(RingPreset ring)
    {
        _name = ring.Name;
        _color = ring.Color;
        foreach (var item in ring.Items) Items.Add(new ItemRow(item.Path, item.Name ?? ""));
    }

    public ObservableCollection<ItemRow> Items { get; } = new();

    public string Name
    {
        get => _name;
        set { _name = value; Raise(nameof(Name)); }
    }

    public string Color
    {
        get => _color;
        set { _color = value; Raise(nameof(Color), nameof(Brush)); }
    }

    public Brush Brush => SwatchRow.ToBrush(_color);

    public RingPreset ToPreset() => new()
    {
        Name = string.IsNullOrWhiteSpace(_name) ? "Ring" : _name.Trim(),
        Color = _color,
        Items = Items.Select(r =>
        {
            var name = r.Name.Trim();
            return new LaunchItem
            {
                Path = r.Path,
                Name = name.Length == 0 || name == LaunchItem.DefaultName(r.Path) ? null : name,
            };
        }).ToList(),
    };
}

public sealed class SwatchRow(string color) : Observable
{
    bool _isSelected;

    public string Color { get; } = color;
    public Brush Brush { get; } = ToBrush(color);

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Raise(nameof(IsSelected), nameof(SelectedVisibility)); }
    }

    public Visibility SelectedVisibility => _isSelected ? Visibility.Visible : Visibility.Hidden;

    public static Brush ToBrush(string color)
    {
        try
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Gray;
        }
    }
}

public sealed class ItemRow : Observable
{
    /// <summary>The remove symbol currently chosen, so each row's remove button matches the wheel.</summary>
    public static RemoveSymbol Symbol { get; set; } = RemoveSymbol.Unpin;

    string _name;

    public ItemRow(string path, string name)
    {
        Path = path;
        _name = name.Length > 0 ? name : LaunchItem.DefaultName(path);
        Icon = ShellIcons.Get(path, 64);
        MissingText = File.Exists(path) || Directory.Exists(path) ? "" : "  ·  not found";
    }

    public string Path { get; }
    public ImageSource? Icon { get; }
    public string MissingText { get; }
    public string RemoveGlyph => RemoveSymbols.Glyph(Symbol);
    public string RemoveText => RemoveSymbols.RemoveText(Symbol);

    public void RefreshSymbol() => Raise(nameof(RemoveGlyph), nameof(RemoveText));

    public string Name
    {
        get => _name;
        set { _name = value; Raise(nameof(Name)); }
    }
}

public sealed class SymbolRow(RemoveSymbol value) : Observable
{
    bool _isSelected;

    public RemoveSymbol Value { get; } = value;
    public string Glyph { get; } = RemoveSymbols.Glyph(value);
    public string AddGlyph { get; } = RemoveSymbols.AddGlyph(value);
    public string Label { get; } = RemoveSymbols.Label(value);

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Raise(nameof(IsSelected)); }
    }
}

public sealed class MonitorRow(string deviceName, string label)
{
    public string DeviceName { get; } = deviceName;
    public string Label { get; } = label;
    public bool IsChecked { get; set; }
}
