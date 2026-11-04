using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Typryt;

/// <summary>
/// Global low-level keyboard hook (WH_KEYBOARD_LL). Raises KeyPressed for every
/// key-down the user makes in any application. Keys injected by this app are
/// ignored, so the app does not react to its own corrections.
/// The callback must stay fast: Windows drops hooks that block for too long.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLKHF_EXTENDED = 0x01;
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private const int VK_BACK = 0x08;
    private const int VK_TAB = 0x09;
    private const int VK_RETURN = 0x0D;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_CAPITAL = 0x14;
    private const int VK_PRIOR = 0x21;   // Page Up
    private const int VK_NEXT = 0x22;    // Page Down
    private const int VK_END = 0x23;
    private const int VK_HOME = 0x24;
    private const int VK_LEFT = 0x25;
    private const int VK_UP = 0x26;
    private const int VK_RIGHT = 0x27;
    private const int VK_DOWN = 0x28;
    private const int VK_DELETE = 0x2E;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private LowLevelKeyboardProc? _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _mouseProc;
    private IntPtr _mouseHookId = IntPtr.Zero;

    // Alt key state tracked from the hook's own key events.
    private bool _altDown;

    private const int VK_U = 0x55;

    public event Action<KeyInput>? KeyPressed;

    /// <summary>
    /// Raised when a mouse button goes down anywhere. A click can move the caret, so the text before
    /// the caret is no longer what the app has typed. Must return fast.
    /// </summary>
    public event Action? MouseClicked;

    /// <summary>Test only: a fault injection chord was pressed (the virtual key).</summary>
    public event Action<int>? TestFault;

    /// <summary>
    /// Raised when Alt+U is pressed. A handler that returns true takes the key: Windows
    /// does not pass it to the focused app. Must return fast.
    /// </summary>
    public event Func<bool>? AltUPressed;

    /// <summary>
    /// The letter that undoes a correction when pressed with Alt (a virtual-key code, A to Z).
    /// Set from the settings; read on the hook thread.
    /// </summary>
    public int UndoVirtualKey { get; set; } = VK_U;

    public void Install()
    {
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule
            ?? throw new InvalidOperationException("No main module for the keyboard hook.");
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        _current = this;
        if (_hookId == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard hook.");
        }

        _mouseProc = MouseCallback;
        _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(module.ModuleName), 0);
        if (_mouseHookId == IntPtr.Zero)
        {
            // Typing still works without it; only the click reset is lost.
            Log.Write("hook", $"mouse hook not installed (error {Marshal.GetLastWin32Error()})");
        }
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        if (_mouseHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int message = wParam.ToInt32();
        if (nCode >= 0 && message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            // Clicks on this app's own windows (the toast's Undo button) do not move the caret in the typed app.
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (!IsOwnWindowAt(info.pt))
            {
                CancelHold();
                MouseClicked?.Invoke();
            }
        }
        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private static bool IsOwnWindowAt(POINT point)
    {
        IntPtr window = WindowFromPoint(point);
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            return CallbackCore(nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            // The hook must never throw into Windows. The app pauses itself, every held key is delivered, and this
            // key goes on to the application unless it is already waiting in the held keys (then it is delivered with them).
            Log.Fault("keyboard hook", ex);
            bool queued = _holdStartedByThisKey;
            _holdStartedByThisKey = false;
            _pendingKeyValid = false;
            try { SafeMode.Enter(SafeMode.HookFault); } catch (Exception) { }
            return queued ? (IntPtr)1 : CallNextHookEx(_hookId, nCode, wParam, lParam);
        }
        finally
        {
            RecordCallback(Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>Ends whatever hold is running and delivers the held keys, in order. Safe from any thread.</summary>
    internal static void ReleaseAllHolds()
    {
        int token;
        lock (HoldGate)
        {
            token = _holdToken;
            if (!Holding && Held.IsEmpty) return;
            _holdCancelled = true;
        }
        if (EndHold(token, "safe mode") < 0)
        {
            // No hold state to end, yet keys are queued: send them.
            ReplayHeldKeys();
        }
    }

    private IntPtr CallbackCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int message = wParam.ToInt32();
        bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
        bool isUp = message == WM_KEYUP || message == WM_SYSKEYUP;
        if (nCode >= 0 && (isDown || isUp))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Keys this app sent itself are ignored. Keys from other programs are ignored too,
            // unless the test switch is on (see InputMarks).
            bool own = info.dwExtraInfo == InputMarks.Own;
            bool injected = (info.flags & LLKHF_INJECTED) != 0;
            bool real = !own && (!injected || InputMarks.TestInput);
            int vk = (int)info.vkCode;

            if (real)
            {
                TrackModifier(vk, isDown);

                // Counted before the hold decision below, so a key typed just as a correction starts is
                // either seen by that correction (which then cancels) or held by it. It cannot slip between.
                if (isDown) Interlocked.Increment(ref _realKeyDowns);
            }

            // Track Alt from its own key events. Asking Windows for the Alt state at the moment
            // U is pressed can be out of date when the keys are pressed close together.
            if (real && vk is VK_MENU or VK_LMENU or VK_RMENU)
            {
                _altDown = isDown;
            }

            if (real && isDown && InputMarks.TestInput && vk is >= 0x77 and <= 0x7B && _altDown && IsDown(VK_CONTROL) && IsDown(VK_SHIFT))
            {
                // Test only (needs the test-input switch): Ctrl+Alt+Shift+F9 a hook fault, F10 five forced hold releases,
                // F11 the engine given up, F12 an unhandled exception on the UI thread, F8 turn on again (as the tray does).
                TestFault?.Invoke(vk);
                if (vk == 0x78) throw new InvalidOperationException("injected hook fault");
                return (IntPtr)1;
            }

            if (real && isDown && vk == UndoVirtualKey && _altDown && !IsDown(VK_CONTROL))
            {
                bool taken = AltUPressed?.Invoke() ?? false;
                if (taken)
                {
                    // Swallowing U leaves Alt pressed alone, and Windows would open Notepad's menu bar
                    // when Alt is released. A harmless key pressed while Alt is held prevents that.
                    SendHarmlessKey();
                    return (IntPtr)1;
                }
            }

            // A correction is being typed in an app that needs its keys paced (Notepad): the user's keys, Shift,
            // Ctrl and Win included, wait here and are sent again, in order, afterwards. Alt and Caps Lock are not
            // held: a held Alt release would stay down while the correction is typed, and Windows would then read
            // the correction's keys as Alt shortcuts.
            // The hold is re-checked under the lock that also ends it, so a key cannot be queued after the last
            // replay has found the queue empty (it would stay swallowed until some later correction).
            if (real && Holding && !IsAltOrCapsLock(vk))
            {
                lock (HoldGate)
                {
                    if (Holding)
                    {
                        KeyInput? input = isDown && !IsModifierKey(vk) ? Translate(info, heldState: true) : null;
                        Held.Enqueue(new HeldKey((ushort)vk, (ushort)info.scanCode, isDown, (info.flags & LLKHF_EXTENDED) != 0, input));
                        if (InputMarks.TestInput)
                        {
                            Log.Write("shift_diag", $"queued vk={vk} {(isDown ? "down" : "up")} modifier={IsModifierKey(vk)}");
                        }
                        return (IntPtr)1;
                    }
                }
            }

            // Modifier keys alone (Alt, Shift, Ctrl, Win, Caps Lock) type nothing. Reporting them would
            // count as typing and would block undo, because Alt+U would look like typing after a correction.
            if (real && isDown && !IsModifierKey(vk))
            {
                // A handler may start a hold on this very key (BeginHold): the key is then queued instead of delivered.
                _pendingKey = new HeldKey((ushort)vk, (ushort)info.scanCode, true, (info.flags & LLKHF_EXTENDED) != 0, null);
                _pendingKeyValid = true;
                _holdStartedByThisKey = false;
                try
                {
                    KeyPressed?.Invoke(Translate(info));
                }
                finally
                {
                    _pendingKeyValid = false;
                }
                if (_holdStartedByThisKey)
                {
                    _holdStartedByThisKey = false;
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static KeyboardHook? _current;

    /// <summary>True while keys are held. Keys from the user wait until the hold ends.</summary>
    private static volatile bool Holding;

    private static readonly ConcurrentQueue<HeldKey> Held = new();

    // Taken only for a few instructions, never around a SendInput call (the hook would wait for it).
    private static readonly object HoldGate = new();

    // Pause after each replayed key, the same as the one used when typing a correction.
    private const int ReplayKeyStepMilliseconds = 12;

    private readonly record struct HeldKey(ushort Vk, ushort Scan, bool IsDown, bool Extended, KeyInput? Input);

    // A hold is in one of these states. Waiting: the word is being looked up, nothing is typed yet. Typing: the
    // correction is being typed. Releasing: the held keys are being sent again.
    private const int HoldNone = 0;
    private const int HoldWaiting = 1;
    private const int HoldTyping = 2;
    private const int HoldReleasing = 3;

    // The timer that ends a hold, whatever the worker thread is doing: after the time limit, when focus or the
    // foreground window changes, and after a mouse click. A correction that is already being typed may finish.
    private const int HoldTickMilliseconds = 10;
    private const int TypingHardCapMilliseconds = 2000;
    private static readonly System.Threading.Timer HoldTimer = new(HoldTick, null, Timeout.Infinite, Timeout.Infinite);

    // All guarded by HoldGate.
    private static int _holdState;
    private static int _holdToken;
    private static int _holdCounter;
    private static long _holdStartTimestamp;
    private static int _holdCapMilliseconds;
    private static IntPtr _holdWindow;
    private static int _holdFocus;
    private static bool _holdCancelled;

    // The key the hook is handling right now (hook thread only), so a handler can start a hold on it.
    private static HeldKey _pendingKey;
    private static bool _pendingKeyValid;
    private static bool _holdStartedByThisKey;

    // Real key-downs seen so far. A correction compares it with the value from the key that finished the word.
    private static int _realKeyDowns;

    public static int RealKeyDownCount => Volatile.Read(ref _realKeyDowns);

    // Shift, Ctrl and Win as the hook has seen them. While keys are held the system's own key state is not
    // updated for them, so the text of a held key is worked out from these.
    private static volatile bool _shiftTracked;
    private static volatile bool _ctrlTracked;

    /// <summary>True while the hook has seen Shift pressed and not yet released (including keys it is holding).</summary>
    internal static bool ShiftTracked => _shiftTracked;

    // Whether Shift was down, as far as the app being typed into knows, when the current hold began: its press was
    // delivered and its release has not been. The release may be among the held keys by now, so the live state is not used.
    private static volatile bool _holdShiftDown;

    internal static bool ShiftDownForApp => _holdShiftDown;

    private static void TrackModifier(int vk, bool down)
    {
        switch (vk)
        {
            case VK_SHIFT or 0xA0 or 0xA1:
                _shiftTracked = down;
                break;
            case VK_CONTROL or 0xA2 or 0xA3:
                _ctrlTracked = down;
                break;
        }
    }

    /// <summary>True while Shift, Ctrl or a Windows key (and Alt, when asked) is physically down.</summary>
    public static bool ModifierKeyDown(bool includeAlt) =>
        IsDown(VK_SHIFT) || IsDown(VK_CONTROL) || IsDown(0x5B) || IsDown(0x5C) || (includeAlt && IsDown(VK_MENU));

    /// <summary>True while Ctrl, Alt or a Windows key is down. Shift alone is not counted.</summary>
    internal static bool ShortcutModifierDown() => IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(0x5B) || IsDown(0x5C);

    private static bool IsAltOrCapsLock(int vk) => vk is VK_MENU or VK_LMENU or VK_RMENU or VK_CAPITAL;

    /// <summary>
    /// Starts holding the key the hook is handling right now: it is not delivered, and neither is any key typed after
    /// it, until EndHold. Call it only from the KeyPressed handler. Returns the hold's token, or 0 when no hold could
    /// be started. The hold ends by itself after capMilliseconds, or at once when focus or the foreground window
    /// changes or the mouse is clicked, whatever the worker thread is doing.
    /// </summary>
    public static int BeginHold(int capMilliseconds)
    {
        lock (HoldGate)
        {
            if (Holding || !_pendingKeyValid) return 0;
            int token = ++_holdCounter;
            _holdToken = token;
            _holdState = HoldWaiting;
            _holdCancelled = false;
            _holdStartTimestamp = Stopwatch.GetTimestamp();
            _holdCapMilliseconds = capMilliseconds;
            _holdWindow = GetForegroundWindow();
            _holdFocus = FocusGuard.Generation;
            _holdShiftDown = _shiftTracked;
            Held.Enqueue(_pendingKey);
            Holding = true;
            _holdStartedByThisKey = true;
            HoldTimer.Change(HoldTickMilliseconds, HoldTickMilliseconds);
            if (InputMarks.TestInput) Log.Write("shift_diag", $"hold opened (BeginHold) token={token}");
            return token;
        }
    }

    /// <summary>
    /// The worker is about to type the correction. False when the hold has already ended or was cancelled: the keys
    /// are on their way to the app, so nothing may be typed. Once true, the hold is not ended by the time limit.
    /// </summary>
    public static bool TryBeginTyping(int token)
    {
        lock (HoldGate)
        {
            if (_holdToken != token || _holdState != HoldWaiting || _holdCancelled) return false;
            _holdState = HoldTyping;
            return true;
        }
    }

    /// <summary>Milliseconds left of the time limit for a hold that is still waiting, or 0.</summary>
    public static int HoldRemainingMilliseconds(int token)
    {
        lock (HoldGate)
        {
            if (_holdToken != token || _holdState != HoldWaiting) return 0;
            return Math.Max(0, _holdCapMilliseconds - (int)ElapsedMilliseconds(_holdStartTimestamp));
        }
    }

    private static double ElapsedMilliseconds(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Ends the hold and sends the held keys again, in order. Safe to call more than once and from any thread:
    /// only the first call acts. Returns how long the hold lasted in milliseconds, or -1 when this call did nothing.
    /// </summary>
    public static double EndHold(int token, string? earlyReason = null)
    {
        if (token == 0) return -1;
        double held;
        lock (HoldGate)
        {
            if (_holdToken != token || _holdState is HoldNone or HoldReleasing) return -1;
            _holdState = HoldReleasing;
            held = ElapsedMilliseconds(_holdStartTimestamp);
        }
        HoldTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (earlyReason is not null) Log.Write("hold", $"released early ({earlyReason}) after {held:0} ms; the text stays as typed");
        if (InputMarks.TestInput) Log.Write("shift_diag", $"hold ending (EndHold) token={token}, replaying queue");
        ReplayHeldKeys();
        return held;
    }

    /// <summary>A mouse click ends a hold that is still waiting: the caret may have moved.</summary>
    private static void CancelHold()
    {
        if (!Holding) return;
        lock (HoldGate)
        {
            if (_holdState == HoldWaiting) _holdCancelled = true;
        }
    }

    private static void HoldTick(object? state)
    {
        int token;
        string reason;
        lock (HoldGate)
        {
            if (_holdState is HoldNone or HoldReleasing) return;
            if (_holdState == HoldWaiting)
            {
                if (_holdCancelled) reason = "mouse click";
                else if (GetForegroundWindow() != _holdWindow || FocusGuard.Generation != _holdFocus) reason = "focus changed";
                else if (ElapsedMilliseconds(_holdStartTimestamp) >= _holdCapMilliseconds) reason = "time limit";
                else return;
                _holdCancelled = true;
            }
            else if (ElapsedMilliseconds(_holdStartTimestamp) >= TypingHardCapMilliseconds) reason = "typing took too long";
            else return;
            token = _holdToken;
        }
        EndHold(token, reason);
        if (reason == "time limit") SafeMode.NoteForcedRelease();
    }

    /// <summary>
    /// Runs action with the user's keys held, then sends the held keys again in the order they were pressed
    /// (SendInput, with their scan codes). Holding stops only when the queue is empty, so no key can slip in
    /// between the held ones. Used for corrections that are not made at a word boundary (undo, punctuation).
    /// </summary>
    public static T HoldKeysWhile<T>(Func<T> action)
    {
        // A hold that is still sending its keys again finishes first.
        var wait = Stopwatch.StartNew();
        while (Holding && wait.ElapsedMilliseconds < 500) Thread.Sleep(2);

        int token;
        lock (HoldGate)
        {
            token = ++_holdCounter;
            _holdToken = token;
            _holdState = HoldTyping;
            _holdCancelled = false;
            _holdStartTimestamp = Stopwatch.GetTimestamp();
            Holding = true;
            HoldTimer.Change(HoldTickMilliseconds, HoldTickMilliseconds);
        }
        if (InputMarks.TestInput) Log.Write("shift_diag", $"hold opened (HoldKeysWhile) token={token}");
        try
        {
            return action();
        }
        finally
        {
            EndHold(token);
        }
    }

    // How long a replayed modifier-down may wait, with the hold kept open, for its matching up to arrive
    // before the hold is allowed to end anyway. See ReplayHeldKeys for why this exists.
    private const int UnmatchedModifierGraceMilliseconds = 200;

    /// <summary>
    /// Sends every held key again, in the order it was queued. A capital letter is four separate low-level
    /// events (modifier-down, letter-down, letter-up, modifier-up); if the hold ends the instant the queue is
    /// momentarily empty - right between a replayed modifier-down and its still-in-flight matching up - that up
    /// gets delivered LIVE (Holding is already false) while the down is still working its way out through this
    /// delayed replay, and the two can reach the target app out of order or not at all: a modifier that looks
    /// down with no up ever following it, i.e. a stuck Shift. So the hold is kept open - not ended just because
    /// the queue is briefly empty - for as long as a replayed modifier-down has not yet been followed by its own
    /// matching up. Holding stays true means the hook keeps queuing new real keys (including that up) instead of
    /// letting them bypass the queue, so the up is captured and replayed, in order, right behind everything already
    /// queued ahead of it - nothing is skipped or reordered to achieve this; only ending the hold is delayed. A
    /// short grace period bounds the wait in case the up never arrives (held across unrelated corrections), after
    /// which the hold ends anyway, matching the one case this cannot fix generically: a modifier held down past
    /// the correction, by design (GetAsyncKeyState is deliberately never consulted here; only the queue is).
    /// </summary>
    private static void ReplayHeldKeys()
    {
        var unmatchedModifierDown = new HashSet<int>();
        long blockedSince = 0;

        while (true)
        {
            while (Held.TryDequeue(out HeldKey held))
            {
                if (InputMarks.TestInput)
                {
                    bool hasPartner = held.IsDown
                        ? unmatchedModifierDown.Contains(held.Vk)
                        : !unmatchedModifierDown.Contains(held.Vk);
                    Log.Write("shift_diag", $"replay vk={held.Vk} {(held.IsDown ? "down" : "up")} modifier={IsModifierKey(held.Vk)} partnerAlreadyTracked={hasPartner}");
                }
                TextReplacer.SendRawKey(held.Vk, held.Scan, up: !held.IsDown, extended: held.Extended);
                if (held.IsDown && held.Input is { } input) _current?.KeyPressed?.Invoke(input);
                if (IsModifierKey(held.Vk))
                {
                    if (held.IsDown) unmatchedModifierDown.Add(held.Vk);
                    else unmatchedModifierDown.Remove(held.Vk);
                }
                Thread.Sleep(ReplayKeyStepMilliseconds);
            }

            if (unmatchedModifierDown.Count == 0)
            {
                blockedSince = 0;
            }
            else if (blockedSince == 0)
            {
                blockedSince = Stopwatch.GetTimestamp();
            }
            else if (ElapsedMilliseconds(blockedSince) >= UnmatchedModifierGraceMilliseconds)
            {
                if (InputMarks.TestInput) Log.Write("shift_diag", $"grace period over, giving up waiting for: {string.Join(',', unmatchedModifierDown)}");
                unmatchedModifierDown.Clear();
                blockedSince = 0;
            }

            // Ending the hold and finding the queue empty is one step, under the lock the hook callback uses.
            lock (HoldGate)
            {
                if (Held.IsEmpty && unmatchedModifierDown.Count == 0)
                {
                    Holding = false;
                    _holdState = HoldNone;
                    if (InputMarks.TestInput) Log.Write("shift_diag", "hold closed");
                    return;
                }
            }
            if (Held.IsEmpty) Thread.Sleep(ReplayKeyStepMilliseconds);
        }
    }

    // How long the hook callback takes. The last CallbackSamples calls are kept (the hook thread writes, the worker reads).
    private const int CallbackSamples = 8192;
    private static readonly long[] CallbackTicks = new long[CallbackSamples];
    private static long _callbackCount;
    private static long _callbackMaxTicks;
    private static long _callbackSlow;

    private static void RecordCallback(long ticks)
    {
        long n = _callbackCount;
        CallbackTicks[n % CallbackSamples] = ticks;
        _callbackCount = n + 1;
        if (ticks > _callbackMaxTicks) _callbackMaxTicks = ticks;
        if (ticks * 1000 / Stopwatch.Frequency >= 30) _callbackSlow++;
    }

    /// <summary>A one-line summary of the hook callback time: calls, p50 and p99 of the recent ones, the longest ever, and calls of 30 ms or more.</summary>
    public static string CallbackSummary()
    {
        long count = Volatile.Read(ref _callbackCount);
        int n = (int)Math.Min(count, CallbackSamples);
        if (n == 0) return "no hook calls yet";
        var copy = new long[n];
        Array.Copy(CallbackTicks, copy, n);
        Array.Sort(copy);
        double Micro(long t) => t * 1_000_000.0 / Stopwatch.Frequency;
        return $"hook callback: {count} calls, recent {n}: p50 {Micro(copy[n / 2]):0} us, p99 {Micro(copy[(int)(n * 0.99)]):0} us, max ever {Micro(_callbackMaxTicks):0} us, 30 ms or more: {_callbackSlow}";
    }

    // A dead key (an accent waiting for its letter) was pressed, or the key just handled completed one. A hold is not
    // started then: the app combines the keys itself.
    private static bool _deadKeyPending;
    private static bool _keyAfterDeadKey;

    internal static bool DeadKeyInvolved => _deadKeyPending || _keyAfterDeadKey;

    /// <summary>True when the window being typed into uses a Japanese, Korean or Chinese keyboard layout, whose input method composes text itself.</summary>
    internal static bool ForegroundUsesImeLayout()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        uint thread = GetWindowThreadProcessId(window, out _);
        long primaryLanguage = GetKeyboardLayout(thread).ToInt64() & 0x3FF;
        return primaryLanguage is 0x11 or 0x12 or 0x04;
    }

    // An unassigned virtual key that no application uses, so it has no visible effect.
    private const byte VK_UNASSIGNED = 0xE8;
    private const uint KEYEVENTF_KEYUP_KB = 0x0002;

    private static void SendHarmlessKey()
    {
        keybd_event(VK_UNASSIGNED, 0, 0, new UIntPtr(0x54595052u));
        keybd_event(VK_UNASSIGNED, 0, KEYEVENTF_KEYUP_KB, new UIntPtr(0x54595052u));
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static bool IsModifierKey(int vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_CAPITAL
        or 0x5B or 0x5C        // Left and right Windows keys
        or 0xA0 or 0xA1        // Left and right Shift
        or 0xA2 or 0xA3        // Left and right Ctrl
        or 0xA4 or 0xA5;       // Left and right Alt

    /// <summary>Turns a raw key into the KeyInput the word detector understands.</summary>
    private static KeyInput Translate(KBDLLHOOKSTRUCT info, bool heldState = false)
    {
        int vk = (int)info.vkCode;

        // Shortcuts (Ctrl or Alt combinations) are never typed text.
        bool ctrl = heldState ? _ctrlTracked : IsDown(VK_CONTROL);
        if (ctrl || IsDown(VK_MENU))
        {
            // Paste, cut, undo and redo change the text without the word detector seeing the characters.
            bool edits = ctrl && vk is 0x56 or 0x58 or 0x5A or 0x59;
            return new KeyInput(null, IsBackspace: false, IsBreak: true, EditsText: edits);
        }

        if (vk == VK_BACK)
        {
            return new KeyInput(null, IsBackspace: true, IsBreak: false);
        }

        if (vk is VK_TAB or VK_RETURN)
        {
            // Ends a word like Space does, but is not typed text. The app decides whether to hold it for a correction.
            return new KeyInput(null, IsBackspace: false, IsBreak: true, Boundary: vk == VK_RETURN ? '\r' : '\t');
        }

        if (vk is VK_ESCAPE or VK_DELETE or VK_HOME or VK_END
            or VK_PRIOR or VK_NEXT or VK_LEFT or VK_UP or VK_RIGHT or VK_DOWN)
        {
            return new KeyInput(null, IsBackspace: false, IsBreak: true);
        }

        char? character = ToCharacter(vk, info.scanCode, heldState);
        return new KeyInput(character, IsBackspace: false, IsBreak: false);
    }

    /// <summary>Returns the printed character for a key, or null when it prints nothing.</summary>
    private static char? ToCharacter(int vk, uint scanCode, bool heldState)
    {
        var state = new byte[256];
        if (heldState ? _shiftTracked : IsDown(VK_SHIFT)) state[VK_SHIFT] = 0x80;
        if (IsToggled(VK_CAPITAL)) state[VK_CAPITAL] = 0x01;

        var buffer = new StringBuilder(4);
        int count = ToUnicode((uint)vk, scanCode, state, buffer, buffer.Capacity, 0);
        if (!heldState)
        {
            _keyAfterDeadKey = _deadKeyPending;
            _deadKeyPending = count < 0;
        }
        return count == 1 ? buffer[0] : null;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsToggled(int vk) => (GetKeyState(vk) & 0x0001) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out] StringBuilder pwszBuff, int cchBuff, uint wFlags);
}
