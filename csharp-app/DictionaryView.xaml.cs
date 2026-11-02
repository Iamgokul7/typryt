using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Typryt;

/// <summary>
/// The Dictionary tab: the words the engine has learned for this user, and the words the user
/// undid. The engine is the only place these are stored; this view reads and changes them there.
/// </summary>
public partial class DictionaryView : UserControl
{
    private readonly ApiClient _api = new();
    private List<WordRow> _rows = new();
    private bool _engineReachable = true;

    /// <summary>One line in the list. Kind is "Personal" or "Never corrected".</summary>
    public sealed record WordRow(string Word, bool Personal, DateTime? Until = null)
    {
        public string Kind => Personal
            ? "Personal"
            : Until is null
                ? "Never corrected"
                : $"Never corrected until {Until.Value:dd MMM HH:mm}";
    }

    public DictionaryView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var snapshot = await _api.GetDictionaryAsync(CancellationToken.None);
        if (snapshot is null)
        {
            _engineReachable = false;
            _rows = new List<WordRow>();
            ShowRows();
            Status.Text = "The engine is not running, so your words cannot be shown. Start the engine, then reopen this tab.";
            return;
        }

        _engineReachable = true;
        _rows = snapshot.PersonalWords.Select(w => new WordRow(w, Personal: true))
            .Concat(snapshot.NeverCorrect.Select(w => new WordRow(w, Personal: false, Until: UntilFor(snapshot, w))))
            .OrderBy(r => r.Word, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ShowRows();
        Status.Text = $"{snapshot.PersonalWords.Count} personal words. {snapshot.NeverCorrect.Count} words you undid are never corrected.";
    }

    /// <summary>When a blocked word stops being blocked, in local time; null when it never expires.</summary>
    private static DateTime? UntilFor(DictionarySnapshot snapshot, string word)
    {
        if (!snapshot.NeverCorrectUntil.TryGetValue(word, out double? seconds) || seconds is null) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds.Value * 1000)).LocalDateTime;
    }

    /// <summary>Shows the rows that match the search text.</summary>
    private void ShowRows()
    {
        var search = SearchBox.Text.Trim();
        var shown = _rows
            .Where(r => search.Length == 0 || r.Word.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        WordList.ItemsSource = shown;
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _rows.Count == 0 ? "NO WORDS YET. TYPE ONE ON THE RIGHT AND PRESS ADD." : "NO WORDS MATCH YOUR SEARCH.";
        UpdateRemoveButton();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ShowRows();

    private void WordList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRemoveButton();

    private void UpdateRemoveButton()
    {
        RemoveButton.IsEnabled = _engineReachable && WordList.SelectedItem is WordRow;
        RemoveHint.Text = WordList.SelectedItem is WordRow selected ? $"Selected: {selected.Word}" : "Pick a word in the list first.";
    }

    /// <summary>Add stays disabled while the box is empty.</summary>
    private void NewWord_TextChanged(object sender, TextChangedEventArgs e) => AddButton.IsEnabled = NewWordBox.Text.Trim().Length > 0;

    private async void Add_Click(object sender, RoutedEventArgs e) => await AddWordAsync();

    private async void NewWord_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddWordAsync();
    }

    private async Task AddWordAsync()
    {
        var word = NewWordBox.Text.Trim();
        if (word.Length == 0) return;

        if (!_engineReachable)
        {
            Status.Text = "The engine is not running, so the word cannot be added.";
            return;
        }

        bool added = await _api.AddPersonalWordAsync(word, CancellationToken.None);
        if (!added)
        {
            Status.Text = $"Could not add \"{word}\". Use letters only, with optional apostrophes or hyphens, up to 40 characters.";
            return;
        }

        NewWordBox.Clear();
        await RefreshAsync();
        Status.Text = $"Added \"{word.ToLowerInvariant()}\" to your personal words.";
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (WordList.SelectedItem is not WordRow row) return;

        bool removed = row.Personal
            ? await _api.RemovePersonalWordAsync(row.Word, CancellationToken.None)
            : await _api.RemoveNeverCorrectAsync(row.Word, CancellationToken.None);

        await RefreshAsync();
        Status.Text = removed
            ? $"Removed \"{row.Word}\". {(row.Personal ? "" : "It can be corrected again.")}".Trim()
            : $"Could not remove \"{row.Word}\". It may already have been removed.";
    }
}
