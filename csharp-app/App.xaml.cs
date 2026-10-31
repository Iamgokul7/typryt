using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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

    // The last characters typed on screen since the last reset (worker thread only).
    private readonly StringBuilder _tail = new();
    private const int TailLength = 12;

    // Time with no new keys before a replacement is made.
    private const int QuietMilliseconds = 30;

    // Wait before the read-back after a correction, so the typed keys have landed (Notepad reads take about 85 ms).
    private const int VerifyDelayMilliseconds = 150;

    // How many read-backs ran, and how many were skipped because typing continued. Shown in the log.
    private static int _verifyChecks;
    private static int _verifySkipped;

    // Spacing or punctuation that the engine's punctuation fix may change.
    private static readonly Regex NeedsPunctuationFix = new(@"[ \t]{2}|[ \t][.,!?;:]|([.!?,])\1", RegexOptions.Compiled);

    // Tray icon, engine process, and dashboard window. All used on the UI thread.
    private TrayIcon? _tray;
    private EngineLauncher? _engine;
    private MainWindow? _main;
    private bool _exiting;

    // The last foreground window and whether it was allowed, kept briefly (hook thread only).
    private const int WindowCacheMilliseconds = 300;
    private IntPtr _cachedWindow;
    private bool _cachedAllowed;
    private long _cachedAtTicks;

    // FocusGeneration is the FocusGuard generation when the key was typed. A job from another generation is dropped.
    // KeyDownCount is KeyboardHook.RealKeyDownCount when the key was typed. A replacement is cancelled if more keys came since.
    private sealed record Job(CompletedWord? Word, bool ResetContext, int KeySequence, bool UndoRequest = false, char? Typed = null, bool Backspace = false, int FocusGeneration = 0, int KeyDownCount = -1, int HoldToken = 0, char? HeldDelimiter = null, long KeyTicks = 0);

    // How long a delimiter may be held while its word is looked up and corrected: Space and punctuation, then Enter and Tab.
    private const int ShortHoldMilliseconds = 150;
    private const int LongHoldMilliseconds = 300;

    // Word jobs since the hook callback times were last written to the log (worker thread only).
    private int _wordsSinceHookLog;

    // The FocusGuard generation and key count of the job being worked on (worker thread only).
    private int _jobFocus;
    private int _jobKeyDowns = -1;

    private sealed record ScreenWord(string Text, string Trail);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one copy may run. Two copies would each install a keyboard hook and correct every word twice.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool firstCopy);
        if (!firstCopy)
        {
            // Tell the running copy to show its window, then leave. The running copy may bring itself to the front.
            AllowSetForegroundWindow(-1);
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
                // The running copy has not created its signal yet: nothing to tell.
            }
            Shutdown();
            return;
        }
        ListenForSecondLaunch();

        Log.PurgeWordLogsOnce();
        Log.PruneOld();
        Log.Write("app", $"started {Environment.ProcessPath}");

        _permissions.Load();
        if (!_permissions.FirstRunDone)
        {
            RunFirstLaunch();
        }

        _keyboardHook = new KeyboardHook();
        _keyboardHook.KeyPressed += OnKeyPressed;
        _keyboardHook.AltUPressed += OnAltUPressed;
        _keyboardHook.MouseClicked += OnMouseClicked;
        _keyboardHook.Install();
        FocusGuard.Start();
        _keyboardHook.UndoVirtualKey = UndoKeyFor(_permissions.UndoLetter);
        _permissions.Changed += ApplySettings;

        _worker = Task.Run(() => ProcessJobsAsync(_shutdown.Token));

        // The engine starts in the background, so the splash is not held up by it.
        _engine = new EngineLauncher();
        Task.Run(() =>
        {
            try
            {
                bool running = _engine.StartIfNeeded();
                Log.Write("engine", running ? "engine is running" : "engine did not start");
                _engine.StartWatchdog(() => ShowMessage("Typryt's correction engine stopped and could not be restarted. Corrections are off until Typryt is restarted."));
            }
            catch (Exception ex)
            {
                Log.Error("engine start", ex);
            }
        });

        // The app lives in the tray. The dashboard opens only when asked for.
        _tray = new TrayIcon(_permissions.MasterEnabled);
        _tray.OpenDashboardRequested += ShowDashboard;
        _tray.OpenHistoryRequested += ShowDashboard;
        _tray.OpenLogFolderRequested += () => Process.Start("explorer.exe", $"\"{Log.Folder}\"");
        _tray.ToggleRequested += ToggleCorrections;
        _tray.ExitRequested += ExitApp;

        ThemeManager.Apply(_permissions.Theme);
        new SplashScreen().Show();
    }

    private const string ShowSignalName = "Typryt.ShowWindow";
    private EventWaitHandle? _showSignal;

    /// <summary>A second launch sets this signal. The window is brought to the front on the UI thread.</summary>
    private void ListenForSecondLaunch()
    {
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var signal = _showSignal;
        var stop = _shutdown.Token;
        Task.Run(() =>
        {
            var handles = new WaitHandle[] { signal, stop.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        ShowDashboard();
                        if (_main is not null)
                        {
                            // Activate alone only flashes the taskbar when another window has the focus.
                            _main.Topmost = true;
                            _main.Topmost = false;
                        }
                    });
                }
                catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
                {
                    return;
                }
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

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
        FocusGuard.Stop();
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

        // A password field counts as a window where corrections are not allowed: its keys are not buffered at all.
        bool allowed = _permissions.MasterEnabled && !FocusGuard.Blocked && IsAllowedWindowActive();
        int focus = FocusGuard.Generation;
        int keyDowns = KeyboardHook.RealKeyDownCount;
        if (allowed != _lastWindowAllowed)
        {
            // Focus moved in or out of Notepad: the engine's context no longer matches the text.
            _lastWindowAllowed = allowed;
            _wordDetector.OnKey(new KeyInput(null, IsBackspace: false, IsBreak: true));
            _jobs.Writer.TryWrite(new Job(null, ResetContext: true, sequence));
        }
        if (!allowed) return;
        long keyTicks = Stopwatch.GetTimestamp();

        // Enter and Tab end a word like Space does. The key is held while the word is corrected, so the correction is in
        // place before a message is sent or focus moves on. When it cannot be held, the word is left as typed.
        if (key.Boundary != '\0')
        {
            var ended = _wordDetector.OnBoundary(key.Boundary);
            if (ended is { IsCorrectable: true })
            {
                int token = TryHold(LongHoldMilliseconds);
                if (token != 0)
                {
                    _jobs.Writer.TryWrite(new Job(ended, ResetContext: false, sequence, FocusGeneration: focus, KeyDownCount: keyDowns, HoldToken: token, KeyTicks: keyTicks));
                }
            }
            _jobs.Writer.TryWrite(new Job(null, ResetContext: true, sequence));
            return;
        }

        // Space and punctuation that end a word: the key is held while the word is corrected, then delivered.
        if (key.Character is { } ending && !key.IsBreak && !key.IsBackspace && IsHeldTerminator(ending))
        {
            var done = _wordDetector.OnKey(key);
            int token = done is { IsCorrectable: true } ? TryHold(ShortHoldMilliseconds) : 0;
            if (token != 0)
            {
                _jobs.Writer.TryWrite(new Job(done, ResetContext: false, sequence, FocusGeneration: focus, KeyDownCount: keyDowns, HoldToken: token, HeldDelimiter: ending, KeyTicks: keyTicks));
                return;
            }
            _jobs.Writer.TryWrite(new Job(null, ResetContext: false, sequence, Typed: ending, FocusGeneration: focus, KeyDownCount: keyDowns));
            if (done is not null)
            {
                _jobs.Writer.TryWrite(new Job(done, ResetContext: false, sequence, FocusGeneration: focus, KeyDownCount: keyDowns, KeyTicks: keyTicks));
            }
            return;
        }

        // Keys that move the caret or start new text end the context, so the next word
        // does not refer to text on screen before the break.
        if (!key.IsBackspace && (key.Character is null || key.IsBreak || EndsContext(key.Character.Value)))
        {
            _jobs.Writer.TryWrite(new Job(null, ResetContext: true, sequence));
        }

        // The text typed since the last reset, kept in order with the corrections so punctuation fixes see what is on screen.
        if (key.IsBackspace)
        {
            _jobs.Writer.TryWrite(new Job(null, ResetContext: false, sequence, Backspace: true));
        }
        else if (key.Character is { } typed && !key.IsBreak)
        {
            _jobs.Writer.TryWrite(new Job(null, ResetContext: false, sequence, Typed: typed, FocusGeneration: focus, KeyDownCount: keyDowns));
        }

        var completed = _wordDetector.OnKey(key);
        if (completed is not null)
        {
            _jobs.Writer.TryWrite(new Job(completed, ResetContext: false, sequence, FocusGeneration: focus, KeyDownCount: keyDowns));
        }
    }

    /// <summary>
    /// Runs on the hook thread. A click moves the caret, so the typing context ends here, and any
    /// correction still in progress is dropped (it would act on the wrong text).
    /// </summary>
    private void OnMouseClicked()
    {
        int sequence = Interlocked.Increment(ref _keySequence);
        _wordDetector.OnKey(new KeyInput(null, IsBackspace: false, IsBreak: true));
        _jobs.Writer.TryWrite(new Job(null, ResetContext: true, sequence));
    }

    /// <summary>Runs on the hook thread. Takes Alt+U only in Notepad, and queues the undo.</summary>
    private bool OnAltUPressed()
    {
        if (!_permissions.MasterEnabled || FocusGuard.Blocked || !IsAllowedWindowActive()) return false;
        _jobs.Writer.TryWrite(new Job(null, ResetContext: false, 0, UndoRequest: true, KeyDownCount: KeyboardHook.RealKeyDownCount));
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

    private static bool IsHeldTerminator(char c) => c is ' ' or ',' or '!' or '?' or ';' or ':';

    /// <summary>
    /// Holds the key being handled so its word can be corrected first. Returns the hold's token, or 0 when no hold is
    /// safe: the engine is down, the field is a password or unknown, Ctrl, Alt or Win is down, a dead key or an input
    /// method is composing, or the test switch for the earlier way is on. Runs on the hook thread.
    /// </summary>
    private static int TryHold(int capMilliseconds)
    {
        if (InputMarks.OldPath || !EngineState.Up || FocusGuard.Blocked) return 0;
        if (KeyboardHook.ShortcutModifierDown() || KeyboardHook.DeadKeyInvolved || KeyboardHook.ForegroundUsesImeLayout()) return 0;
        return KeyboardHook.BeginHold(capMilliseconds);
    }

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
                        _jobKeyDowns = job.KeyDownCount;
                        await UndoLastCorrectionAsync(ct);
                    }
                    else if (job.ResetContext)
                    {
                        _lastWord = null;
                        _tail.Clear();
                        await _api.ResetAsync(SessionId, ct);
                    }
                    else if (job.Backspace)
                    {
                        if (_tail.Length > 0) _tail.Length--;
                    }
                    else if (job.Typed is char typedChar)
                    {
                        if (FocusUnchanged(job)) await HandleTypedAsync(typedChar, job.KeySequence, ct);
                    }
                    else if (job.Word is not null)
                    {
                        try
                        {
                            if (FocusUnchanged(job)) await ProcessWordAsync(job.Word, job.KeySequence, ct, job.HoldToken, job.KeyTicks);
                        }
                        finally
                        {
                            // Whatever happened, the held key is delivered. Only the first call to EndHold does anything.
                            KeyboardHook.EndHold(job.HoldToken);
                        }

                        // The delimiter is on screen now: the typed text and the punctuation check follow it.
                        if (job.HeldDelimiter is char delimiter && FocusUnchanged(job))
                        {
                            await HandleTypedAsync(delimiter, job.KeySequence, ct);
                        }
                        if (++_wordsSinceHookLog >= 200)
                        {
                            _wordsSinceHookLog = 0;
                            Log.Write("hook", KeyboardHook.CallbackSummary());
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A failed correction leaves the text alone. The error is logged and counted.
                    Log.Error("job", ex);
                    _lastWord = null;
                    _tail.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Focus moved since this key was typed: the text may belong to another control (a password field, for
    /// example), so the job is dropped and the context cleared. Also records the generation for the checks
    /// made just before the engine is contacted.
    /// </summary>
    private bool FocusUnchanged(Job job)
    {
        _jobFocus = job.FocusGeneration;
        _jobKeyDowns = job.KeyDownCount;
        if (job.FocusGeneration == FocusGuard.Generation) return true;
        _lastWord = null;
        _tail.Clear();
        return false;
    }

    /// <summary>
    /// Adds a typed character to the tail, then fixes punctuation when a space or mark ends a piece of it.
    /// A period waits for the next key, so "..." can become an ellipsis; "done.." is fixed by the next space.
    /// </summary>
    private async Task HandleTypedAsync(char typed, int keySequence, CancellationToken ct)
    {
        _tail.Append(typed);
        if (_tail.Length > TailLength) _tail.Remove(0, _tail.Length - TailLength);

        // A period never triggers the check: the fix must see the screen as it is after the next key.
        // "..." becomes an ellipsis when the space after it is typed.
        if (typed is ' ' or ',' or '!' or '?' or ';' or ':') await FixPunctuationAsync(keySequence, ct);
    }

    /// <summary>
    /// Asks the engine to fix the tail and replaces it on screen. Skipped when the user has typed more
    /// since the key that started the check, because the screen then differs from the tail.
    /// </summary>
    private async Task FixPunctuationAsync(int keySequence, CancellationToken ct)
    {
        string before = _tail.ToString();
        if (!NeedsPunctuationFix.IsMatch(before)) return;

        if (!await FocusGuard.IsSafeAsync(_jobFocus))
        {
            _tail.Clear();
            return;
        }

        string? after = await _api.PunctuateAsync(before, ct);
        if (after is null || after == before) return;
        if (!await StillQuietAsync(keySequence, ct)) return;

        ReplaceResult result = TextReplacer.ReplaceBeforeCaret(before, after, keyDownCount: _jobKeyDowns);
        Log.Write("punct", $"punctuation fix {before.Length} -> {after.Length} characters ({result})");
        _lastWord = null;
        if (result != ReplaceResult.Done)
        {
            _tail.Clear();
            return;
        }

        _tail.Clear().Append(after);
    }

    /// <summary>
    /// True when no key has been typed for a short time after the key that started this work. Replacing
    /// text while the user is still typing can delete the wrong characters, so the work waits for a pause
    /// and is dropped if typing continues.
    /// </summary>
    private async Task<bool> StillQuietAsync(int keySequence, CancellationToken ct)
    {
        await Task.Delay(QuietMilliseconds, ct);
        return Volatile.Read(ref _keySequence) == keySequence;
    }

    /// <summary>Keeps the tail in step with the screen after a correction or undo. Clears it if the tail does not end with the old text.</summary>
    private void ReplaceTail(string oldSuffix, string newSuffix)
    {
        if (_tail.ToString().EndsWith(oldSuffix, StringComparison.Ordinal))
        {
            _tail.Length -= oldSuffix.Length;
            _tail.Append(newSuffix);
        }
        else
        {
            _tail.Clear();
        }
    }

    private async Task ProcessWordAsync(CompletedWord word, int keySequence, CancellationToken ct, int holdToken = 0, long keyTicks = 0)
    {
        var sw = Stopwatch.StartNew();
        bool held = holdToken != 0;

        // Stage times for the log (no text): from the key to the start of this work, then the stages of the work.
        long queueMs = keyTicks == 0 ? 0 : (Stopwatch.GetTimestamp() - keyTicks) * 1000 / Stopwatch.Frequency;
        long safetyMs = 0, engineMs = 0, replaceMs = 0;
        double holdMs = 0;
        void Release(bool changed)
        {
            if (!held) return;
            double heldFor = KeyboardHook.EndHold(holdToken);
            if (heldFor < 0) return;
            holdMs = heldFor;
            Log.Write("hold", $"{heldFor:0} ms (engine {engineMs} ms, {(changed ? "changed" : "unchanged")})");
        }

        // What of the trail is on screen while the word is corrected. A held delimiter is not delivered yet.
        string onScreenTrail = held ? word.Trail[..^1] : word.Trail;

        // Test switch only: makes one correction fail, so the test can check that the error is logged.
        if (InputMarks.TestInput && Environment.GetEnvironmentVariable("TYPRYT_TEST_FORCE_ERROR") == "1")
        {
            throw new InvalidOperationException("forced error for the test");
        }

        // Words the pre-filters skip never reach the engine, so the engine's previous word
        // is no longer the word before this one. Revisions can't be trusted after that.
        // Words the user undid are never corrected again. The engine keeps that list and leaves
        // those words unchanged, so only the pre-filters are checked here.
        if (!word.IsCorrectable)
        {
            _lastWord = null;
            return;
        }

        // Nothing typed in a password field, or in a control that cannot be identified, reaches the engine.
        var stage = Stopwatch.StartNew();
        if (!await FocusGuard.IsSafeAsync(_jobFocus))
        {
            _lastWord = null;
            _tail.Clear();
            return;
        }
        safetyMs = stage.ElapsedMilliseconds;

        // A held key waits for the engine only as long as the hold lasts.
        using var budget = held ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        budget?.CancelAfter(KeyboardHook.HoldRemainingMilliseconds(holdToken));

        stage.Restart();
        var response = await _api.CorrectAsync(
            word.Text,
            SessionId,
            _permissions.AbbreviationsEnabled,
            budget?.Token ?? ct,
            _permissions.LearningEnabled,
            _permissions.AutoCapitalizeEnabled,
            _lastWord?.Trail ?? "");
        // The log never holds typed text: only timings, lengths and outcomes.
        engineMs = stage.ElapsedMilliseconds;
        Log.Write("engine", $"answered in {sw.ElapsedMilliseconds} ms" + (response is null ? " (no answer)" : $", {(response.Correction == word.Text ? "unchanged" : "changed")} (confidence {response.Confidence:0.00})"));
        if (response is null)
        {
            _lastWord = null;
            return;
        }

        // With the delimiter held, keys typed meanwhile wait too, so they cannot reach the text: no check is needed.
        if (!held && Volatile.Read(ref _keySequence) != keySequence)
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

        if (held && corrected == word.Text && revisedPrevious is null)
        {
            // Nothing to change: the held key goes straight to the app.
            Release(changed: false);
        }

        if (!held && !await StillQuietAsync(keySequence, ct))
        {
            // Typing continued, so the word stays as typed. It is still the word before the next one.
            _lastWord = new ScreenWord(word.Text, word.Trail);
            return;
        }

        stage.Restart();
        ReplaceResult result = ReplaceResult.Done;
        int keyDownGuard = held ? -1 : _jobKeyDowns;
        if (revisedPrevious is not null && previous is not null)
        {
            // Rewrite the earlier word, its trail, this word, and its trail in one go.
            result = TextReplacer.ReplaceBeforeCaret(
                previous.Text + previous.Trail + word.Text + onScreenTrail,
                revisedPrevious + previous.Trail + corrected + onScreenTrail,
                keyDownCount: keyDownGuard,
                holdToken: holdToken);
        }
        else if (corrected != word.Text)
        {
            result = TextReplacer.ReplaceBeforeCaret(word.Text + onScreenTrail, corrected + onScreenTrail, keyDownCount: keyDownGuard, holdToken: holdToken);
        }
        replaceMs = stage.ElapsedMilliseconds;
        Release(changed: true);

        // Notepad corrected the word itself first: the screen already shows the corrected form, so the tail and
        // the last word follow the screen, but nothing is counted, shown, or recorded for undo.
        bool alreadyCorrect = result == ReplaceResult.AlreadyCorrect;

        if (result != ReplaceResult.Done && !alreadyCorrect)
        {
            // Nothing was changed. Forget the earlier word, so no later revision uses it.
            Log.Write("replace", $"not applied ({result}) for a word of {word.Text.Length} letters");
            _lastWord = null;
            return;
        }

        if (revisedPrevious is not null && previous is not null)
        {
            ReplaceTail(
                previous.Text + previous.Trail + word.Text + onScreenTrail,
                revisedPrevious + previous.Trail + corrected + onScreenTrail);
        }
        else if (corrected != word.Text)
        {
            ReplaceTail(word.Text + onScreenTrail, corrected + onScreenTrail);
        }

        _lastWord = new ScreenWord(corrected, word.Trail);

        if (corrected != word.Text && !alreadyCorrect)
        {
            // After Enter or Tab the caret is on a new line or in another control (or the message is sent), so an undo
            // would edit the wrong place: it is refused instead.
            if (word.Terminator is '\r' or '\t') _undo.Clear();
            else _undo.Record(word.Text, corrected, word.Trail, keySequence);
            _stats.RecordCorrection(word.Text, corrected);
            ShowToast(word.Text, corrected);
            Log.Write("correct", $"corrected {word.Text.Length} -> {corrected.Length} letters in {sw.ElapsedMilliseconds} ms (key to start {queueMs} ms, safety check {safetyMs} ms, engine {engineMs} ms, replace {replaceMs} ms, key held {holdMs:0} ms)");
            _ = VerifyAfterAsync(corrected + word.Trail, keySequence);
        }
    }

    /// <summary>
    /// Reads the text before the caret after a correction and logs whether it is what was typed. It only
    /// logs: a mismatch is reported so it can be investigated, not repaired. Skipped when typing continued,
    /// because the text before the caret then legitimately differs.
    /// </summary>
    private async Task VerifyAfterAsync(string expected, int keySequence)
    {
        try
        {
            await Task.Delay(VerifyDelayMilliseconds);
            Interlocked.Increment(ref _verifyChecks);
            if (Volatile.Read(ref _keySequence) != keySequence)
            {
                int skipped = Interlocked.Increment(ref _verifySkipped);
                Log.Write("verify", $"skipped: typing continued ({skipped} of {_verifyChecks} checks skipped so far)");
                return;
            }

            string? actual = TextReplacer.TextBeforeCaret(expected.Length, readSlowApps: true);

            // The read can take most of a second in some apps. If a key arrived during it, the text may
            // have changed under the read, so the result is not compared.
            if (Volatile.Read(ref _keySequence) != keySequence)
            {
                int skipped = Interlocked.Increment(ref _verifySkipped);
                Log.Write("verify", $"skipped: typing during the read ({skipped} of {_verifyChecks} checks skipped so far)");
                return;
            }

            if (actual is null) Log.Write("verify", "not readable in this app");
            else if (actual == expected) Log.Write("verify", $"ok ({expected.Length} characters)");
            else Log.Write("verify", $"MISMATCH: expected {expected.Length} characters, found {actual.Length} that differ");
        }
        catch (Exception ex)
        {
            Log.Error("verify", ex);
        }
    }

    /// <summary>Restores the word a correction replaced, if the undo window is still open and nothing was typed since.</summary>
    private async Task UndoLastCorrectionAsync(CancellationToken ct)
    {
        var attempt = _undo.Attempt(Volatile.Read(ref _keySequence));
        var correction = attempt.Correction;
        Log.Write("undo", $"request: {(correction is null ? attempt.Refusal.ToString() : "allowed")}");
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
        // Undo deletes by counting characters, so the text must be read first and must still match.
        var result = TextReplacer.ReplaceBeforeCaret(
            correction.Corrected + correction.Trail,
            correction.Original + correction.Trail,
            requireReadable: true,
            keyDownCount: _jobKeyDowns);
        if (result != ReplaceResult.Done)
        {
            // The text was changed since the correction, so nothing is undone.
            Log.Write("undo", $"not applied ({result}) for a correction of {correction.Corrected.Length} letters");
            ShowMessage("Can't undo: the text changed");
            _lastWord = null;
            return;
        }

        Log.Write("undo", $"undone: {correction.Corrected.Length} -> {correction.Original.Length} letters");
        ReplaceTail(correction.Corrected + correction.Trail, correction.Original + correction.Trail);
        _lastWord = new ScreenWord(correction.Original, correction.Trail);
        _stats.RecordUndo(correction.Original, correction.Corrected);

        // An undo reverts only this correction. The word is not blocked: when it is typed again it can be corrected.
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

    /// <summary>
    /// True when the window the user is typing in belongs to an app the user allowed. The answer is
    /// kept for a short time for the same window, so the keyboard hook does not look up the process
    /// on every key. Runs on the hook thread only.
    /// </summary>
    private bool IsAllowedWindowActive()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;

        long now = Environment.TickCount64;
        if (window == _cachedWindow && now - _cachedAtTicks < WindowCacheMilliseconds)
        {
            return _cachedAllowed;
        }

        bool allowed = false;
        GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            allowed = _permissions.IsAllowed(process.ProcessName);
        }
        catch (ArgumentException)
        {
        }

        _cachedWindow = window;
        _cachedAllowed = allowed;
        _cachedAtTicks = now;
        return allowed;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
