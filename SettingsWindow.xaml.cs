using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace HaloDock;

public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly ObservableCollection<RingRow> _rings = new();
    readonly ObservableCollection<MonitorRow> _monitors = new();
    readonly List<SwatchRow> _swatches = RingPreset.Palette.Select(c => new SwatchRow(c)).ToList();

    public event Action<AppSettings>? Saved;

    ObservableCollection<ItemRow> CurrentItems => ((RingRow)RingPicker.SelectedItem).Items;

    public SettingsWindow(AppSettings settings, IReadOnlyList<MonitorInfo> monitors)
    {
        InitializeComponent();
        _settings = settings;

        foreach (var ring in settings.Rings) _rings.Add(new RingRow(ring));
        RingPicker.ItemsSource = _rings;
        Swatches.ItemsSource = _swatches;
        RingPicker.SelectedIndex = Math.Clamp(settings.ActiveRing, 0, _rings.Count - 1);

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

        DelaySlider.Value = settings.HoverDelayMs;
        EdgeSlider.Value = settings.EdgeWidthPx;
        RadiusSlider.Value = settings.Radius;
        SizeSlider.Value = settings.ItemSize;
        InvertScroll.IsChecked = settings.InvertScroll;
        var symbols = Enum.GetValues<RemoveSymbol>().Select(s => new SymbolRow(s)).ToList();
        SymbolPicker.ItemsSource = symbols;
        SymbolPicker.SelectedItem = symbols.First(s => s.Value == settings.RemoveSymbol);
        IgnoreFullscreen.IsChecked = settings.IgnoreFullscreen;
        StartWithWindows.IsChecked = settings.StartWithWindows;
    }

    // ---- Rings ----

    void RingPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RingPicker.SelectedItem is not RingRow ring) return;
        ItemsList.ItemsSource = ring.Items;
        foreach (var s in _swatches) s.IsSelected = string.Equals(s.Color, ring.Color, StringComparison.OrdinalIgnoreCase);
    }

    void NewRing_Click(object sender, RoutedEventArgs e)
    {
        // Next colour nobody is using yet, so the dots stay distinguishable.
        var used = _rings.Select(r => r.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var color = RingPreset.Palette.FirstOrDefault(c => !used.Contains(c)) ?? RingPreset.Palette[_rings.Count % RingPreset.Palette.Length];
        var ring = new RingRow(new RingPreset { Name = $"Ring {_rings.Count + 1}", Color = color });
        _rings.Add(ring);
        RingPicker.SelectedItem = ring;
        RingName.Focus();
        RingName.SelectAll();
    }

    void DeleteRing_Click(object sender, RoutedEventArgs e)
    {
        if (RingPicker.SelectedItem is not RingRow ring) return;
        if (_rings.Count == 1)
        {
            MessageBox.Show(this, "You need at least one ring.", "HaloDock");
            return;
        }
        if (ring.Items.Count > 0 &&
            MessageBox.Show(this, $"Delete \"{ring.Name}\" and its {ring.Items.Count} item(s)?", "HaloDock",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var index = _rings.IndexOf(ring);
        _rings.Remove(ring);
        RingPicker.SelectedIndex = Math.Min(index, _rings.Count - 1);
    }

    void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SwatchRow swatch } || RingPicker.SelectedItem is not RingRow ring) return;
        ring.Color = swatch.Color;
        foreach (var s in _swatches) s.IsSelected = s == swatch;
    }

    // ---- Items ----

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (MonitorList != null) MonitorList.IsEnabled = ModeSelected.IsChecked == true;
    }

    void ItemsList_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking into a rename box should also select its row, so Remove/Move act on it.
        if (e.NewFocus is not TextBox { DataContext: ItemRow row } || ItemsList.SelectedItems.Contains(row)) return;
        ItemsList.SelectedItems.Clear();
        ItemsList.SelectedItems.Add(row);
    }

    void AddPaths(IEnumerable<string> paths)
    {
        var items = CurrentItems;
        ItemRow? last = null;
        foreach (var path in paths)
        {
            if (items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            last = new ItemRow(path, "");
            items.Add(last);
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

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ItemsList.SelectedItems.Cast<ItemRow>().ToList()) CurrentItems.Remove(row);
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    void MoveSelected(int direction)
    {
        var items = CurrentItems;
        var selected = ItemsList.SelectedItems.Cast<ItemRow>().ToList();
        if (selected.Count == 0) return;
        var indices = selected.Select(items.IndexOf).OrderBy(i => i).ToList();
        if (direction > 0) indices.Reverse();
        foreach (var i in indices)
        {
            var target = i + direction;
            if (target < 0 || target >= items.Count) return; // block already at the end
            items.Move(i, target);
        }
        foreach (var row in selected) ItemsList.SelectedItems.Add(row);
    }

    void ItemsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void ItemsList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Rings = _rings.Select(r => r.ToPreset()).ToList();
        _settings.ActiveRing = Math.Clamp(_settings.ActiveRing, 0, _settings.Rings.Count - 1);

        _settings.MonitorMode = ModePrimary.IsChecked == true ? MonitorMode.Primary
            : ModeSelected.IsChecked == true ? MonitorMode.Selected
            : MonitorMode.All;
        _settings.SelectedMonitors = _monitors.Where(m => m.IsChecked).Select(m => m.DeviceName).ToList();
        _settings.OuterEdgesOnly = OuterOnly.IsChecked == true;

        _settings.HoverDelayMs = (int)DelaySlider.Value;
        _settings.EdgeWidthPx = (int)EdgeSlider.Value;
        _settings.Radius = RadiusSlider.Value;
        _settings.ItemSize = SizeSlider.Value;
        _settings.InvertScroll = InvertScroll.IsChecked == true;
        _settings.RemoveSymbol = (SymbolPicker.SelectedItem as SymbolRow)?.Value ?? RemoveSymbol.Minus;
        _settings.IgnoreFullscreen = IgnoreFullscreen.IsChecked == true;
        _settings.StartWithWindows = StartWithWindows.IsChecked == true;

        if (_settings.MonitorMode == MonitorMode.Selected && _settings.SelectedMonitors.Count == 0)
        {
            MessageBox.Show(this, "Pick at least one monitor, or choose \"All monitors\".", "HaloDock");
            return;
        }

        Saved?.Invoke(_settings);
        Close();
    }
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
    string _name;

    public ItemRow(string path, string name)
    {
        Path = path;
        _name = name.Length > 0 ? name : LaunchItem.DefaultName(path);
        Icon = ShellIcons.Get(path, 64);
        MissingVisibility = File.Exists(path) || Directory.Exists(path) ? Visibility.Collapsed : Visibility.Visible;
    }

    public string Path { get; }
    public ImageSource? Icon { get; }
    public Visibility MissingVisibility { get; }

    public string Name
    {
        get => _name;
        set { _name = value; Raise(nameof(Name)); }
    }
}

public sealed class SymbolRow(RemoveSymbol value)
{
    public RemoveSymbol Value { get; } = value;
    public string Glyph { get; } = RemoveSymbols.Glyph(value);
    public string Label { get; } = RemoveSymbols.Label(value);
}

public sealed class MonitorRow(string deviceName, string label)
{
    public string DeviceName { get; } = deviceName;
    public string Label { get; } = label;
    public bool IsChecked { get; set; }
}
