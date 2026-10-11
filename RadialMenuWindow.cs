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
/// left, right or top edge. Items shrink toward the ends until they slide off behind the edge.
/// A column of coloured dots right by the edge switches between saved rings (item sets).
///
/// Editing works like a phone home screen: long-press (or press and drag) an icon to pick it up and the
/// others part to show where it will land. Files dragged in from Explorer can be dropped onto a folder/app
/// icon (move/copy/open with) or into a gap between icons to add them to the ring.
///
/// Geometry is worked out in edge-local coordinates (x = distance out from the edge, y = position along it)
/// and mapped to the window at the end, so every edge shares the same code.
/// </summary>
public sealed class RadialMenuWindow : Window
{
    const double AngleStepDeg = 24;
    const double MinScale = 0.5;
    const double HoverGrow = 0.25;
    const double LabelHoverGrow = 0.3;
    const double LabelMinFalloff = 0.25; // names of items squashed against the edge would overlap, so hide them
    const double DimmedOpacity = 0.55;
    const double SideLabelRoom = 320;     // DIP beside the wheel reserved for names (left/right edges)
    const double TopLabelRoom = 56;       // DIP below the wheel for names (top edge)
    const double OpenSeconds = 0.14;
    const double CloseSeconds = 0.1;
    const double RingSwitchSeconds = 0.16;
    const double RippleSeconds = 0.5;
    const double LeaveGraceSeconds = 0.12;

    const double LongPressSeconds = 0.35;
    const double DragStartDistance = 8;    // DIP the pointer can move before a press becomes a drag
    const double DropIntoFraction = 0.42;  // within this much of an icon's size from its centre = drop onto it
    const double AutoScrollAngleDeg = 62;  // dragging past this angle scrolls the wheel
    const double AutoScrollSeconds = 0.35;

    const double TrashSize = 40;           // the remove target that appears in the middle while an icon is held
    const double TrashHitFactor = 0.9;     // how close (relative to its size) counts as over it
    const double TrashInsetFactor = 0.25;  // how far out from the edge it sits, relative to the wheel radius

    const double DotSize = 12;
    const double DotHoverGrow = 0.5;
    const double DotInset = 16;            // ring dots sit in a column this far from the edge
    const double DotSpacing = 22;

    // Alpha 1 is invisible but still catches the mouse (and drag-and-drop), so nothing falls through the wheel.
    static readonly Brush HitArea = Frozen(new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)));
    static readonly Brush DangerFill = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));

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
    readonly EllipseGeometry _rippleGeometry = new();
    readonly Path _ripple;
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
    ScreenEdge _edge = ScreenEdge.Left;
    Drawing.Rectangle _bounds;
    Drawing.Rectangle _placement;
    Drawing.Point _anchor;
    double _monitorScale = 1;
    double _winW, _winH;          // window size in DIP
    double _cy;                   // hub position along the edge, in DIP
    double _offset, _targetOffset, _open, _itemsIn, _outsideFor, _lastFrame, _hubDistance;
    double _rippleT = 1;
    bool _isOpen, _closing, _cursorInside, _rendering, _contextMenuOpen;
    int _wheelAccum, _hovered = -1, _hoveredDot = -1, _ring;
    Point _cursor;                // edge-local
    double _cursorSlot;
    double? _simNow;              // set while rendering the demo/snapshots, so animations follow a fake clock

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

    // The drop target in the middle for taking a held icon off the ring.
    readonly Grid _trash;
    readonly Ellipse _trashCircle;
    readonly TextBlock _trashGlyph;
    readonly ScaleTransform _trashScale = new();
    readonly Border _trashLabel;
    double _trashShow, _trashHover;
    bool _overTrash;

    // Its counterpart: marks the gap where dragged-in files will be added.
    readonly Grid _addMarker;
    readonly Ellipse _addCircle;
    readonly TextBlock _addGlyph;

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

    bool ShowDots => _settings.Rings.Count > 1;

    bool Busy => _liftedIndex >= 0 || _dragging;

    double Now => _simNow ?? _clock.Elapsed.TotalSeconds;

    double OuterRadius => _settings.Radius + _settings.ItemSize * (0.5 + HoverGrow) + 16;

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
        Panel.SetZIndex(_hitDisk, -3);
        _canvas.Children.Add(_hitDisk);

        // Thin semicircle the icons ride on. It's a full circle centred on the screen edge; half is off-window.
        _arc = new Path { Data = _arcGeometry, StrokeThickness = 1.5, IsHitTestVisible = false };
        Panel.SetZIndex(_arc, -1);
        _canvas.Children.Add(_arc);

        // Colour wave that spreads out from the edge when switching rings.
        _ripple = new Path { Data = _rippleGeometry, StrokeThickness = 3, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Panel.SetZIndex(_ripple, -2);
        _canvas.Children.Add(_ripple);

        _dotLabelText = new TextBlock { FontSize = 12 };
        _dotLabel = MakeTag(_dotLabelText);
        Panel.SetZIndex(_dotLabel, 9000);
        _canvas.Children.Add(_dotLabel);

        _gapLabel = MakeTag(new TextBlock { FontSize = 12 });
        Panel.SetZIndex(_gapLabel, 9000);
        _canvas.Children.Add(_gapLabel);

        _trash = MakeSymbolDisc(out _trashCircle, out _trashGlyph);
        _trash.RenderTransform = _trashScale;
        Panel.SetZIndex(_trash, 7000); // under the held icon, over everything else
        _canvas.Children.Add(_trash);

        _trashLabel = MakeTag(new TextBlock { FontSize = 12 });
        Panel.SetZIndex(_trashLabel, 9000);
        _canvas.Children.Add(_trashLabel);

        _addMarker = MakeSymbolDisc(out _addCircle, out _addGlyph);
        Panel.SetZIndex(_addMarker, 7000);
        _canvas.Children.Add(_addMarker);
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
        MouseLeftButtonDown += (_, _) => { if (Press()) CaptureMouse(); };
        MouseLeftButtonUp += (_, _) =>
        {
            Release();
            if (IsMouseCaptured) ReleaseMouseCapture();
        };
        MouseRightButtonUp += OnRightClick;
        LostMouseCapture += (_, _) => CancelPress();
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => EndExternalDrag();
        Drop += OnDrop;
    }

    /// <summary>A plain disc with an icon-font symbol: the remove target and the add marker.</summary>
    static Grid MakeSymbolDisc(out Ellipse circle, out TextBlock glyph)
    {
        circle = new Ellipse { Width = TrashSize, Height = TrashSize };
        glyph = new TextBlock
        {
            FontFamily = new FontFamily(RemoveSymbols.Font),
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Grid
        {
            Width = TrashSize,
            Height = TrashSize,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Children = { circle, glyph },
        };
    }

    static Border MakeTag(TextBlock text) => new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 2, 6, 3),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        Child = text,
    };

    // ---- Edge geometry ----

    /// <summary>Edge-local point to window DIP.</summary>
    Point ToWindow(double x, double y) => _edge switch
    {
        ScreenEdge.Right => new Point(_winW - x, y),
        ScreenEdge.Top => new Point(y, x),
        _ => new Point(x, y),
    };

    Point ToWindow(Point local) => ToWindow(local.X, local.Y);

    /// <summary>Window DIP to edge-local point.</summary>
    Point ToLocal(Point w) => _edge switch
    {
        ScreenEdge.Right => new Point(_winW - w.X, w.Y),
        ScreenEdge.Top => new Point(w.Y, w.X),
        _ => w,
    };

    double AlongLength => _edge == ScreenEdge.Top ? _winW : _winH;

    static void SetCenter(FrameworkElement element, Point window, double width, double height)
    {
        Canvas.SetLeft(element, window.X - width / 2);
        Canvas.SetTop(element, window.Y - height / 2);
    }

    /// <summary>Puts a tag next to something, on the side facing away from the edge (below it for the top edge).</summary>
    void PlaceBeside(FrameworkElement tag, Point window, double clearance)
    {
        tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = tag.DesiredSize;
        var (left, top) = _edge switch
        {
            ScreenEdge.Right => (window.X - clearance - size.Width, window.Y - size.Height / 2),
            ScreenEdge.Top => (window.X - size.Width / 2, window.Y + clearance),
            _ => (window.X + clearance, window.Y - size.Height / 2),
        };
        Canvas.SetLeft(tag, Math.Max(2, left));
        Canvas.SetTop(tag, Math.Max(2, top));
    }

    Point LabelOrigin => _edge switch
    {
        ScreenEdge.Right => new Point(1, 0.5),
        ScreenEdge.Top => new Point(0.5, 0),
        _ => new Point(0, 0.5),
    };

    // ---- Opening and closing ----

    /// <summary>Opens the wheel against <paramref name="edge"/> of <paramref name="monitor"/>, centred on the pointer.</summary>
    public void Open(MonitorInfo monitor, Drawing.Point cursor, ScreenEdge edge)
    {
        if (_isOpen) return;

        _edge = edge;
        _bounds = monitor.Bounds;
        _monitorScale = monitor.Scale;
        _anchor = cursor;

        var room = edge == ScreenEdge.Top ? TopLabelRoom : SideLabelRoom;
        var depth = (int)Math.Ceiling((_settings.Radius + _settings.ItemSize * (1 + HoverGrow) + room) * monitor.Scale);
        var b = monitor.Bounds;
        _placement = edge switch
        {
            ScreenEdge.Right => new Drawing.Rectangle(b.Right - depth, b.Top, depth, b.Height),
            ScreenEdge.Top => new Drawing.Rectangle(b.Left, b.Top, b.Width, depth),
            _ => new Drawing.Rectangle(b.Left, b.Top, depth, b.Height),
        };

        Prepare();
        _isOpen = true;

        Place();
        LayoutFromWindow(0); // render the collapsed state before the window becomes visible
        Show();
        Place();

        _cursorInside = true;
        _hook.Install();
        _lastFrame = Now;
        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
    }

    /// <summary>Fresh state for a new opening: visuals for the current ring, nothing hovered or held.</summary>
    void Prepare()
    {
        ApplyPalette();
        _ring = Math.Clamp(_settings.ActiveRing, 0, _settings.Rings.Count - 1);
        RebuildItems();
        RebuildDots();
        _open = 0;
        _itemsIn = 1;
        _rippleT = 1;
        _outsideFor = 0;
        _wheelAccum = 0;
        _hovered = _hoveredDot = -1;
        _pressIndex = _liftedIndex = -1;
        _gap = _dropInto = -1;
        _overTrash = false;
        _trashShow = _trashHover = 0;
        _dragging = false;
        _closing = false;
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
            _placement.Left, _placement.Top, _placement.Width, _placement.Height, NativeMethods.SWP_NOACTIVATE);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF resizes the window to Windows' suggested rect on a DPI change; put it back where we want it.
        if (_isOpen) Dispatcher.BeginInvoke(Place);
    }

    void ApplyPalette()
    {
        _palette = Theme.IsDark ? DarkPalette : LightPalette;
        foreach (var tag in new[] { _dotLabel, _gapLabel, _trashLabel })
        {
            tag.Background = _palette.TagBackground;
            ((TextBlock)tag.Child).Foreground = _palette.TagText;
        }
        // Remove and add use a matching pair of symbols and words (unpin/pin, minus/plus, ...).
        var symbol = _settings.RemoveSymbol;
        ((TextBlock)_gapLabel.Child).Text = RemoveSymbols.AddText(symbol);
        ((TextBlock)_trashLabel.Child).Text = RemoveSymbols.RemoveText(symbol);
        _trashGlyph.Text = RemoveSymbols.Glyph(symbol);
        _addGlyph.Text = RemoveSymbols.AddGlyph(symbol);
        _addCircle.Fill = _palette.TagBackground;
        _addGlyph.Foreground = _palette.TagText;
        StyleTrash();
    }

    void StyleTrash()
    {
        _trashCircle.Fill = _overTrash ? DangerFill : _palette.TagBackground;
        _trashGlyph.Foreground = _overTrash ? Brushes.White : _palette.TagText;
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

        // A wave of the new ring's colour rolls out from the edge.
        _rippleT = 0;
        _ripple.Stroke = Frozen(new SolidColorBrush(RingColor(CurrentRing, 0xD0)));
        _ripple.Fill = Frozen(new SolidColorBrush(RingColor(CurrentRing, 0x22)));

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
            // A ring opens where it was left; the first time, on its middle item so the wheel looks full.
            var center = _ringCenters.TryGetValue(_ring, out var remembered) ? remembered : max / 2;
            _targetOffset = _offset = Math.Clamp(center, 0, max);
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
            RenderTransformOrigin = LabelOrigin, // grow away from the icon
            RenderTransform = labelScale,
            Child = labelText,
        };

        return new ItemVisual(item, root, plate, scale, rotate, label, labelText, labelScale, item == null || item.Exists, icon == null);
    }

    // ---- Frame loop ----

    void OnRendering(object? sender, EventArgs e)
    {
        var now = Now;
        var dt = Math.Min(0.05, now - _lastFrame);
        _lastFrame = now;
        if (dt <= 0) return; // Rendering can fire more than once per frame

        if (!Advance(dt))
        {
            FinishHide();
            return;
        }
        LayoutFromWindow(dt);
        CheckLeave(dt);
    }

    /// <summary>Moves the time-based animations on. Returns false once the close animation has finished.</summary>
    bool Advance(double dt)
    {
        if (_closing)
        {
            _open -= dt / CloseSeconds;
            if (_open <= 0)
            {
                _open = 0;
                return false;
            }
        }
        else
        {
            _open = Math.Min(1, _open + dt / OpenSeconds);
        }
        _itemsIn = Math.Min(1, _itemsIn + dt / RingSwitchSeconds);
        _rippleT = Math.Min(1, _rippleT + dt / RippleSeconds);

        // A press turns into a pick-up after a moment, or as soon as the pointer moves.
        if (_pressIndex >= 0 && _liftedIndex < 0)
        {
            var moved = (_cursor - _pressPoint).Length;
            if (Now - _pressTime >= LongPressSeconds || moved > DragStartDistance) Lift(_pressIndex);
        }
        return true;
    }

    /// <summary>Starts closing once the pointer has been away from the wheel for a moment (unless something is held).</summary>
    void CheckLeave(double dt)
    {
        if (_closing || _contextMenuOpen || _pressIndex >= 0 || _liftedIndex >= 0) return;
        _outsideFor = _cursorInside ? 0 : _outsideFor + dt;
        if (_outsideFor > LeaveGraceSeconds) _closing = true;
    }

    /// <summary>Lays out using the live window position and pointer.</summary>
    void LayoutFromWindow(double dt)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        NativeMethods.GetWindowRect(_hwnd, out var rc);
        NativeMethods.GetCursorPos(out var p);

        _winW = (rc.Right - rc.Left) / dpi.DpiScaleX;
        _winH = (rc.Bottom - rc.Top) / dpi.DpiScaleY;
        var cursor = new Point((p.X - rc.Left) / dpi.DpiScaleX, (p.Y - rc.Top) / dpi.DpiScaleY);
        var anchor = _edge == ScreenEdge.Top
            ? (_anchor.X - rc.Left) / dpi.DpiScaleX
            : (_anchor.Y - rc.Top) / dpi.DpiScaleY;
        Layout(dt, ToLocal(cursor), anchor, _bounds.Contains(p.X, p.Y));
    }

    void Layout(double dt, Point cursor, double anchor, bool onMonitor)
    {
        _cursor = cursor;
        var now = Now;
        var radius = _settings.Radius;
        var itemSize = _settings.ItemSize;

        // Hub on the edge at the pointer's position, nudged inward so the wheel isn't clipped by the screen.
        var along = AlongLength;
        var margin = Math.Min(along / 2, radius * 0.6);
        var cy = _cy = Math.Clamp(anchor, margin, along - margin);
        var hub = ToWindow(0, cy);

        var ease = 1 - Math.Pow(1 - _open, 3);
        var itemsEase = 1 - Math.Pow(1 - _itemsIn, 3);
        var follow = dt > 0 ? 1 - Math.Exp(-dt * 16) : 1;
        var slotFollow = dt > 0 ? 1 - Math.Exp(-dt * 18) : 1;
        var hoverFollow = dt > 0 ? 1 - Math.Exp(-dt * 22) : 1;
        _offset += (_targetOffset - _offset) * follow;
        if (Math.Abs(_targetOffset - _offset) < 0.001) _offset = _targetOffset;

        var outer = OuterRadius;
        _hitGeometry.Center = hub;
        _hitGeometry.RadiusX = _hitGeometry.RadiusY = outer * ease;
        _arcGeometry.Center = hub;
        _arcGeometry.RadiusX = _arcGeometry.RadiusY = radius * ease;
        _arc.Opacity = ease;
        LayoutRipple(hub, outer, ease);

        var hubDy = cursor.Y - cy;
        _hubDistance = Math.Sqrt(cursor.X * cursor.X + hubDy * hubDy);
        var step = AngleStepDeg * Math.PI / 180;
        var cursorAngle = Math.Atan2(hubDy, Math.Max(cursor.X, 0.001));
        _cursorSlot = cursorAngle / step + _offset;

        // Ring dots: a short column right by the edge.
        var hoveredDot = -1;
        if (_dots.Count > 0)
        {
            var spacing = Math.Min(DotSpacing, radius * 1.2 / Math.Max(1, _dots.Count - 1));
            var bestDot = double.MaxValue;
            for (var i = 0; i < _dots.Count; i++)
            {
                var d = _dots[i];
                d.X = DotInset * ease;
                d.Y = cy + (i - (_dots.Count - 1) / 2.0) * spacing * ease;
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

        UpdateDropTarget(hovered, bestDistance, itemSize, radius, onMonitor, cursorAngle, ordered, now, cy);

        _hovered = _closing || hoveredDot >= 0 || _liftedIndex >= 0 ? -1
            : _dragging ? _dropInto
            : hovered;
        _hoveredDot = _closing || Busy ? -1 : hoveredDot;
        _cursorInside = onMonitor && _hubDistance <= outer;

        LayoutTrash(ease, cy, hoverFollow);
        LayoutDots(ease, hoverFollow);
        LayoutGapMarker(ease, cy, radius, step);

        // Names hide sooner on the top edge, where they sit below the icons and would collide.
        var labelCutoff = _edge == ScreenEdge.Top ? 0.45 : LabelMinFalloff;
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

            var center = ToWindow(v.X, v.Y);
            v.Root.Visibility = Visibility.Visible;
            SetCenter(v.Root, center, itemSize, itemSize);
            Panel.SetZIndex(v.Root, z);
            v.Scale.ScaleX = v.Scale.ScaleY = s;
            if (!v.AlwaysPlate) v.Plate.Opacity = v.Hover;
            v.Root.Opacity = opacity;

            LayoutLabel(v, i == _hovered, center, itemSize * s, z, opacity, labelCutoff);
        }
    }

    void LayoutRipple(Point hub, double outer, double ease)
    {
        if (_rippleT >= 1)
        {
            _ripple.Visibility = Visibility.Collapsed;
            return;
        }
        var t = 1 - Math.Pow(1 - _rippleT, 3);
        _ripple.Visibility = Visibility.Visible;
        _rippleGeometry.Center = hub;
        _rippleGeometry.RadiusX = _rippleGeometry.RadiusY = 12 + (outer + 24) * t;
        _ripple.StrokeThickness = 1 + 3 * (1 - _rippleT);
        _ripple.Opacity = ease * (1 - _rippleT);
    }

    /// <summary>Works out where a held icon or dragged-in files would land, and scrolls near the ends.</summary>
    void UpdateDropTarget(int hovered, double hoveredDistance, double itemSize, double radius, bool onMonitor,
        double cursorAngle, int ordered, double now, double cy)
    {
        var wasOverTrash = _overTrash;
        _overTrash = false;
        if (!Busy || _closing)
        {
            _gap = _dropInto = -1;
            if (wasOverTrash) StyleTrash();
            return;
        }

        // A held icon over the remove target comes off the ring; the ring closes up to show it's going.
        if (_liftedIndex >= 0 && _trashShow > 0.5
            && (_cursor - TrashCenter(cy)).Length <= TrashSize * TrashHitFactor)
        {
            _overTrash = true;
            _gap = -1;
            if (!wasOverTrash) StyleTrash();
            return;
        }
        if (wasOverTrash) StyleTrash();

        // Dragged-in files held right over a folder or app go into it; the gap stays where it was.
        // The zone is capped by the spacing between icons, so on a tight ring the gaps stay reachable.
        var spacing = radius * AngleStepDeg * Math.PI / 180;
        var dropIntoRadius = Math.Min(itemSize * (hovered >= 0 ? _visuals[hovered].BaseScale : 1) * DropIntoFraction, spacing * 0.3);
        if (_dragging && hovered >= 0 && _visuals[hovered].Item is { } target
            && hoveredDistance <= dropIntoRadius
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
        SetCenter(v.Root, ToWindow(_cursor), itemSize, itemSize);
        Panel.SetZIndex(v.Root, 8000);
        v.Scale.ScaleX = v.Scale.ScaleY = _overTrash ? 0.85 : 1.12;
        v.Rotate.Angle = Math.Sin(now * 18) * 3;
        v.Plate.Opacity = 1;
        v.Root.Opacity = _overTrash ? 0.7 : _gap >= 0 ? 0.95 : 0.6; // fades when letting go would cancel
        v.Label.Visibility = Visibility.Collapsed;
        v.X = _cursor.X;
        v.Y = _cursor.Y;
    }

    /// <summary>The remove target sits in the middle of the wheel, between the ring dots and the icons.</summary>
    Point TrashCenter(double cy) => new(Math.Max(DotInset + TrashSize * 0.75, _settings.Radius * TrashInsetFactor + 12), cy);

    void LayoutTrash(double ease, double cy, double hoverFollow)
    {
        _trashShow += ((_liftedIndex >= 0 ? 1 : 0) - _trashShow) * hoverFollow;
        _trashHover += ((_overTrash ? 1 : 0) - _trashHover) * hoverFollow;
        if (_trashShow < 0.01)
        {
            _trash.Visibility = Visibility.Collapsed;
            _trashLabel.Visibility = Visibility.Collapsed;
            return;
        }
        var c = ToWindow(TrashCenter(cy));
        _trash.Visibility = Visibility.Visible;
        SetCenter(_trash, c, TrashSize, TrashSize);
        _trashScale.ScaleX = _trashScale.ScaleY = (0.6 + 0.4 * _trashShow) * (1 + 0.25 * _trashHover);
        _trash.Opacity = ease * _trashShow;

        // While over it, say what dropping will do, beside the held icon.
        if (_trashHover < 0.05)
        {
            _trashLabel.Visibility = Visibility.Collapsed;
            return;
        }
        _trashLabel.Visibility = Visibility.Visible;
        PlaceBeside(_trashLabel, c, _settings.ItemSize * 0.5 + 6);
        _trashLabel.Opacity = ease * _trashHover;
    }

    void LayoutDots(double ease, double hoverFollow)
    {
        for (var i = 0; i < _dots.Count; i++)
        {
            var d = _dots[i];
            d.Hover += ((i == _hoveredDot ? 1 : 0) - d.Hover) * hoverFollow;
            var s = (0.5 + 0.5 * ease) * (1 + DotHoverGrow * d.Hover) * (i == _ring ? 1.15 : 1);
            SetCenter(d.Shape, ToWindow(d.X, d.Y), DotSize, DotSize);
            d.Scale.ScaleX = d.Scale.ScaleY = s;
            // Dots step aside while the remove target is showing.
            d.Shape.Opacity = ease * (1 - _trashShow) * (i == _ring || i == _hoveredDot ? 1 : 0.7);
        }

        if (_hoveredDot < 0 || ease < 0.5)
        {
            _dotLabel.Visibility = Visibility.Collapsed;
            return;
        }

        var hovered = _dots[_hoveredDot];
        if (_dotLabelText.Text != hovered.Ring.Name) _dotLabelText.Text = hovered.Ring.Name;
        _dotLabel.Visibility = Visibility.Visible;
        PlaceBeside(_dotLabel, ToWindow(hovered.X, hovered.Y), DotSize * (1 + DotHoverGrow) / 2 + 6);
        _dotLabel.Opacity = Math.Min(1, hovered.Hover * 1.5);
    }

    void LayoutGapMarker(double ease, double cy, double radius, double step)
    {
        // Only for files coming in from outside; a held icon shows its own landing spot by the gap.
        if (!_dragging || _gap < 0 || _dropInto >= 0 || ease < 0.5)
        {
            _gapLabel.Visibility = Visibility.Collapsed;
            _addMarker.Visibility = Visibility.Collapsed;
            return;
        }

        // The add symbol sits in the gap, with its words beside it.
        var angle = (_gap - _offset) * step;
        var c = ToWindow(radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
        _addMarker.Visibility = Visibility.Visible;
        SetCenter(_addMarker, c, TrashSize, TrashSize);
        _gapLabel.Visibility = Visibility.Visible;
        PlaceBeside(_gapLabel, c, TrashSize / 2 + 8);
    }

    void LayoutLabel(ItemVisual v, bool hovered, Point center, double iconSize, int z, double opacity, double cutoff)
    {
        if (v.Falloff < cutoff && v.Hover < 0.01)
        {
            v.Label.Visibility = Visibility.Collapsed;
            return;
        }

        var text = v.Item == null ? "Add items…" : v.Item.DisplayName;
        if (hovered && _dragging && v.Item != null)
            text = DropTargets.Describe(v.Item, DropTargets.EffectFor(v.Item, _dragFiles, _dragKeys, _dragAllowed));
        if (v.LabelText.Text != text) v.LabelText.Text = text;

        v.Label.Visibility = Visibility.Visible;
        PlaceBeside(v.Label, center, iconSize / 2 + (_edge == ScreenEdge.Top ? 4 : 8));
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

        // Scrolling between the edge and the icons flips through rings; anywhere else turns the item wheel.
        var ringZone = (_settings.Radius - _settings.ItemSize / 2) * 0.75;
        if (ShowDots && !Busy && _hubDistance < ringZone) SwitchRing(_ring - steps);
        else _targetOffset = Math.Clamp(Math.Round(_targetOffset) - steps, 0, Math.Max(0, _visuals.Count - (Busy ? 0 : 1)));
        return true;
    }

    /// <summary>Left button down. Returns true if an icon was pressed (and the mouse should be captured).</summary>
    bool Press()
    {
        if (_closing || _contextMenuOpen || _hoveredDot >= 0 || _hovered < 0 || _visuals[_hovered].Item == null) return false;
        _pressIndex = _hovered;
        _pressTime = Now;
        _pressPoint = _cursor;
        return true;
    }

    /// <summary>Left button up: drop a held icon, switch ring, or open what was clicked.</summary>
    void Release()
    {
        var pressed = _pressIndex;
        _pressIndex = -1;

        if (_liftedIndex >= 0)
        {
            PutDown(_liftedIndex);
            return;
        }
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
        _gap = -1; // capture lost mid-drag (e.g. Alt+Tab): put it back, never delete it
        _overTrash = false;
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

        if (_overTrash && v.Item != null)
        {
            _overTrash = false;
            _gap = -1;
            StyleTrash();
            CurrentRing.Items.RemoveAt(from);
            RebuildItems(keepOffset: true); // the rest have already closed up, so nothing jumps
            RingEdited?.Invoke();
            return;
        }

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
            invoked = ShellContextMenu.Show(item.Path, p.X, p.Y, (RemoveSymbols.RemoveText(_settings.RemoveSymbol), () => remove = true));
        else
            ShellContextMenu.ShowSimpleMenu((RemoveSymbols.RemoveText(_settings.RemoveSymbol), false, () => remove = true));
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

    // ---- Offline rendering (screenshots and the demo video) ----

    /// <summary>Sets up an off-screen wheel of the given size against <paramref name="edge"/>, fully open.</summary>
    Grid BeginOffscreen(int width, int height, ScreenEdge edge, Brush background)
    {
        _edge = edge;
        _winW = width;
        _winH = height;
        _bounds = new Drawing.Rectangle(0, 0, width, height);
        _simNow = 0;
        Prepare();
        _isOpen = true;

        var root = new Grid { Width = width, Height = height, Background = background, ClipToBounds = true };
        Content = null;
        root.Children.Add(_canvas);
        return root;
    }

    void EndOffscreen(Grid root)
    {
        root.Children.Remove(_canvas);
        Content = _canvas;
        _isOpen = false;
        _simNow = null;
    }

    static BitmapSource Render(Grid root)
    {
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)root.Width, (int)root.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        bmp.Freeze();
        return bmp;
    }

    public static Brush DemoBackground(bool dark) => Frozen(new LinearGradientBrush(
        dark ? Color.FromRgb(0x1D, 0x2B, 0x45) : Color.FromRgb(0xB9, 0xCC, 0xE6),
        dark ? Color.FromRgb(0x4A, 0x3A, 0x5E) : Color.FromRgb(0xE8, 0xDC, 0xEF), 20));

    /// <summary>
    /// Debug aid (<c>HaloDock.exe --snapshot out.png [hoverIndex]</c>): renders the fully-open wheel
    /// without touching the screen. A negative index -N hovers ring dot N-2;
    /// 100+N shows item N picked up and held over the gap after it; 200+N shows it held over the remove target;
    /// 300+N shows a file dragged in from outside, held in the gap before item N.
    /// </summary>
    public BitmapSource RenderSnapshot(int width, int height, int hoverIndex, ScreenEdge edge = ScreenEdge.Left)
    {
        var root = BeginOffscreen(width, height, edge, DemoBackground(Theme.IsDark));
        _ringCenters[_ring] = (CurrentRing.Items.Count - 1) / 2;
        RebuildItems();
        _open = 1;
        var anchor = AlongLength / 2;
        var cy = anchor;
        var step = AngleStepDeg * Math.PI / 180;

        // Aim the pointer at the requested item or dot, then let the animations settle.
        Layout(0, new Point(-1000, -1000), anchor, true);
        var cursor = new Point(-1000, -1000);
        if (hoverIndex >= 300)
        {
            // Pretend a file is being dragged in from Explorer and held in the gap before item N.
            _dragging = true;
            _dragFiles = [Environment.GetFolderPath(Environment.SpecialFolder.Windows)];
            _dragAllowed = DragDropEffects.Copy | DragDropEffects.Link;
            var a = (hoverIndex - 300 - 0.5 - _offset) * step;
            cursor = new Point(_settings.Radius * Math.Cos(a), cy + _settings.Radius * Math.Sin(a));
        }
        else if (hoverIndex >= 200 && hoverIndex - 200 < _visuals.Count)
        {
            Lift(hoverIndex - 200);
            cursor = TrashCenter(cy);
        }
        else if (hoverIndex >= 100 && hoverIndex - 100 < _visuals.Count - 1)
        {
            var held = hoverIndex - 100;
            Lift(held);
            var a = (_visuals[held + 1].Slot + 0.5 - _offset) * step;
            cursor = new Point(_settings.Radius * Math.Cos(a) + 10, cy + _settings.Radius * Math.Sin(a));
        }
        else if (hoverIndex >= 0 && hoverIndex < _visuals.Count) cursor = new Point(_visuals[hoverIndex].X, _visuals[hoverIndex].Y);
        else if (hoverIndex < -1 && -hoverIndex - 2 < _dots.Count) cursor = new Point(_dots[-hoverIndex - 2].X, _dots[-hoverIndex - 2].Y);
        for (var i = 0; i < 40; i++)
        {
            _simNow += 1 / 60.0;
            _trashShow = _liftedIndex >= 0 ? 1 : 0;
            Layout(1 / 60.0, cursor, anchor, true);
        }

        var bmp = Render(root);
        _dragging = false;
        _liftedIndex = -1;
        EndOffscreen(root);
        return bmp;
    }

    /// <summary>
    /// Renders a scripted walkthrough (open, hover, scroll, switch rings, rearrange, unpin, close) frame by frame,
    /// with a drawn mouse pointer, for the README's demo video.
    /// </summary>
    public void RenderDemo(int width, int height, int fps, ScreenEdge edge, Action<int, BitmapSource> onFrame)
    {
        var root = BeginOffscreen(width, height, edge, DemoBackground(Theme.IsDark));
        var dt = 1.0 / fps;
        var frame = 0;
        var opened = false;
        var visible = false;
        double anchor = 0;
        double clickFx = 1;

        // Mouse pointer and a little click pulse, drawn over the wheel.
        var overlay = new Canvas { IsHitTestVisible = false };
        var pulse = new Ellipse { Width = 34, Height = 34, Stroke = Brushes.White, StrokeThickness = 2, Visibility = Visibility.Collapsed };
        var pointer = new Path
        {
            Data = Geometry.Parse("M 0,0 L 0,17 L 4.5,13 L 7.5,20 L 10.2,18.8 L 7.3,12 L 12.5,12 Z"),
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 1.1,
            StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = new ScaleTransform(1.35, 1.35),
        };
        overlay.Children.Add(pulse);
        overlay.Children.Add(pointer);
        root.Children.Add(overlay);

        var along = AlongLength;
        Point EdgePoint(double alongPos) => ToWindow(2, alongPos);
        var pos = ToWindow(along * 0.42, along * 0.62); // start out in the middle of the "desktop"

        Point ItemAt(int i) => ToWindow(_visuals[Math.Clamp(i, 0, _visuals.Count - 1)].X, _visuals[Math.Clamp(i, 0, _visuals.Count - 1)].Y);
        int Center() => (int)Math.Round(_targetOffset);
        Point Dot(int i) => ToWindow(_dots[i].X, _dots[i].Y);
        Point SlotPoint(double slot)
        {
            var a = (slot - _offset) * AngleStepDeg * Math.PI / 180;
            return ToWindow(_settings.Radius * Math.Cos(a), _cy + _settings.Radius * Math.Sin(a));
        }

        void Frame()
        {
            _simNow = frame * dt;
            if (opened && !Advance(dt))
            {
                opened = false;
                visible = false;
                _open = 0;
            }
            var local = ToLocal(pos);
            Layout(dt, local, anchor, true);
            if (opened) CheckLeave(dt);
            _canvas.Visibility = visible ? Visibility.Visible : Visibility.Hidden;

            clickFx = Math.Min(1, clickFx + dt / 0.35);
            pulse.Visibility = clickFx < 1 ? Visibility.Visible : Visibility.Collapsed;
            var ps = 0.4 + 0.8 * clickFx;
            pulse.RenderTransform = new ScaleTransform(ps, ps, 17, 17);
            pulse.Opacity = 0.8 * (1 - clickFx);
            Canvas.SetLeft(pulse, pos.X - 17);
            Canvas.SetTop(pulse, pos.Y - 17);
            Canvas.SetLeft(pointer, pos.X);
            Canvas.SetTop(pointer, pos.Y);

            onFrame(frame++, Render(root));
        }

        double Smooth(double t) => t * t * (3 - 2 * t);

        void Move(double seconds, Func<Point> target)
        {
            var from = pos;
            var n = Math.Max(1, (int)Math.Round(seconds * fps));
            for (var i = 1; i <= n; i++)
            {
                var to = target();
                var t = Smooth((double)i / n);
                pos = new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
                Frame();
            }
        }

        void Hold(double seconds)
        {
            var n = (int)Math.Round(seconds * fps);
            for (var i = 0; i < n; i++) Frame();
        }

        void Click(Action action)
        {
            clickFx = 0;
            action();
        }

        // Rest against the edge until the wheel opens.
        Hold(0.4);
        var edgeAt = along * 0.5;
        Move(0.8, () => EdgePoint(edgeAt));
        Hold(0.2);
        anchor = edgeAt;
        _open = 0;
        opened = visible = true;
        Hold(0.5);

        // Look around.
        var c = Center();
        Move(0.6, () => ItemAt(c));
        Hold(0.5);
        Move(0.45, () => ItemAt(c + 1));
        Hold(0.45);
        Move(0.45, () => ItemAt(c - 1));
        Hold(0.4);

        // Scroll the wheel.
        Move(0.3, () => SlotPoint(_offset - 0.5));
        OnWheel(-120);
        Hold(0.45);
        OnWheel(-120);
        Hold(0.45);
        OnWheel(120);
        Hold(0.5);

        // Switch rings: a ripple in each ring's colour.
        if (_dots.Count > 1)
        {
            foreach (var target in new[] { 1, 2, 0 }.Where(i => i < _dots.Count))
            {
                Move(0.5, () => Dot(target));
                Hold(0.15);
                Click(() => SwitchRing(target));
                Hold(0.8);
            }
        }

        // Pick an icon up and move it two places along.
        c = Center();
        Move(0.55, () => ItemAt(c - 1));
        Hold(0.15);
        Click(() => Press());
        Hold(0.45);
        var from = c - 1;
        Move(0.9, () => SlotPoint(from + 2.5));
        Hold(0.35);
        Release();
        Hold(0.7);

        // Pick one up and unpin it.
        c = Center();
        Move(0.5, () => ItemAt(c + 1));
        Hold(0.15);
        Click(() => Press());
        Hold(0.45);
        Move(0.8, () => ToWindow(TrashCenter(_cy)));
        Hold(0.6);
        Release();
        Hold(0.8);

        // Move away; the wheel tucks itself back in.
        Move(0.7, () => ToWindow(along * 0.42, along * 0.55));
        Hold(0.9);

        EndOffscreen(root);
        root.Children.Remove(overlay);
        _canvas.Visibility = Visibility.Visible;
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
