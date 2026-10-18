using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Windows;

namespace Typryt;

public partial class App : Application
{
    // One engine session for the whole app for now; Phase 9 keys it per window.
    private const string SessionId = "default";

    private const string SingleInstanceName = "Typryt.SingleInstance";

    private Mutex? _singleInstance;
    private KeyboardHook? _keyboardHook;
    private readonly WordDetector _wordDetector = new();
    private readonly ApiClient _api = new();

    // Which apps corrections may run in. Read on the hook thread and the worker thread; it is only
    // changed at startup, before the hook is installed.
    private readonly PermissionManager _permissions = new(PermissionManager.DefaultPath());

    // Counts corrections and undos for the Home tab. Locks internally, so any thread may use it.
    private readonly StatsStore _stats = new(StatsStore.DefaultPath());

    // Used only on the worker thread, except the toast, which is used only on the UI thread.
    private readonly UndoManager _undo = new();
    private readonly ToastManager _toast = new();
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;

    // Bumped by every key the user types. A replacement is skipped if the user typed after the word.
    private int _keySequence;

    // Only the hook thread reads and writes this.
    private bool _lastWindowAllowed;

    // Only the worker thread reads and writes this: the last word on screen, and its trail.
    private ScreenWord? _lastWord;

    // Tray icon, engine process, and dashboard window. All used on the UI thread.
    private TrayIcon? _tray;
    private EngineLauncher? _engine;
    private MainWindow? _main;
    private bool _exiting;

    private sealed record Job(CompletedWord? Word, bool ResetContext, int KeySequence, bool UndoRequest = false);

    private sealed record ScreenWord(string Text, string Trail);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one copy may run. Two copies would each install a keyboard hook and correct every word twice.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool firstCopy);
        if (!firstCopy)
        {
            Shutdown();
            return;
        }

        _permissions.Load();
        if (!_permissions.FirstRunDone)
        {
            RunFirstLaunch();
        }

        _keyboardHook = new KeyboardHook();
        _keyboardHook.KeyPressed += OnKeyPressed;
        _keyboardHook.AltUPressed += OnAltUPressed;
        _keyboardHook.Install();
        _keyboardHook.UndoVirtualKey = UndoKeyFor(_permissions.UndoLetter);
        _permissions.Changed += ApplySettings;

        _worker = Task.Run(() => ProcessJobsAsync(_shutdown.Token));

        // The engine starts in the background, so the splash is not held up by it.
        _engine = new EngineLauncher(EngineLauncher.FindEngineFolder() ?? string.Empty);
        Task.Run(() => _engine.StartIfNeeded());

        // The app lives in the tray. The dashboard opens only when asked for.
        _tray = new TrayIcon(_permissions.MasterEnabled);
        _tray.OpenDashboardRequested += ShowDashboard;
        _tray.OpenHistoryRequested += ShowDashboard;
        _tray.ToggleRequested += ToggleCorrections;
        _tray.ExitRequested += ExitApp;

        ThemeManager.Apply(_permissions.Theme);
        new SplashScreen().Show();
    }

    /// <summary>Shows the dashboard, creating it the first time. Closing it only hides it; the tray keeps the app running.</summary>
    private void ShowDashboard()
    {
        if (_main is null)
        {
            var window = new MainWindow(_stats, _permissions);
            window.Closing += (_, e) =>
            {
                if (_exiting) return;
                e.Cancel = true;
                window.Hide();
            };
            _main = window;
        }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Show();
        _main.Activate();
    }

    /// <summary>Turns corrections on or off, from the tray menu. Same setting as the master switch in Settings.</summary>
    private void ToggleCorrections()
    {
        _permissions.MasterEnabled = !_permissions.MasterEnabled;
        _permissions.Commit();
        _tray?.SetOn(_permissions.MasterEnabled);
    }

    private void ExitApp()
    {
        _exiting = true;
        Shutdown();
    }

    /// <summary>
    /// First launch: finds the installed apps, lets the user choose where corrections may run,
    /// and saves the choice. Runs before the keyboard hook is installed.
    /// </summary>
    private void RunFirstLaunch()
    {
        _permissions.MergeScanned(AppScanner.Scan());

        var window = new FirstRunWindow(_permissions.Apps);
        window.ShowDialog();

        // Closing the window without Save still counts as the first run, with the defaults kept.
        _permissions.MarkFirstRunDone();
        _permissions.Save();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _engine?.Dispose();
        _keyboardHook?.Dispose();
        _jobs.Writer.TryComplete();
        _shutdown.Cancel();
        _api.Dispose();
        if (_singleInstance is not null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread: nothing to release.
            }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>Runs on the hook thread. Must return fast: it only queues work.</summary>
    private void OnKeyPressed(KeyInput key)
    {
        int sequence = Interlocked.Increment(ref _keySequence);

        bool allowed = _permissions.MasterEnabled && IsAllowedWindowActive();
        if (allowed != _lastWindowAllowed)
        {
            // Focus moved in or out of Notepad: the engine's context no longer matches the text.
            _lastWindowAllowed = allowed;
            _wordDetector.OnKey(new KeyInput(null, IsBackspace: false, IsBreak: true));
            _jobs.Writer.TryWrite(new Job(null, ResetContext: true, sequence));
        }
        if (!allowed) return;

        // Keys that move the caret or start new text end the context, so the next word
        // does not refer to text on screen before the break.
        if (!key.IsBackspace && (key.Character is null || key.IsBreak || EndsContext(key.Character.Value)))
        {
            _jobs.Writer.TryWrite(new Job(null, ResetContext: true, sequence));
        }

        var completed = _wordDetector.OnKey(key);
        if (completed is not null)
        {
            _jobs.Writer.TryWrite(new Job(completed, ResetContext: false, sequence));
        }
    }

    /// <summary>Runs on the hook thread. Takes Alt+U only in Notepad, and queues the undo.</summary>
    private bool OnAltUPressed()
    {
        if (!_permissions.MasterEnabled || !IsAllowedWindowActive()) return false;
        _jobs.Writer.TryWrite(new Job(null, ResetContext: false, 0, UndoRequest: true));
        return true;
    }

    /// <summary>Applies the saved settings that the hook, toast, and theme use. Called on the UI thread after a change.</summary>
    private void ApplySettings()
    {
        if (_keyboardHook is not null)
        {
            _keyboardHook.UndoVirtualKey = UndoKeyFor(_permissions.UndoLetter);
        }
        ThemeManager.Apply(_permissions.Theme);
    }

    /// <summary>The virtual-key code of a letter. For A to Z it equals the uppercase character code.</summary>
    private static int UndoKeyFor(char letter) => char.ToUpperInvariant(letter);

    private static bool EndsContext(char c) =>
        !(char.IsLetterOrDigit(c) || c == '\'' || c is ' ' or ',' or '.' or '!' or '?' or ';' or ':');

    private async Task ProcessJobsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(ct))
            {
                try
                {
                    if (job.UndoRequest)
                    {
                        await UndoLastCorrectionAsync(ct);
                    }
                    else if (job.ResetContext)
                    {
                        _lastWord = null;
                        await _api.ResetAsync(SessionId, ct);
                    }
                    else if (job.Word is not null)
                    {
                        await ProcessWordAsync(job.Word, job.KeySequence, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // A failed correction leaves the text alone. Logging comes with the tray icon (Phase 11).
                    _lastWord = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessWordAsync(CompletedWord word, int keySequence, CancellationToken ct)
    {
        // Words the pre-filters skip never reach the engine, so the engine's previous word
        // is no longer the word before this one. Revisions can't be trusted after that.
        // Words the user undid are never corrected again. The engine keeps that list and leaves
        // those words unchanged, so only the pre-filters are checked here.
        if (!word.IsCorrectable)
        {
            _lastWord = null;
            return;
        }

        var response = await _api.CorrectAsync(
            word.Text,
            SessionId,
            _permissions.AbbreviationsEnabled,
            ct,
            _permissions.LearningEnabled,
            _permissions.AutoCapitalizeEnabled);
        if (response is null)
        {
            _lastWord = null;
            return;
        }

        if (Volatile.Read(ref _keySequence) != keySequence)
        {
            // The user kept typing. Leave the text alone rather than delete the wrong characters.
            _lastWord = null;
            return;
        }

        string corrected = response.Confidence >= _permissions.Threshold ? response.Correction : word.Text;

        string? revisedPrevious = null;
        var previous = _lastWord;
        if (previous is not null)
        {
            if (response.PreviousCorrection is not null)
            {
                revisedPrevious = response.PreviousCorrection.Correction;
            }
            else if (response.CommaAfterPrevious)
            {
                revisedPrevious = previous.Text + ",";
            }
            if (revisedPrevious == previous.Text) revisedPrevious = null;
        }

        if (Volatile.Read(ref _keySequence) != keySequence)
        {
            _lastWord = null;
            return;
        }

        if (revisedPrevious is not null && previous is not null)
        {
            // Rewrite the earlier word, its trail, this word, and its trail in one go.
            int deleteCount = previous.Text.Length + previous.Trail.Length + word.Text.Length + word.Trail.Length;
            TextReplacer.Replace(deleteCount, revisedPrevious + previous.Trail + corrected + word.Trail);
        }
        else if (corrected != word.Text)
        {
            TextReplacer.Replace(word.Text.Length + word.Trail.Length, corrected + word.Trail);
        }

        _lastWord = new ScreenWord(corrected, word.Trail);

        if (corrected != word.Text)
        {
            _undo.Record(word.Text, corrected, word.Trail, keySequence);
            _stats.RecordCorrection(word.Text, corrected);
            ShowToast(word.Text, corrected);
        }
    }

    /// <summary>Restores the word a correction replaced, if the undo window is still open and nothing was typed since.</summary>
    private async Task UndoLastCorrectionAsync(CancellationToken ct)
    {
        var attempt = _undo.Attempt(Volatile.Read(ref _keySequence));
        var correction = attempt.Correction;
        if (correction is null)
        {
            // Say why the undo was refused. With nothing to undo there is nothing to say.
            switch (attempt.Refusal)
            {
                case UndoManager.UndoRefusal.TooLate:
                    ShowMessage("Too late to undo");
                    break;
                case UndoManager.UndoRefusal.TypedSince:
                    ShowMessage("Can't undo: you typed after the correction");
                    break;
                default:
                    HideToast();
                    break;
            }
            return;
        }

        HideToast();
        TextReplacer.Replace(correction.Corrected.Length + correction.Trail.Length, correction.Original + correction.Trail);
        _lastWord = new ScreenWord(correction.Original, correction.Trail);
        _stats.RecordUndo(correction.Original, correction.Corrected);

        // The engine remembers the undo, so this word is never corrected again (also after a restart).
        if (_permissions.LearningEnabled)
        {
            await _api.LearnUndoAsync(correction.Original, ct);
        }
    }

    private void ShowMessage(string message)
    {
        Dispatcher.Invoke(() => _toast.ShowMessage(message));
    }

    private void ShowToast(string original, string corrected)
    {
        Dispatcher.Invoke(() =>
        {
            _toast.DisplaySeconds = _permissions.ToastSeconds;
            _toast.Corner = _permissions.ToastCorner;
            _toast.Show(original, corrected, RequestUndo);
        });
    }

    private void HideToast()
    {
        Dispatcher.Invoke(() => _toast.Hide());
    }

    /// <summary>Called from the toast's Undo button on the UI thread.</summary>
    private void RequestUndo()
    {
        _jobs.Writer.TryWrite(new Job(null, ResetContext: false, 0, UndoRequest: true));
    }

    /// <summary>True when the window the user is typing in belongs to an app the user allowed.</summary>
    private bool IsAllowedWindowActive()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;

        GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return _permissions.IsAllowed(process.ProcessName);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
