using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;

namespace HaloDock;

/// <summary>
/// Transparent, non-activating overlay that fans the items' icons out in a half-wheel against a monitor's
/// left edge. Items shrink toward the top and bottom until they slide off behind the edge.
/// A smaller inner arc of coloured dots switches between saved rings (item sets).
///
/// Editing works like a phone home screen: long-press (or press and drag) an icon to pick it up and the
/// others part to show where it will land. Files dragged in from Explorer can be dropped onto a folder/app
/// icon (move/copy/open with) or into a gap between icons to add them to the ring.
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

    const double LongPressSeconds = 0.35;
    const double DragStartDistance = 8;    // DIP the pointer can move before a press becomes a drag
    const double DropIntoFraction = 0.42;  // within this much of an icon's size from its centre = drop onto it
    const double AutoScrollAngleDeg = 62;  // dragging past this angle scrolls the wheel
    const double AutoScrollSeconds = 0.35;

    const double InnerRadiusFactor = 0.42; // inner dot ring radius, relative to the item ring
    const double DotSize = 12;
    const double DotHoverGrow = 0.5;
    const double DotStepDeg = 20;
    const double DotMaxSpanDeg = 140;

    // Alpha 1 is invisible but still catches the mouse (and drag-and-drop), so nothing falls through the wheel.
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
    readonly EllipseGeometry _hitGeometry = new();
    readonly Path _hitDisk;
    readonly EllipseGeometry _arcGeometry = new();
    readonly Path _arc;
    readonly Border _dotLabel;
    readonly TextBlock _dotLabelText;
    readonly Border _gapLabel;
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
    bool _isOpen, _closing, _cursorInside, _rendering, _contextMenuOpen;
    int _wheelAccum, _hovered = -1, _hoveredDot = -1, _ring;
    Point _cursor;
    double _cursorSlot;

    // Pressing/rearranging an icon.
    int _pressIndex = -1, _liftedIndex = -1;
    double _pressTime;
    Point _pressPoint;

    // Files dragged in from outside.
    bool _dragging;
    string[] _dragFiles = [];
    DragDropKeyStates _dragKeys;
    DragDropEffects _dragAllowed;

    // Where a drag would land: a gap at this position in the ring, or onto this item.
    int _gap = -1, _dropInto = -1;
    double _lastAutoScroll;

    public event Action<LaunchItem>? ItemInvoked;
    public event Action<LaunchItem, string[], DragDropEffects>? FilesDropped;
    public event Action<int>? ActiveRingChanged;
    /// <summary>Items were added, removed or rearranged from inside the menu.</summary>
    public event Action? RingEdited;
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

    bool Busy => _liftedIndex >= 0 || _dragging;

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
        Title = "HaloDock";

        // Invisible disk covering the whole wheel, so gaps between icons still receive clicks and drops.
        _hitDisk = new Path { Data = _hitGeometry, Fill = HitArea };
        Panel.SetZIndex(_hitDisk, -2);
        _canvas.Children.Add(_hitDisk);

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
        _dotLabel = MakeTag(_dotLabelText);
        Panel.SetZIndex(_dotLabel, 9000);
        _canvas.Children.Add(_dotLabel);

        _gapLabel = MakeTag(new TextBlock { FontSize = 12, Text = "Add to ring" });
        Panel.SetZIndex(_gapLabel, 9000);
        _canvas.Children.Add(_gapLabel);
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
        MouseLeftButtonDown += OnLeftDown;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonUp += OnRightClick;
        LostMouseCapture += (_, _) => CancelPress();
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => EndExternalDrag();
        Drop += OnDrop;
    }

    static Border MakeTag(TextBlock text) => new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 2, 6, 3),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        Child = text,
    };

    /// <summary>Opens the wheel on <paramref name="monitor"/>, centred vertically on the pointer.</summary>
    public void Open(MonitorInfo monitor, Drawing.Point cursor)
    {
        if (_isOpen) return;

        _bounds = monitor.Bounds;
        _monitorScale = monitor.Scale;
        _anchorY = cursor.Y;
        var widthDip = _settings.Radius + _settings.ItemSize * (1 + HoverGrow) + LabelRoom;
        _targetWidthPx = (int)Math.Ceiling(widthDip * monitor.Scale);

        ApplyPalette();
        _ring = Math.Clamp(_settings.ActiveRing, 0, _settings.Rings.Count - 1);
        RebuildItems();
        RebuildDots();
        _open = 0;
        _itemsIn = 1;
        _outsideFor = 0;
        _wheelAccum = 0;
        _hovered = _hoveredDot = -1;
        _pressIndex = _liftedIndex = -1;
        _gap = _dropInto = -1;
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
        if (IsMouseCaptured) ReleaseMouseCapture();
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

    void ApplyPalette()
    {
        _palette = Theme.IsDark ? DarkPalette : LightPalette;
        foreach (var tag in new[] { _dotLabel, _gapLabel })
        {
            tag.Background = _palette.TagBackground;
            ((TextBlock)tag.Child).Foreground = _palette.TagText;
        }
    }

    // ---- Rings ----

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

    /// <summary>Recreates the item visuals for the current ring. With <paramref name="keepOffset"/> the wheel doesn't jump.</summary>
    void RebuildItems(bool keepOffset = false)
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

        for (var i = 0; i < _visuals.Count; i++)
        {
            _visuals[i].Slot = i;
            _canvas.Children.Add(_visuals[i].Root);
            _canvas.Children.Add(_visuals[i].Label);
        }

        var max = Math.Max(0, _visuals.Count - 1);
        if (keepOffset)
        {
            _offset = Math.Clamp(_offset, 0, max);
            _targetOffset = Math.Clamp(_targetOffset, 0, max);
        }
        else
        {
            _targetOffset = _offset = Math.Clamp(_ringCenters.GetValueOrDefault(_ring), 0, max);
        }
    }

    void RebuildDots()
    {
        foreach (var d in _dots) _canvas.Children.Remove(d.Shape);
        _dots.Clear();
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
        var rotate = new RotateTransform();
        var root = new Grid
        {
            Width = size,
            Height = size,
            Background = HitArea,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { scale, rotate } },
            Children = { plate, content },
        };

        var labelText = new TextBlock
        {
            Text = item?.DisplayName ?? "Add itemsâ€¦",
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

        return new ItemVisual(item, root, plate, scale, rotate, label, labelText, labelScale, item == null || item.Exists, icon == null);
    }

    // ---- Frame loop ----

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

        // A press turns into a pick-up after a moment, or as soon as the pointer moves.
        if (_pressIndex >= 0 && _liftedIndex < 0)
        {
            var moved = (_cursor - _pressPoint).Length;
            if (now - _pressTime >= LongPressSeconds || moved > DragStartDistance) Lift(_pressIndex);
        }

        LayoutFromWindow(dt);

        // Keep the wheel open while something is held, even if the pointer strays.
        if (!_closing && !_contextMenuOpen && _pressIndex < 0 && _liftedIndex < 0)
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
        _cursor = cursor;
        var now = _clock.Elapsed.TotalSeconds;
        var radius = _settings.Radius;
        var itemSize = _settings.ItemSize;

        // Hub on the left edge at the pointer's height, nudged inward so the wheel isn't clipped by the screen.
        var margin = Math.Min(height / 2, radius * 0.6);
        var cy = Math.Clamp(anchorY, margin, height - margin);

        var ease = 1 - Math.Pow(1 - _open, 3);
        var itemsEase = 1 - Math.Pow(1 - _itemsIn, 3);
        var follow = dt > 0 ? 1 - Math.Exp(-dt * 16) : 1;
        var slotFollow = dt > 0 ? 1 - Math.Exp(-dt * 18) : 1;
        var hoverFollow = dt > 0 ? 1 - Math.Exp(-dt * 22) : 1;
        _offset += (_targetOffset - _offset) * follow;
        if (Math.Abs(_targetOffset - _offset) < 0.001) _offset = _targetOffset;

        var outer = radius + itemSize * (0.5 + HoverGrow) + 16;
        _hitGeometry.Center = new Point(0, cy);
        _hitGeometry.RadiusX = _hitGeometry.RadiusY = outer * ease;
        _arcGeometry.Center = new Point(0, cy);
        _arcGeometry.RadiusX = _arcGeometry.RadiusY = radius * ease;
        _arc.Opacity = ease;

        var hubDy = cursor.Y - cy;
        _hubDistance = Math.Sqrt(cursor.X * cursor.X + hubDy * hubDy);
        var step = AngleStepDeg * Math.PI / 180;
        var cursorAngle = Math.Atan2(hubDy, Math.Max(cursor.X, 0.001));
        _cursorSlot = cursorAngle / step + _offset;

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
                var distance = (cursor - new Point(d.X, d.Y)).Length;
                if (distance <= DotSize * (1 + DotHoverGrow * d.Hover) / 2 + 4 && distance < bestDot)
                {
                    bestDot = distance;
                    hoveredDot = i;
                }
            }
        }

        // Slots: each item's place on the arc. While dragging, the held item leaves its slot and
        // everything from the gap onward shifts one place along to make room.
        var ordered = 0;
        for (var i = 0; i < _visuals.Count; i++)
        {
            var v = _visuals[i];
            v.InOrder = i != _liftedIndex && !(Busy && v.Item == null); // the "+" placeholder hides during drags
            if (!v.InOrder) continue;
            v.TargetSlot = _gap >= 0 && ordered >= _gap ? ordered + 1 : ordered;
            v.Slot += (v.TargetSlot - v.Slot) * slotFollow;
            ordered++;
        }

        // Place items on the arc and find which one the pointer is over.
        var limit = Math.PI / 2 + step * 0.6;
        var hovered = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _visuals.Count; i++)
        {
            var v = _visuals[i];
            if (!v.InOrder)
            {
                v.Visible = false;
                continue;
            }
            var angle = (v.Slot - _offset) * step;
            v.Visible = Math.Abs(angle) <= limit;
            if (!v.Visible) continue;

            v.Falloff = Math.Max(0, Math.Cos(angle)); // 1 pointing straight out, 0 flat against the edge
            var r = radius * ease;
            v.X = r * Math.Cos(angle);
            v.Y = cy + r * Math.Sin(angle);
            v.BaseScale = (MinScale + (1 - MinScale) * v.Falloff) * (0.6 + 0.4 * ease) * (0.7 + 0.3 * itemsEase);

            var distance = (cursor - new Point(v.X, v.Y)).Length;
            var hitRadius = itemSize * v.BaseScale * (1 + HoverGrow * v.Hover) / 2;
            if (distance <= hitRadius && distance < bestDistance)
            {
                bestDistance = distance;
                hovered = i;
            }
        }
        if (_contextMenuOpen) return; // keep the right-clicked item highlighted while its menu is up

        UpdateDropTarget(hovered, bestDistance, itemSize, radius, onMonitor, cursorAngle, ordered, now);

        _hovered = _closing || hoveredDot >= 0 || _liftedIndex >= 0 ? -1
            : _dragging ? _dropInto
            : hovered;
        _hoveredDot = _closing || Busy ? -1 : hoveredDot;
        _cursorInside = onMonitor && _hubDistance <= outer;

        LayoutDots(ease, hoverFollow);
        LayoutGapLabel(ease, cy, radius, step);

        for (var i = 0; i < _visuals.Count; i++)
        {
            var v = _visuals[i];
            v.Hover += ((i == _hovered ? 1 : 0) - v.Hover) * hoverFollow;
            var dimTarget = _hovered >= 0 && i != _hovered ? 1 : 0;
            v.Dim += (dimTarget - v.Dim) * hoverFollow;

            if (i == _liftedIndex)
            {
                LayoutLifted(v, itemSize, now);
                continue;
            }
            v.Rotate.Angle = 0;

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
            v.Scale.ScaleX = v.Scale.ScaleY = s;
            if (!v.AlwaysPlate) v.Plate.Opacity = v.Hover;
            v.Root.Opacity = opacity;

            LayoutLabel(v, i == _hovered, itemSize * s, z, opacity);
        }
    }

    /// <summary>Works out where a held icon or dragged-in files would land, and scrolls near the ends.</summary>
    void UpdateDropTarget(int hovered, double hoveredDistance, double itemSize, double radius, bool onMonitor,
        double cursorAngle, int ordered, double now)
    {
        if (!Busy || _closing)
        {
            _gap = _dropInto = -1;
            return;
        }

        // Dragged-in files held right over a folder or app go into it; the gap stays where it was.
        if (_dragging && hovered >= 0 && _visuals[hovered].Item is { } target
            && hoveredDistance <= itemSize * _visuals[hovered].BaseScale * DropIntoFraction
            && DropTargets.EffectFor(target, _dragFiles, _dragKeys, _dragAllowed) != DragDropEffects.None)
        {
            _dropInto = hovered;
            return;
        }
        _dropInto = -1;

        var inBand = onMonitor && Math.Abs(_hubDistance - radius) <= itemSize * 0.9;
        if (!inBand)
        {
            _gap = -1; // let go out here and nothing changes
            return;
        }

        // Insert before the first item that sits past the pointer.
        var gap = 0;
        foreach (var v in _visuals)
            if (v.InOrder && v.Slot < _cursorSlot) gap++;
        _gap = gap;

        if (Math.Abs(cursorAngle) > AutoScrollAngleDeg * Math.PI / 180 && now - _lastAutoScroll > AutoScrollSeconds)
        {
            _lastAutoScroll = now;
            _targetOffset = Math.Clamp(Math.Round(_targetOffset) + Math.Sign(cursorAngle), 0, ordered);
        }
    }

    void LayoutLifted(ItemVisual v, double itemSize, double now)
    {
        // The held icon follows the pointer, a little larger, with a gentle home-screen wiggle.
        v.Root.Visibility = Visibility.Visible;
        Canvas.SetLeft(v.Root, _cursor.X - itemSize / 2);
        Canvas.SetTop(v.Root, _cursor.Y - itemSize / 2);
        Panel.SetZIndex(v.Root, 8000);
        v.Scale.ScaleX = v.Scale.ScaleY = 1.12;
        v.Rotate.Angle = Math.Sin(now * 18) * 3;
        v.Plate.Opacity = 1;
        v.Root.Opacity = _gap >= 0 ? 0.95 : 0.6; // fades when letting go would cancel
        v.Label.Visibility = Visibility.Collapsed;
        v.X = _cursor.X;
        v.Y = _cursor.Y;
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

    void LayoutGapLabel(double ease, double cy, double radius, double step)
    {
        // Only for files coming in from outside; a held icon shows its own landing spot by the gap.
        if (!_dragging || _gap < 0 || _dropInto >= 0 || ease < 0.5)
        {
            _gapLabel.Visibility = Visibility.Collapsed;
            return;
        }
        var angle = (_gap - _offset) * step;
        _gapLabel.Visibility = Visibility.Visible;
        _gapLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_gapLabel, radius * Math.Cos(angle) - _gapLabel.DesiredSize.Width / 2);
        Canvas.SetTop(_gapLabel, cy + radius * Math.Sin(angle) - _gapLabel.DesiredSize.Height / 2);
    }

    void LayoutLabel(ItemVisual v, bool hovered, double iconSize, int z, double opacity)
    {
        if (v.Falloff < LabelMinFalloff && v.Hover < 0.01)
        {
            v.Label.Visibility = Visibility.Collapsed;
            return;
        }

        var text = v.Item == null ? "Add itemsâ€¦" : v.Item.DisplayName;
        if (hovered && _dragging && v.Item != null)
            text = DropTargets.Describe(v.Item, DropTargets.EffectFor(v.Item, _dragFiles, _dragKeys, _dragAllowed));
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

    // ---- Input ----

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
        if (ShowDots && !Busy && _hubDistance < ringZone) SwitchRing(_ring - steps);
        else _targetOffset = Math.Clamp(Math.Round(_targetOffset) - steps, 0, Math.Max(0, _visuals.Count - (Busy ? 0 : 1)));
        return true;
    }

    void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing || _contextMenuOpen || _hoveredDot >= 0 || _hovered < 0 || _visuals[_hovered].Item == null) return;
        _pressIndex = _hovered;
        _pressTime = _clock.Elapsed.TotalSeconds;
        _pressPoint = _cursor;
        CaptureMouse(); // keep getting the button-up even if the pointer leaves the wheel
    }

    void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressIndex;
        _pressIndex = -1;

        if (_liftedIndex >= 0)
        {
            PutDown(_liftedIndex);
            if (IsMouseCaptured) ReleaseMouseCapture();
            return;
        }
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_closing) return;

        if (_hoveredDot >= 0)
        {
            SwitchRing(_hoveredDot);
            return;
        }
        if (_hovered < 0 || (pressed >= 0 && pressed != _hovered)) return;

        var item = _visuals[_hovered].Item;
        _targetOffset = _hovered; // re-open centred on what was just used
        _closing = true;
        if (item == null) SettingsRequested?.Invoke();
        else ItemInvoked?.Invoke(item);
    }

    void CancelPress()
    {
        _pressIndex = -1;
        if (_liftedIndex < 0) return;
        _gap = -1; // capture lost mid-drag (e.g. Alt+Tab): put it back
        PutDown(_liftedIndex);
    }

    void Lift(int index)
    {
        _liftedIndex = index;
        _hovered = -1;
        // The gap starts where the icon was, so nothing moves until the pointer does.
        _gap = _visuals.Take(index).Count(v => v.Item != null);
    }

    void PutDown(int from)
    {
        var v = _visuals[from];
        _liftedIndex = -1;
        v.Slot = _cursorSlot; // glide from where it was let go into its place
        if (_gap >= 0 && v.Item != null)
        {
            var to = Math.Clamp(_gap, 0, _visuals.Count - 1);
            var items = CurrentRing.Items;
            items.RemoveAt(from);
            items.Insert(to, v.Item);
            _visuals.RemoveAt(from);
            _visuals.Insert(to, v);
            if (to != from) RingEdited?.Invoke();
        }
        _gap = -1;
    }

    void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_closing || _contextMenuOpen || Busy || _hovered < 0 || _visuals[_hovered].Item is not { } item) return;
        e.Handled = true;
        var index = _hovered;
        var remove = false;

        // TrackPopupMenu runs a modal loop; rendering keeps going, so freeze hover/close until it returns.
        _contextMenuOpen = true;
        NativeMethods.GetCursorPos(out var p);
        var invoked = false;
        if (item.Exists)
            invoked = ShellContextMenu.Show(item.Path, p.X, p.Y, ("Remove from ring", () => remove = true));
        else
            ShellContextMenu.ShowSimpleMenu(("Remove from ring", false, () => remove = true));
        _contextMenuOpen = false;
        _outsideFor = 0;

        if (remove)
        {
            CurrentRing.Items.RemoveAt(index);
            RebuildItems(keepOffset: true);
            RingEdited?.Invoke();
        }
        else if (invoked)
        {
            _targetOffset = _hovered;
            _closing = true;
        }
    }

    // ---- Files dragged in from outside ----

    void OnDragOver(object sender, DragEventArgs e)
    {
        _dragging = true;
        _dragFiles = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        _dragKeys = e.KeyStates;
        _dragAllowed = e.AllowedEffects;
        e.Effects = CurrentDropEffect();
        e.Handled = true;
    }

    DragDropEffects CurrentDropEffect()
    {
        if (_closing || _dragFiles.Length == 0) return DragDropEffects.None;
        if (_dropInto >= 0 && _visuals[_dropInto].Item is { } target)
            return DropTargets.EffectFor(target, _dragFiles, _dragKeys, _dragAllowed);
        if (_gap >= 0 && NewPaths().Count > 0)
            return _dragAllowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : _dragAllowed & DragDropEffects.Copy;
        return DragDropEffects.None;
    }

    /// <summary>Dragged paths that aren't already on this ring.</summary>
    List<string> NewPaths()
    {
        var existing = CurrentRing.Items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _dragFiles.Where(f => !existing.Contains(f) && (System.IO.File.Exists(f) || System.IO.Directory.Exists(f))).ToList();
    }

    void EndExternalDrag()
    {
        _dragging = false;
        _gap = _dropInto = -1;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        _dragFiles = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        _dragKeys = e.KeyStates;
        var effect = CurrentDropEffect();
        var into = _dropInto >= 0 ? _visuals[_dropInto].Item : null;
        var gap = _gap;
        var paths = NewPaths();
        EndExternalDrag();
        e.Handled = true;

        if (into != null && effect != DragDropEffects.None)
        {
            // We do moves ourselves, so report "none" back: a source told "move" would delete the originals itself.
            e.Effects = effect == DragDropEffects.Move ? DragDropEffects.None : effect;
            _targetOffset = _visuals.FindIndex(v => v.Item == into);
            _closing = true;
            FilesDropped?.Invoke(into, _dragFiles, effect);
            return;
        }

        if (gap >= 0 && paths.Count > 0)
        {
            // Added in place; the wheel stays open showing them.
            e.Effects = effect;
            var items = CurrentRing.Items;
            gap = Math.Clamp(gap, 0, items.Count);
            items.InsertRange(gap, paths.Select(p => new LaunchItem { Path = p }));
            RebuildItems(keepOffset: true);
            _targetOffset = gap;
            RingEdited?.Invoke();
            return;
        }

        e.Effects = DragDropEffects.None;
    }

    /// <summary>
    /// Debug aid (<c>HaloDock.exe --snapshot out.png [hoverIndex]</c>): renders the fully-open wheel
    /// over a plain background without touching the screen. A negative index -N hovers ring dot N-2;
    /// 100+N shows item N picked up and held over the gap after it.
    /// </summary>
    public BitmapSource RenderSnapshot(int width, int height, int hoverIndex)
    {
        ApplyPalette();
        _ring = Math.Clamp(_settings.ActiveRing, 0, _settings.Rings.Count - 1);
        _ringCenters[_ring] = (CurrentRing.Items.Count - 1) / 2;
        RebuildItems();
        RebuildDots();
        _open = 1;
        _itemsIn = 1;
        var cy = height / 2.0;

        // Aim the pointer at the requested item or dot, then let the animations settle.
        Layout(0, new Point(-1000, -1000), height, cy, true);
        var cursor = new Point(-1000, -1000);
        if (hoverIndex >= 100 && hoverIndex - 100 < _visuals.Count - 1)
        {
            var held = hoverIndex - 100;
            Lift(held);
            var a = (_visuals[held + 1].Slot + 0.5 - _offset) * AngleStepDeg * Math.PI / 180;
            cursor = new Point(_settings.Radius * Math.Cos(a) + 10, cy + _settings.Radius * Math.Sin(a));
        }
        else if (hoverIndex >= 0 && hoverIndex < _visuals.Count) cursor = new Point(_visuals[hoverIndex].X, _visuals[hoverIndex].Y);
        else if (hoverIndex < -1 && -hoverIndex - 2 < _dots.Count) cursor = new Point(_dots[-hoverIndex - 2].X, _dots[-hoverIndex - 2].Y);
        for (var i = 0; i < 40; i++) Layout(1 / 60.0, cursor, height, cy, true);

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
        LaunchItem? item, Grid root, Border plate, ScaleTransform scale, RotateTransform rotate,
        Border label, TextBlock labelText, ScaleTransform labelScale, bool exists, bool alwaysPlate)
    {
        public LaunchItem? Item { get; } = item;
        public Grid Root { get; } = root;
        public Border Plate { get; } = plate;
        public ScaleTransform Scale { get; } = scale;
        public RotateTransform Rotate { get; } = rotate;
        public Border Label { get; } = label;
        public TextBlock LabelText { get; } = labelText;
        public ScaleTransform LabelScale { get; } = labelScale;
        public bool Exists { get; } = exists;
        public bool AlwaysPlate { get; } = alwaysPlate;
        public bool InOrder { get; set; }
        public bool Visible { get; set; }
        public double Slot { get; set; }
        public double TargetSlot { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Falloff { get; set; }
        public double BaseScale { get; set; }
        public double Hover { get; set; }
        public double Dim { get; set; }
    }
}
