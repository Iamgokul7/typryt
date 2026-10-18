using System.Windows;

namespace Typryt;

/// <summary>The main window: Home, Settings, and Dictionary tabs along the bottom.</summary>
public partial class MainWindow : Window
{
    public MainWindow(StatsStore stats, PermissionManager permissions)
    {
        InitializeComponent();
        Home.Initialize(stats, permissions);
    }
}
