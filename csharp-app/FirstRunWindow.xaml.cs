using System.Windows;

namespace Typryt;

/// <summary>
/// Shown on the first launch: lists the apps found on this PC, each with an on/off switch.
/// The list is edited in place; Save closes the window and the caller stores the choices.
/// </summary>
public partial class FirstRunWindow : Window
{
    public FirstRunWindow(IEnumerable<AppPermission> apps)
    {
        InitializeComponent();
        AppList.ItemsSource = apps.ToList();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
