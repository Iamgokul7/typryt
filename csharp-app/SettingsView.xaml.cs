using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Typryt;

/// <summary>
/// The Settings tab. Every change is saved straight away. Changing a setting raises
/// PermissionManager.Changed, which the app uses to apply it.
/// </summary>
public partial class SettingsView : UserControl
{
    private PermissionManager? _permissions;
    private bool _loading;
    private bool _capturingUndoKey;

    public SettingsView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void Initialize(PermissionManager permissions)
    {
        _permissions = permissions;
        _loading = true;
        MasterBox.IsChecked = permissions.MasterEnabled;
        StartupBox.IsChecked = StartupRegistration.IsEnabled();
        AbbreviationsBox.IsChecked = permissions.AbbreviationsEnabled;
        CapitalizeBox.IsChecked = permissions.AutoCapitalizeEnabled;
        LearningBox.IsChecked = permissions.LearningEnabled;
        UndoLabel.Text = $"Alt + {permissions.UndoLetter}";
        ToastSlider.Value = permissions.ToastSeconds;
        ToastLabel.Text = $"{permissions.ToastSeconds} s";
        SelectCorner(permissions.ToastCorner);
        SelectBlock(permissions.UndoBlockHours);
        SyncAppearance();
        AppList.ItemsSource = permissions.Apps;
        _loading = false;

        // The tray menu and Home change "Turn corrections on" too; keep the switch in step.
        permissions.Changed += SyncMaster;
        IsVisibleChanged += (_, _) => { if (IsVisible) SyncMaster(); };
    }

    private void SyncMaster()
    {
        if (_permissions is null) return;
        bool was = _loading;
        _loading = true;
        MasterBox.IsChecked = _permissions.MasterEnabled;
        _loading = was;
    }

    /// <summary>Shows the chosen theme and grid setting on their buttons (one of each pair is always selected).</summary>
    private void SyncAppearance()
    {
        if (_permissions is null) return;
        bool light = _permissions.Theme == "light";
        ThemeDarkButton.IsChecked = !light;
        ThemeBox.IsChecked = light;
        GridOnButton.IsChecked = _permissions.GridBackgroundEnabled;
        GridOffButton.IsChecked = !_permissions.GridBackgroundEnabled;
    }

    private void Master_Click(object sender, RoutedEventArgs e) => Change(p => p.MasterEnabled = MasterBox.IsChecked == true);

    /// <summary>The startup entry lives in the Windows registry, not in config.json, so it is written directly.</summary>
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartupRegistration.Set(StartupBox.IsChecked == true);
    }

    private void Abbreviations_Click(object sender, RoutedEventArgs e) => Change(p => p.AbbreviationsEnabled = AbbreviationsBox.IsChecked == true);

    private void Capitalize_Click(object sender, RoutedEventArgs e) => Change(p => p.AutoCapitalizeEnabled = CapitalizeBox.IsChecked == true);

    private void Learning_Click(object sender, RoutedEventArgs e) => Change(p => p.LearningEnabled = LearningBox.IsChecked == true);

    private void Theme_Click(object sender, RoutedEventArgs e) { Change(p => p.Theme = "light"); SyncAppearance(); }

    private void ThemeDark_Click(object sender, RoutedEventArgs e) { Change(p => p.Theme = "dark"); SyncAppearance(); }

    private void GridOn_Click(object sender, RoutedEventArgs e) { Change(p => p.GridBackgroundEnabled = true); SyncAppearance(); }

    private void GridOff_Click(object sender, RoutedEventArgs e) { Change(p => p.GridBackgroundEnabled = false); SyncAppearance(); }

    private void App_Click(object sender, RoutedEventArgs e)
    {
        // The checkbox has already updated the app's Enabled value through its binding.
        Change(_ => { });
    }

    private void ToastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        int seconds = (int)Math.Round(ToastSlider.Value);
        ToastLabel.Text = $"{seconds} s";
        Change(p => p.ToastSeconds = seconds);
    }

    private void Block_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BlockBox.SelectedItem is not ComboBoxItem item) return;
        Change(p => p.UndoBlockHours = int.Parse((string)item.Tag));
    }

    private void SelectBlock(int hours)
    {
        foreach (ComboBoxItem item in BlockBox.Items)
        {
            if (int.Parse((string)item.Tag) == hours)
            {
                BlockBox.SelectedItem = item;
                return;
            }
        }
    }

    private void Corner_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CornerBox.SelectedItem is not ComboBoxItem item) return;
        Change(p => p.ToastCorner = (string)item.Tag);
    }

    private void UndoChange_Click(object sender, RoutedEventArgs e)
    {
        _capturingUndoKey = true;
        UndoHint.Text = "Press a letter now (A to Z). Press Esc to keep the current key.";
        UndoChangeButton.IsEnabled = false;
        Focus();
    }

    /// <summary>While capturing, the next letter becomes the undo key. Esc cancels.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingUndoKey || _permissions is null) return;

        if (e.Key == Key.Escape)
        {
            EndCapture();
            e.Handled = true;
            return;
        }

        if (e.Key >= Key.A && e.Key <= Key.Z)
        {
            char letter = (char)('A' + (e.Key - Key.A));
            _permissions.UndoLetter = letter;
            UndoLabel.Text = $"Alt + {_permissions.UndoLetter}";
            _permissions.Commit();
            EndCapture();
            e.Handled = true;
        }
    }

    private void EndCapture()
    {
        _capturingUndoKey = false;
        UndoChangeButton.IsEnabled = true;
        UndoHint.Text = "Press Alt and this letter within a few seconds of a correction to undo it.";
    }

    private void SelectCorner(string corner)
    {
        foreach (ComboBoxItem item in CornerBox.Items)
        {
            if ((string)item.Tag == corner)
            {
                CornerBox.SelectedItem = item;
                return;
            }
        }
    }

    private void Change(Action<PermissionManager> change)
    {
        if (_loading || _permissions is null) return;
        change(_permissions);
        _permissions.Commit();
    }
}
