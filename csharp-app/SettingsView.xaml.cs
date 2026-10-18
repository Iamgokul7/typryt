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
        ThemeBox.IsChecked = permissions.Theme == "light";
        AppList.ItemsSource = permissions.Apps;
        _loading = false;
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

    private void Theme_Click(object sender, RoutedEventArgs e) => Change(p => p.Theme = ThemeBox.IsChecked == true ? "light" : "dark");

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
