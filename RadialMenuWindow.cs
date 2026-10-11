using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;

namespace EdgeDock;

/// <summary>
/// Transparent, non-activating overlay that fans the items' icons out in a half-wheel against a monitor's
/// left edge. Items shrink toward the top and bottom until they slide off behind the edge.
/// A smaller inner arc of coloured dots switches between saved rings (item sets).
/// Files dragged onto an icon are moved/copied into a folder or opened with an app.
/// </summary>
public sealed class RadialMenuWindow : Window
{
    const double AngleStepDeg = 24;
    const double MinScale = 0.5;
    const double HoverGrow = 0.25;
    const double LabelHoverGrow = 0.3;
    const double LabelMinFalloff = 0.25; // names of items squashed against the edge would overlap, so hide them
    const double DimmedOpacity = 0.55;
    const double LabelRoom = 320; // DIP to the right of the wheel reserved for names
    const double OpenSeconds = 0.14;
    const double CloseSeconds = 0.1;
    const double RingSwitchSeconds = 0.16;
    const double LeaveGraceSeconds = 0.12;

    const double InnerRadiusFactor = 0.42; // inner dot ring radius, relative to the item ring
    const double DotSize = 12;
    const double DotHoverGrow = 0.5;
    const double DotStepDeg = 20;
    const double DotMaxSpanDeg = 140;

    // Alpha 1 is invisible but still catches the mouse, so clicks on an icon's transparent pixels don't fall through.
    static readonly Brush HitArea = Frozen(new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)));

    sealed record Palette(Brush Plate, Brush TagBackground, Brush TagText);

    static readonly Palette DarkPalette = new(
        Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0x30, 0x30, 0x30))),
        Frozen(new SolidColorBrush(Color.FromArgb(0xD8, 0x20, 0x20, 0x20))),
        Brushes.White);

    static readonly Palette LightPalette = new(
        Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))),
        Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xF7, 0xF7, 0xF7))),
        Frozen(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A))));

    Palette _palette = DarkPalette;

    readonly Canvas _canvas = new();
    readonly EllipseGeometry _arcGeometry = new();
    readonly Path _arc;
    readonly Border _dotLabel;
    readonly TextBlock _dotLabelText;
    readonly List<ItemVisual> _visuals = new();
    readonly List<DotVisual> _dots = new();
    readonly Dictionary<int, int> _ringCenters = new(); // which item each ring was last centred on
    readonly MouseWheelHook _hook = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly IntPtr _hwnd;

    AppSettings _settings;
    Drawing.Rectangle _bounds;
    double _monitorScale = 1;
    int _targetWidthPx;
    int _anchorY;
    double _offset, _targetOffset, _open, _itemsIn, _outsideFor, _lastFrame, _hubDistance;
    bool _isOpen, _closing, _cursorInside, _rendering, _dragging, _contextMenuOpen;
    DragDropEffects _dragEffect;
    int _wheelAccum, _hovered = -1, _hoveredDot = -1, _ring;

    public event Action<LaunchItem>? ItemInvoked;
    public event Action<LaunchItem, string[], DragDropEffects>? FilesDropped;
    public event Action<int>? ActiveRingChanged;
    public event Action? SettingsRequested;
    public event Action? Hidden;

    public bool IsOpen => _isOpen;

    public AppSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            _ringCenters.Clear();
        }
    }

    RingPreset CurrentRing => _settings.Rings[_ring];

    double InnerRadius => _settings.Radius * InnerRadiusFactor;

    bool ShowDots => _settings.Rings.Count > 1;

    public RadialMenuWindow(AppSettings settings)
    {
        _settings = settings;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        AllowDrop = true;
        Title = "EdgeDock";

        // Thin semicircle the icons ride on. It's a full circle centred on the screen edge; the left half is off-window.
        _arc = new Path
        {
            Data = _arcGeometry,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
        };
        Panel.SetZIndex(_arc, -1);
        _canvas.Children.Add(_arc);

        _dotLabelText = new TextBlock { FontSize = 12 };
        _dotLabel = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 3),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = _dotLabelText,
        };
        Panel.SetZIndex(_dotLabel, 9000);
        _canvas.Children.Add(_dotLabel);
        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        };
        _hwnd = new WindowInteropHelper(this).EnsureHandle();

        _hook.Wheel = OnWheel;
        MouseLeftButtonUp += OnLeftClick;
        MouseRightButtonUp += OnRightClick;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => _dragging = false;
        Drop += OnDrop;
    }

    /// <summary>Opens the wheel on <paramref name="monitor"/>, centred vertically on the pointer.</summary>
    public void Open(MonitorInfo monitor, Drawing.Point cursor)
    {
        if (_isOpen) return;

        _bounds = monitor.Bounds;
        _monitorScale = monitor.Scale;
        _anchorY = cursor.Y;
        var widthDip = _settings.Radius + _settings.ItemSize * (1 + HoverGrow) + LabelRoom;
        _targetWidthPx = (int)Math.Ceiling(widthDip * monitor.Scale);

        _palette = Theme.IsDark ? DarkPalette : LightPalette;
        _ring = Math.Clamp(_settings.ActiveRing, 0, _settings.Rings.Count - 1);
        RebuildItems();
        RebuildDots();
        _open = 0;
        _itemsIn = 1;
        _outsideFor = 0;
        _wheelAccum = 0;
        _hovered = -1;
        _hoveredDot = -1;
        _dragging = false;
        _closing = false;
        _isOpen = true;

        Place();
        LayoutFromWindow(0); // render the collapsed state before the window becomes visible
        Show();
        Place();

        _cursorInside = true;
        _hook.Install();
        _lastFrame = _clock.Elapsed.TotalSeconds;
        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
    }

    void FinishHide()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
        _hook.Uninstall();
        Hide();
        _isOpen = false;
        _closing = false;
        RememberCenter();
        Hidden?.Invoke();
    }

    void RememberCenter()
    {
        if (_visuals.Count > 0) _ringCenters[_ring] = (int)Math.Round(_targetOffset);
    }

    void Place() =>
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            _bounds.Left, _bounds.Top, _targetWidthPx, _bounds.Height, NativeMethods.SWP_NOACTIVATE);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF resizes the window to Windows' suggested rect on a DPI change; put it back where we want it.
        if (_isOpen) Dispatcher.BeginInvoke(Place);
    }

    void SwitchRing(int ring)
    {
        ring = Math.Clamp(ring, 0, _settings.Rings.Count - 1);
        if (ring == _ring) return;
        RememberCenter();
        _ring = ring;
        _settings.ActiveRing = ring;
        RebuildItems();
        UpdateDotStyles();
        _hovered = -1;
        _itemsIn = 0; // new items pop in
        ActiveRingChanged?.Invoke(ring);
    }

    void RebuildItems()
    {
        _arc.Stroke = Frozen(new SolidColorBrush(RingColor(CurrentRing, 0xC0)));

        foreach (var v in _visuals)
        {
            _canvas.Children.Remove(v.Root);
            _canvas.Children.Remove(v.Label);
        }
        _visuals.Clear();

        var size = _settings.ItemSize;
        var iconPx = (int)Math.Min(256, Math.Ceiling(size * (1 + HoverGrow) * Math.Max(1, _monitorScale)));
        var items = CurrentRing.Items;

        if (items.Count == 0)
        {
            _visuals.Add(CreateVisual(null, size, null));
        }
        else
        {
            foreach (var item in items)
                _visuals.Add(CreateVisual(item, size, ShellIcons.Get(item.Path, iconPx)));
        }

        foreach (var v in _visuals)
        {
            _canvas.Children.Add(v.Root);
            _canvas.Children.Add(v.Label);
        }

        _targetOffset = _offset = Math.Clamp(_ringCenters.GetValueOrDefault(_ring), 0, Math.Max(0, _visuals.Count - 1));
    }

    void RebuildDots()
    {
        foreach (var d in _dots) _canvas.Children.Remove(d.Shape);
        _dots.Clear();
        _dotLabel.Background = _palette.TagBackground;
        _dotLabelText.Foreground = _palette.TagText;
        if (!ShowDots) return;

        foreach (var ring in _settings.Rings)
        {
            var scale = new ScaleTransform();
            var shape = new Ellipse
            {
                Width = DotSize,
                Height = DotSize,
                Fill = Frozen(new SolidColorBrush(RingColor(ring, 0xFF))),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
            };
            Panel.SetZIndex(shape, 500);
            _dots.Add(new DotVisual(ring, shape, scale));
            _canvas.Children.Add(shape);
        }
        UpdateDotStyles();
    }

    void UpdateDotStyles()
    {
        for (var i = 0; i < _dots.Count; i++)
        {
            var active = i == _ring;
            _dots[i].Shape.Stroke = active ? _palette.TagText : null;
            _dots[i].Shape.StrokeThickness = active ? 2 : 0;
        }
    }

    static Color RingColor(RingPreset ring, byte alpha)
    {
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(ring.Color); }
        catch { c = (Color)ColorConverter.ConvertFromString(RingPreset.Palette[0]); }
        c.A = alpha;
        return c;
    }

    ItemVisual CreateVisual(LaunchItem? item, double size, ImageSource? icon)
    {
        var plate = new Border { CornerRadius = new CornerRadius(size * 0.22), Background = _palette.Plate, Opacity = 0 };

        UIElement content;
        if (icon != null)
        {
            var image = new Image { Source = icon, Stretch = Stretch.Uniform, Margin = new Thickness(size * 0.12) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            content = image;
        }
        else
        {
            // Empty ring: a "+" that opens settings. Otherwise the item's initial if Windows has no icon for it.
            var text = item == null ? "+" : item.DisplayName.Length > 0 ? item.DisplayName[..1].ToUpperInvariant() : "?";
            plate.Opacity = 1;
            content = new TextBlock
            {
                Text = text,
                Foreground = _palette.TagText,
                FontSize = size * 0.45,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var scale = new ScaleTransform();
        var root = new Grid
        {
            Width = size,
            Height = size,
            Background = HitArea,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
            Children = { plate, content },
        };

        var labelText = new TextBlock
        {
            Text = item?.DisplayName ?? "Add items…",
            Foreground = _palette.TagText,
            FontSize = 12,
            MaxWidth = 240,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var labelScale = new ScaleTransform();
        var label = new Border
        {
            Background = _palette.TagBackground,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 3),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0, 0.5), // grow to the right, away from the icon
            RenderTransform = labelScale,
            Child = labelText,
        };

        return new ItemVisual(item, root, plate, scale, label, labelText, labelScale, item == null || item.Exists, icon == null);
    }

    void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(0.05, now - _lastFrame);
        _lastFrame = now;
        if (dt <= 0) return; // Rendering can fire more than once per frame

        if (_closing)
        {
            _open -= dt / CloseSeconds;
            if (_open <= 0)
            {
                _open = 0;
                FinishHide();
                return;
            }
        }
        else
        {
            _open = Math.Min(1, _open + dt / OpenSeconds);
        }
        _itemsIn = Math.Min(1, _itemsIn + dt / RingSwitchSeconds);

        LayoutFromWindow(dt);

        if (!_closing && !_contextMenuOpen)
        {
            _outsideFor = _cursorInside ? 0 : _outsideFor + dt;
            if (_outsideFor > LeaveGraceSeconds) _closing = true;
        }
    }

    /// <summary>Lays out using the live window position and pointer.</summary>
    void LayoutFromWindow(double dt)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        NativeMethods.GetWindowRect(_hwnd, out var rc);
        NativeMethods.GetCursorPos(out var p);

        var cursor = new Point((p.X - rc.Left) / dpi.DpiScaleX, (p.Y - rc.Top) / dpi.DpiScaleY);
        var height = (rc.Bottom - rc.Top) / dpi.DpiScaleY;
        var anchor = (_anchorY - rc.Top) / dpi.DpiScaleY;
        Layout(dt, cursor, height, anchor, _bounds.Contains(p.X, p.Y));
    }

    void Layout(double dt, Point cursor, double height, double anchorY, bool onMonitor)
    {
        var radius = _settings.Radius;
        var itemSize = _settings.ItemSize;

        // Hub on the left edge at the pointer's height, nudged inward so the wheel isn't clipped by the screen.
        var margin = Math.Min(height / 2, radius * 0.6);
        var cy = Math.Clamp(anchorY, margin, height - margin);

        var ease = 1 - Math.Pow(1 - _open, 3);
        var itemsEase = 1 - Math.Pow(1 - _itemsIn, 3);
        var follow = dt > 0 ? 1 - Math.Exp(-dt * 16) : 1;
        _offset += (_targetOffset - _offset) * follow;
        if (Math.Abs(_targetOffset - _offset) < 0.001) _offset = _targetOffset;

        _arcGeometry.Center = new Point(0, cy);
        _arcGeometry.RadiusX = _arcGeometry.RadiusY = radius * ease;
        _arc.Opacity = ease;

        var hubDy = cursor.Y - cy;
        _hubDistance = Math.Sqrt(cursor.X * cursor.X + hubDy * hubDy);

        // Inner ring: one dot per saved ring.
        var hoveredDot = -1;
        if (_dots.Count > 0)
        {
            var dotStep = Math.Min(DotStepDeg, DotMaxSpanDeg / Math.Max(1, _dots.Count - 1)) * Math.PI / 180;
            var rIn = InnerRadius * ease;
            var bestDot = double.MaxValue;
            for (var i = 0; i < _dots.Count; i++)
            {
                var d = _dots[i];
                var angle = (i - (_dots.Count - 1) / 2.0) * dotStep;
                d.X = rIn * Math.Cos(angle);
                d.Y = cy + rIn * Math.Sin(angle);
                var dx = cursor.X - d.X;
                var dy = cursor.Y - d.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= DotSize * (1 + DotHoverGrow * d.Hover) / 2 + 4 && distance < bestDot)
                {
                    bestDot = distance;
                    hoveredDot = i;
                }
            }
        }

        // Outer ring: place items on the arc and find which one the pointer is over.
        var step = AngleStepDeg * Math.PI / 180;
        var limit = Math.PI / 2 + step * 0.6;
        var hovered = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _visuals.Count; i++)
        {
            var v = _visuals[i];
            var angle = (i - _offset) * step;
            v.Visible = Math.Abs(angle) <= limit;
            if (!v.Visible) continue;

            v.Falloff = Math.Max(0, Math.Cos(angle)); // 1 pointing straight out, 0 flat against the edge
            var r = radius * ease;
            v.X = r * Math.Cos(angle);
            v.Y = cy + r * Math.Sin(angle);
            v.BaseScale = (MinScale + (1 - MinScale) * v.Falloff) * (0.6 + 0.4 * ease) * (0.7 + 0.3 * itemsEase);

            var dx = cursor.X - v.X;
            var dy = cursor.Y - v.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var hitRadius = itemSize * v.BaseScale * (1 + HoverGrow * v.Hover) / 2;
            if (distance <= hitRadius && distance < bestDistance)
            {
                bestDistance = distance;
                hovered = i;
            }
        }
        if (_contextMenuOpen) return; // keep the right-clicked item highlighted while its menu is up
        _hovered = _closing || hoveredDot >= 0 ? -1 : hovered;
        _hoveredDot = _closing ? -1 : hoveredDot;

        _cursorInside = onMonitor && _hubDistance <= radius + itemSize * (0.5 + HoverGrow) + 16;

        var hoverFollow = dt > 0 ? 1 - Math.Exp(-dt * 22) : 1;
        LayoutDots(ease, hoverFollow);

        for (var i = 0; i < _visuals.Count; i++)
        {
            var v = _visuals[i];
            v.Hover += ((i == _hovered ? 1 : 0) - v.Hover) * hoverFollow;
            var dimTarget = _hovered >= 0 && i != _hovered ? 1 : 0;
            v.Dim += (dimTarget - v.Dim) * hoverFollow;

            if (!v.Visible)
            {
                v.Root.Visibility = Visibility.Collapsed;
                v.Label.Visibility = Visibility.Collapsed;
                continue;
            }

            var s = v.BaseScale * (1 + HoverGrow * v.Hover);
            var z = i == _hovered ? 2000 : (int)(v.Falloff * 1000);
            var opacity = ease
                          * itemsEase
                          * (0.35 + 0.65 * Math.Clamp(v.Falloff * 1.6, 0, 1))
                          * (1 - (1 - DimmedOpacity) * v.Dim)
                          * (v.Exists ? 1 : 0.4);

            v.Root.Visibility = Visibility.Visible;
            Canvas.SetLeft(v.Root, v.X - itemSize / 2);
            Canvas.SetTop(v.Root, v.Y - itemSize / 2);
            Panel.SetZIndex(v.Root, z);
            v.ScaleTransform.ScaleX = v.ScaleTransform.ScaleY = s;
            if (!v.AlwaysPlate) v.Plate.Opacity = v.Hover;
            v.Root.Opacity = opacity;

            LayoutLabel(v, i == _hovered, itemSize * s, z, opacity);
        }
    }

    void LayoutDots(double ease, double hoverFollow)
    {
        for (var i = 0; i < _dots.Count; i++)
        {
            var d = _dots[i];
            d.Hover += ((i == _hoveredDot ? 1 : 0) - d.Hover) * hoverFollow;
            var s = (0.5 + 0.5 * ease) * (1 + DotHoverGrow * d.Hover) * (i == _ring ? 1.15 : 1);
            Canvas.SetLeft(d.Shape, d.X - DotSize / 2);
            Canvas.SetTop(d.Shape, d.Y - DotSize / 2);
            d.Scale.ScaleX = d.Scale.ScaleY = s;
            d.Shape.Opacity = ease * (i == _ring || i == _hoveredDot ? 1 : 0.7);
        }

        if (_hoveredDot < 0 || ease < 0.5)
        {
            _dotLabel.Visibility = Visibility.Collapsed;
            return;
        }

        var hovered = _dots[_hoveredDot];
        if (_dotLabelText.Text != hovered.Ring.Name) _dotLabelText.Text = hovered.Ring.Name;
        _dotLabel.Visibility = Visibility.Visible;
        _dotLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_dotLabel, hovered.X + DotSize * (1 + DotHoverGrow) / 2 + 6);
        Canvas.SetTop(_dotLabel, hovered.Y - _dotLabel.DesiredSize.Height / 2);
        _dotLabel.Opacity = Math.Min(1, hovered.Hover * 1.5);
    }

    void LayoutLabel(ItemVisual v, bool hovered, double iconSize, int z, double opacity)
    {
        if (v.Falloff < LabelMinFalloff && v.Hover < 0.01)
        {
            v.Label.Visibility = Visibility.Collapsed;
            return;
        }

        var text = v.Item == null ? "Add items…" : v.Item.DisplayName;
        if (hovered && _dragging && v.Item != null) text = DropTargets.Describe(v.Item, _dragEffect);
        if (v.LabelText.Text != text) v.LabelText.Text = text;

        v.Label.Visibility = Visibility.Visible;
        v.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(v.Label, v.X + iconSize / 2 + 8);
        Canvas.SetTop(v.Label, v.Y - v.Label.DesiredSize.Height / 2);
        Panel.SetZIndex(v.Label, z + 3000); // names sit above every icon
        var ls = (0.85 + 0.15 * v.Falloff) * (1 + LabelHoverGrow * v.Hover);
        v.LabelScale.ScaleX = v.LabelScale.ScaleY = ls;
        v.Label.Opacity = opacity;
    }

    bool OnWheel(int delta)
    {
        if (!_isOpen || _closing || !_cursorInside || _visuals.Count == 0) return false;
        if (_settings.InvertScroll) delta = -delta;
        _wheelAccum += delta;
        var steps = _wheelAccum / 120; // touchpads send small deltas; accumulate to whole notches
        if (steps == 0) return true;
        _wheelAccum -= steps * 120;

        // Scrolling between the hub and the icons flips through rings; anywhere else turns the item wheel.
        var ringZone = (InnerRadius + _settings.Radius - _settings.ItemSize / 2) / 2;
        if (ShowDots && _hubDistance < ringZone) SwitchRing(_ring - steps);
        else _targetOffset = Math.Clamp(Math.Round(_targetOffset) - steps, 0, _visuals.Count - 1);
        return true;
    }

    void OnLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;
        if (_hoveredDot >= 0)
        {
            SwitchRing(_hoveredDot);
            return;
        }
        if (_hovered < 0) return;
        var item = _visuals[_hovered].Item;
        _targetOffset = _hovered; // re-open centred on what was just used
        _closing = true;
        if (item == null) SettingsRequested?.Invoke();
        else ItemInvoked?.Invoke(item);
    }

    void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_closing || _contextMenuOpen || _hovered < 0 || _visuals[_hovered].Item is not { } item || !item.Exists) return;
        e.Handled = true;

        // TrackPopupMenu runs a modal loop; rendering keeps going, so freeze hover/close until it returns.
        _contextMenuOpen = true;
        NativeMethods.GetCursorPos(out var p);
        var invoked = ShellContextMenu.Show(item.Path, p.X, p.Y);
        _contextMenuOpen = false;

        if (invoked)
        {
            _targetOffset = _hovered;
            _closing = true;
        }
        else
        {
            _outsideFor = 0; // dismissed: stay open if the pointer is still on the wheel
        }
    }

    DragDropEffects DropEffectAt(DragEventArgs e, out LaunchItem? item, out string[] files)
    {
        item = null;
        files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        if (_closing || _hovered < 0 || files.Length == 0) return DragDropEffects.None;
        item = _visuals[_hovered].Item;
        return item == null ? DragDropEffects.None : DropTargets.EffectFor(item, files, e.KeyStates, e.AllowedEffects);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        _dragging = true;
        _dragEffect = DropEffectAt(e, out _, out _);
        e.Effects = _dragEffect;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        _dragging = false;
        var effect = DropEffectAt(e, out var item, out var files);
        // We do moves ourselves, so report "none" back: a source told "move" would delete the originals itself.
        e.Effects = effect == DragDropEffects.Move ? DragDropEffects.None : effect;
        e.Handled = true;
        if (item == null || effect == DragDropEffects.None) return;

        _targetOffset = _hovered;
        _closing = true;
        FilesDropped?.Invoke(item, files, effect);
    }

    /// <summary>
    /// Debug aid (<c>EdgeDock.exe --snapshot out.png [hoverIndex]</c>): renders the fully-open wheel
    /// over a plain background without touching the screen. A negative index -N hovers ring dot N-1.
    /// </summary>
    public BitmapSource RenderSnapshot(int width, int height, int hoverIndex)
    {
        _palette = Theme.IsDark ? DarkPalette : LightPalette;
        _ring = Math.Clamp(_settings.ActiveRing, 0, _settings.Rings.Count - 1);
        _ringCenters[_ring] = (CurrentRing.Items.Count - 1) / 2;
        RebuildItems();
        RebuildDots();
        _open = 1;
        _itemsIn = 1;
        var cy = height / 2.0;

        // Aim the pointer at the requested item or dot, then let the hover animation settle.
        Layout(0, new Point(-1000, -1000), height, cy, true);
        var cursor = new Point(-1000, -1000);
        if (hoverIndex >= 0 && hoverIndex < _visuals.Count) cursor = new Point(_visuals[hoverIndex].X, _visuals[hoverIndex].Y);
        else if (hoverIndex < -1 && -hoverIndex - 2 < _dots.Count) cursor = new Point(_dots[-hoverIndex - 2].X, _dots[-hoverIndex - 2].Y);
        for (var i = 0; i < 30; i++) Layout(1 / 60.0, cursor, height, cy, true);

        var root = new Grid
        {
            Width = width,
            Height = height,
            Background = new LinearGradientBrush(Color.FromRgb(0x3A, 0x5A, 0x7A), Color.FromRgb(0xC8, 0xC2, 0xB0), 0),
        };
        Content = null;
        root.Children.Add(_canvas);
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        root.Children.Remove(_canvas);
        Content = _canvas;
        return bmp;
    }

    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    sealed class DotVisual(RingPreset ring, Ellipse shape, ScaleTransform scale)
    {
        public RingPreset Ring { get; } = ring;
        public Ellipse Shape { get; } = shape;
        public ScaleTransform Scale { get; } = scale;
        public double X { get; set; }
        public double Y { get; set; }
        public double Hover { get; set; }
    }

    sealed class ItemVisual(
        LaunchItem? item, Grid root, Border plate, ScaleTransform scaleTransform,
        Border label, TextBlock labelText, ScaleTransform labelScale, bool exists, bool alwaysPlate)
    {
        public LaunchItem? Item { get; } = item;
        public Grid Root { get; } = root;
        public Border Plate { get; } = plate;
        public ScaleTransform ScaleTransform { get; } = scaleTransform;
        public Border Label { get; } = label;
        public TextBlock LabelText { get; } = labelText;
        public ScaleTransform LabelScale { get; } = labelScale;
        public bool Exists { get; } = exists;
        public bool AlwaysPlate { get; } = alwaysPlate;
        public bool Visible { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Falloff { get; set; }
        public double BaseScale { get; set; }
        public double Hover { get; set; }
        public double Dim { get; set; }
    }
}
