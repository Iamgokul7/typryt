using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Typryt.Ui;

namespace Typryt;

/// <summary>
/// The correction toast: "teh → the [Undo] ✕" in the bottom-right corner.
/// It never takes keyboard focus, so typing continues in the app being corrected.
/// It fades in, stays for the undo window, fades out, and closes. Must run on the UI thread.
/// </summary>
public sealed class ToastManager
{
    /// <summary>How long the toast stays, in seconds. Set from the settings before each Show.</summary>
    public int DisplaySeconds { get; set; } = 3;

    /// <summary>"top-left", "top-right", "bottom-left", or "bottom-right". Set from the settings.</summary>
    public string Corner { get; set; } = "bottom-right";

    /// <summary>The current undo key as shown on the toast, for example "ALT+U". Set from the settings before each Show.</summary>
    public string UndoKeyLabel { get; set; } = "ALT+U";
    private const int MarginPixels = 16;

    private static readonly Color Background = Color.FromRgb(0x1C, 0x1C, 0x1E);
    private static readonly Color Foreground = Color.FromRgb(0xF2, 0xF2, 0xF2);
    private static readonly Color Accent = Color.FromRgb(0xE8, 0x62, 0x0A);

    private Window? _window;
    private TranslateTransform? _slide;
    private DispatcherTimer? _timer;
    private Action? _onUndo;

    /// <summary>Shows the toast. A new correction replaces the one already on screen.</summary>
    public void Show(string original, string corrected, Action onUndo)
    {
        Hide();
        _onUndo = onUndo;
        Present(BuildWindow($"{original} → {corrected}", showUndo: true));
    }

    /// <summary>Shows a short message with no Undo button, for example when an undo is refused.</summary>
    public void ShowMessage(string message)
    {
        Hide();
        Present(BuildWindow(message, showUndo: false));
    }

    private void Present(Window window)
    {
        _window = window;
        window.Show();
        // The width is known only after layout, so position the toast after it has been laid out.
        window.UpdateLayout();
        Position(window);
        FadeTo(window, 1.0);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(DisplaySeconds) };
        _timer.Tick += (_, _) => Hide();
        _timer.Start();
    }

    /// <summary>Fades the toast out and closes it.</summary>
    public void Hide()
    {
        _timer?.Stop();
        _timer = null;
        _onUndo = null;

        var window = _window;
        _window = null;
        if (window is null) return;

        var fade = new DoubleAnimation(window.Opacity, 0, Motion.D160);
        fade.Completed += (_, _) => window.Close();
        window.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>The brush the app's theme uses for this key, so the toast follows the light and dark theme.</summary>
    private static Brush ThemeBrush(string key, Color fallback)
    {
        return Application.Current?.Resources[key] as Brush ?? new SolidColorBrush(fallback);
    }

    private Window BuildWindow(string message, bool showUndo)
    {
        var text = new TextBlock
        {
            Text = message,
            Foreground = ThemeBrush("TextPrimaryBrush", Foreground),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var undo = new Button
        {
            Content = "Undo",
            Style = (Style)Application.Current.FindResource("HardButtonPrimary"),
            Height = 40,
            Margin = new Thickness(14, 0, 4, 4),
            Padding = new Thickness(16, 0, 16, 0),
            Focusable = false,
        };
        undo.Click += (_, _) =>
        {
            var handler = _onUndo;
            Hide();
            handler?.Invoke();
        };

        var close = new Button
        {
            Content = "×",
            Style = (Style)Application.Current.FindResource("HardButton"),
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            FontSize = 20,
            Margin = new Thickness(10, 0, 4, 4),
            Focusable = false,
        };
        close.Click += (_, _) => Hide();

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(text);
        if (showUndo)
        {
            row.Children.Add(new TextBlock
            {
                Text = UndoKeyLabel,
                Foreground = ThemeBrush("TextSecondaryBrush", Foreground),
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
            });
            row.Children.Add(undo);
        }
        row.Children.Add(close);

        // A card with a 3 px border and a 6 px hard shadow (a second layered element, no blur).
        var card = new Grid { Margin = new Thickness(0, 0, 6, 6) };
        card.Children.Add(new Border { Margin = new Thickness(6, 6, -6, -6), Background = ThemeBrush("CardShadowBrush", Background) });
        card.Children.Add(new Border
        {
            Background = ThemeBrush("SurfaceBrush", Background),
            BorderBrush = ThemeBrush("TextPrimaryBrush", Foreground),
            BorderThickness = new Thickness(3),
            Padding = new Thickness(18, 10, 10, 6),
            Child = row,
        });
        _slide = new TranslateTransform(0, Motion.Enabled ? 8 : 0);
        card.RenderTransform = _slide;

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Opacity = 0,
            Content = card,
        };
        window.SourceInitialized += (_, _) => MakeNonActivating(window);
        return window;
    }

    private void Position(Window window)
    {
        var area = SystemParameters.WorkArea;
        bool top = Corner.StartsWith("top", StringComparison.Ordinal);
        bool right = Corner.EndsWith("right", StringComparison.Ordinal);
        window.Left = right ? area.Right - window.ActualWidth - MarginPixels : area.Left + MarginPixels;
        window.Top = top ? area.Top + MarginPixels : area.Bottom - window.ActualHeight - MarginPixels;
    }

    private void FadeTo(Window window, double opacity)
    {
        window.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(opacity, Motion.D160));
        if (_slide is { Y: > 0 })
        {
            _slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, Motion.D160) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    // WS_EX_NOACTIVATE stops the window from taking focus when shown or clicked.
    // WS_EX_TOOLWINDOW keeps it out of Alt+Tab.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private static void MakeNonActivating(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        int style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
