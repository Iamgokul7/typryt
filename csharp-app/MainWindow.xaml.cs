using System.Windows;

namespace Typryt;

/// <summary>The main window: a custom title bar, Home, Settings and Dictionary screens, and the faint grid behind them.</summary>
public partial class MainWindow : Window
{
    private readonly PermissionManager _permissions;

    public MainWindow(StatsStore stats, PermissionManager permissions)
    {
        InitializeComponent();
        _permissions = permissions;
        Home.Initialize(stats, permissions);
        Settings.Initialize(permissions);

        permissions.Changed += ApplyAppearance;
        ApplyAppearance();
        StateChanged += (_, _) => OnStateChanged();
        IsVisibleChanged += (_, _) => UpdateGridActivity();
        UpdateGridActivity();
    }

    /// <summary>The grid follows the Appearance settings straight away.</summary>
    private void ApplyAppearance() => Backdrop.Configure(_permissions.GridBackgroundEnabled, _permissions.Theme == "light");

    /// <summary>The grid moves only while the window can be seen.</summary>
    private void UpdateGridActivity() => Backdrop.SetWindowActive(IsVisible && WindowState != WindowState.Minimized);

    private void OnStateChanged()
    {
        bool maximized = WindowState == WindowState.Maximized;
        // A maximised window with a custom frame reaches past the screen edge by the resize border; pull it back in.
        Frame.Margin = maximized ? new Thickness(8) : new Thickness(0);
        // Hide the outer border when maximised so content fills edge-to-edge.
        Frame.BorderThickness = maximized ? new Thickness(0) : new Thickness(3);
        MaximizeButton.Content = maximized ? "❐" : "□";
        UpdateGridActivity();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
