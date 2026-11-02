using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Typryt;

/// <summary>The Home tab: the on/off switch, counters, correction level, and recent corrections.</summary>
public partial class HomeView : UserControl
{
    private StatsStore? _stats;
    private PermissionManager? _permissions;
    private bool _syncing;

    private static readonly string[] LevelDescriptions =
    [
        "Fixes only what it is sure about: slang, apostrophes and obvious typos.",
        "The default. Fixes clear mistakes and leaves anything it is unsure about.",
        "Fixes more, including less obvious typos. Wrong guesses become more likely.",
        "Fixes everything it can. It will sometimes change words you meant.",
    ];

    private sealed record HistoryRow(string From, string To, string When, bool Undone);

    public HomeView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    public void Initialize(StatsStore stats, PermissionManager permissions)
    {
        _stats = stats;
        _permissions = permissions;
        _stats.Changed += () => Dispatcher.BeginInvoke(Refresh);
        _permissions.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        if (_stats is null || _permissions is null) return;

        FixedCount.Text = _stats.TotalFixed.ToString();
        TodayCount.Text = _stats.FixedToday.ToString();
        UndoneCount.Text = _stats.TotalUndone.ToString();

        RecentList.ItemsSource = _stats.History()
            .Select(h => new HistoryRow(h.Original, h.Corrected, h.At.ToString("dd MMM HH:mm").ToUpperInvariant(), h.Undone))
            .ToList();

        _syncing = true;
        bool on = _permissions.MasterEnabled;
        HomeMasterSwitch.IsChecked = on;
        StatusTitle.Text = on ? "Typryt is on" : "Typryt is off";
        StatusSub.Text = on
            ? "Fixes happen as you type, in the apps you allowed."
            : "Nothing is corrected until you turn it back on.";

        var level = _permissions.Level;
        LevelLow.IsChecked = level == CorrectionLevel.Low;
        LevelMedium.IsChecked = level == CorrectionLevel.Medium;
        LevelHigh.IsChecked = level == CorrectionLevel.High;
        LevelExtreme.IsChecked = level == CorrectionLevel.Extreme;
        LevelSlider.Value = (int)level;
        LevelName.Text = level.ToString().ToUpperInvariant();
        LevelDescription.Text = LevelDescriptions[(int)level];
        _syncing = false;
    }

    /// <summary>The same setting as "Turn corrections on" in Settings and the tray menu.</summary>
    private void HomeMaster_Click(object sender, RoutedEventArgs e)
    {
        if (_permissions is null) return;
        _permissions.MasterEnabled = HomeMasterSwitch.IsChecked == true;
        _permissions.Commit();
    }

    private void Level_Click(object sender, RoutedEventArgs e)
    {
        if (_permissions is null || sender is not ToggleButton button) return;
        _permissions.Level = CorrectionLevels.Parse((string)button.Tag);
        _permissions.Save();
        Refresh();
    }

    /// <summary>Dragging the slider, or using its keys, sets the level the same way the labels do.</summary>
    private void LevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _permissions is null) return;
        _permissions.Level = (CorrectionLevel)(int)Math.Round(LevelSlider.Value);
        _permissions.Save();
        Refresh();
    }
}
