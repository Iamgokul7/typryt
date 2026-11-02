using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Typryt.Ui;

/// <summary>Animation lengths. All are zero when Windows animation effects are off. Read once, shared by every style.</summary>
public static class Motion
{
    public static readonly bool Enabled = SystemParameters.ClientAreaAnimation;
    public static readonly Duration D90 = Make(90);
    public static readonly Duration D160 = Make(160);
    public static readonly Duration D220 = Make(220);
    public static readonly Duration D300 = Make(300);

    private static Duration Make(int ms) => new(Enabled ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero);
}

/// <summary>Upper-cases button and label text for display only. The brand name keeps its spelling ("Typryt", never "TYPRYT").</summary>
public sealed class CapsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text) return value;
        return text.ToUpperInvariant().Replace("TYPRYT", "Typryt", StringComparison.Ordinal);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Places its children centred on evenly spaced stops, like the ticks and labels under a slider.</summary>
public sealed class StopPanel : Panel
{
    public static readonly DependencyProperty InsetProperty =
        DependencyProperty.Register(nameof(Inset), typeof(double), typeof(StopPanel),
            new FrameworkPropertyMetadata(17.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Distance from each edge to the first and last stop.</summary>
    public double Inset { get => (double)GetValue(InsetProperty); set => SetValue(InsetProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = InternalChildren.Count;
        double span = Math.Max(0, finalSize.Width - 2 * Inset);
        for (int i = 0; i < count; i++)
        {
            var child = InternalChildren[i];
            double centre = count == 1 ? finalSize.Width / 2 : Inset + span * i / (count - 1);
            double left = Math.Clamp(centre - child.DesiredSize.Width / 2, 0, Math.Max(0, finalSize.Width - child.DesiredSize.Width));
            child.Arrange(new Rect(left, 0, child.DesiredSize.Width, finalSize.Height));
        }
        return finalSize;
    }
}

/// <summary>
/// A slider with stops. The thumb and the fill slide to the new stop in 300 ms whether it was dragged,
/// clicked, moved with the keyboard (Left, Right, Home, End), or set from code. Value is the stop.
/// </summary>
public sealed class StopSlider : Slider
{
    private const double ThumbSize = 34;

    private FrameworkElement? _host;
    private FrameworkElement? _fill;
    private FrameworkElement? _thumb;
    private StopPanel? _ticks;
    private readonly TranslateTransform _move = new();
    private readonly DoubleAnimation _moveAnimation = new() { Duration = Motion.D300, EasingFunction = Easing };
    private readonly DoubleAnimation _fillAnimation = new() { Duration = Motion.D300, EasingFunction = Easing };

    private static readonly IEasingFunction Easing = MakeEasing();

    private static IEasingFunction MakeEasing()
    {
        var easing = new QuinticEase { EasingMode = EasingMode.EaseOut };
        easing.Freeze();
        return easing;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _host = GetTemplateChild("PART_Host") as FrameworkElement;
        _fill = GetTemplateChild("PART_Fill") as FrameworkElement;
        _thumb = GetTemplateChild("PART_Thumb") as FrameworkElement;
        _ticks = GetTemplateChild("PART_Ticks") as StopPanel;
        if (_host is null || _thumb is null) return;

        _thumb.RenderTransform = _move;
        BuildTicks();
        _host.SizeChanged += (_, _) => Place(animate: false);
        _host.MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            _host.CaptureMouse();
            SetFromPoint(e.GetPosition(_host));
            e.Handled = true;
        };
        _host.MouseMove += (_, e) =>
        {
            if (_host.IsMouseCaptured) SetFromPoint(e.GetPosition(_host));
        };
        _host.MouseLeftButtonUp += (_, _) => _host.ReleaseMouseCapture();
        Place(animate: false);
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        Place(animate: true);
    }

    protected override void OnMaximumChanged(double oldMaximum, double newMaximum)
    {
        base.OnMaximumChanged(oldMaximum, newMaximum);
        BuildTicks();
    }

    private void BuildTicks()
    {
        if (_ticks is null) return;
        _ticks.Children.Clear();
        int stops = (int)Math.Round((Maximum - Minimum) / Math.Max(TickFrequency, double.Epsilon)) + 1;
        for (int i = 0; i < stops; i++)
        {
            var tick = new Rectangle { Width = 3, Height = 10 };
            tick.SetResourceReference(Shape.FillProperty, "TextPrimaryBrush");
            _ticks.Children.Add(tick);
        }
    }

    private void SetFromPoint(Point point)
    {
        if (_host is null || _host.ActualWidth <= ThumbSize) return;
        double fraction = Math.Clamp((point.X - ThumbSize / 2) / (_host.ActualWidth - ThumbSize), 0, 1);
        double steps = (Maximum - Minimum) / TickFrequency;
        Value = Minimum + Math.Round(fraction * steps) * TickFrequency;
    }

    private void Place(bool animate)
    {
        if (_host is null || _fill is null || _thumb is null || _host.ActualWidth <= ThumbSize) return;
        double fraction = Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
        double x = (_host.ActualWidth - ThumbSize) * fraction;
        double fillWidth = 14 + x;

        if (animate && Motion.Enabled)
        {
            _moveAnimation.To = x;
            _fillAnimation.To = fillWidth;
            _move.BeginAnimation(TranslateTransform.XProperty, _moveAnimation);
            _fill.BeginAnimation(WidthProperty, _fillAnimation);
        }
        else
        {
            _move.BeginAnimation(TranslateTransform.XProperty, null);
            _fill.BeginAnimation(WidthProperty, null);
            _move.X = x;
            _fill.Width = fillWidth;
        }
    }
}

/// <summary>
/// The faint grid behind everything: one frozen tiled brush on one element, moved by a single storyboard
/// (48 px every 48 s, 30 frames a second). It runs only while it is enabled, the window is visible and not
/// minimised, and Windows animation effects are on. Otherwise the storyboard is stopped and removed.
/// </summary>
public sealed class GridBackdrop : Grid
{
    private const double Tile = 48;

    private static readonly Brush DarkGrid = MakeGrid(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LightGrid = MakeGrid(Color.FromArgb(0x17, 0x00, 0x00, 0x00));

    private readonly Rectangle _rectangle;
    private readonly TranslateTransform _move = new();
    private Storyboard? _running;
    private bool _enabled = true;
    private bool _windowActive = true;

    public GridBackdrop()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        _rectangle = new Rectangle
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-Tile, -Tile, 0, 0),
            RenderTransform = _move,
            Fill = DarkGrid,
            IsHitTestVisible = false,
        };
        Children.Add(_rectangle);
        NameScope.SetNameScope(this, new NameScope());
        RegisterName("Move", _move);
        SizeChanged += (_, e) =>
        {
            _rectangle.Width = e.NewSize.Width + Tile;
            _rectangle.Height = e.NewSize.Height + Tile;
        };
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Stop();
    }

    private static Brush MakeGrid(Color line)
    {
        var pen = new Pen(new SolidColorBrush(line), 1);
        var drawing = new GeometryDrawing(null, pen, Geometry.Parse("M 0.5,0 L 0.5,48 M 0,0.5 L 48,0.5"));
        drawing.Freeze();
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewbox = new Rect(0, 0, Tile, Tile),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, Tile, Tile),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Applies the Appearance settings.</summary>
    public void Configure(bool enabled, bool light)
    {
        _enabled = enabled;
        _rectangle.Fill = light ? LightGrid : DarkGrid;
        _rectangle.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    /// <summary>The window tells the grid whether it can be seen (shown and not minimised).</summary>
    public void SetWindowActive(bool active)
    {
        _windowActive = active;
        Refresh();
    }

    private void Refresh()
    {
        if (_enabled && _windowActive && Motion.Enabled && IsLoaded) Start();
        else Stop();
    }

    private void Start()
    {
        if (_running is not null) return;
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(storyboard, 30);
        foreach (var property in new[] { "X", "Y" })
        {
            var animation = new DoubleAnimation(0, Tile, TimeSpan.FromSeconds(48));
            Storyboard.SetTargetName(animation, "Move");
            Storyboard.SetTargetProperty(animation, new PropertyPath(property));
            storyboard.Children.Add(animation);
        }
        storyboard.Begin(this, isControllable: true);
        _running = storyboard;
    }

    private void Stop()
    {
        if (_running is null) return;
        _running.Stop(this);
        _running.Remove(this);
        _running = null;
        _move.X = 0;
        _move.Y = 0;
    }
}
