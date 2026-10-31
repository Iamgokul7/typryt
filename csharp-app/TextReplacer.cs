using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Typryt;

/// <summary>The result of a replacement attempt.</summary>
public enum ReplaceResult
{
    /// <summary>The text was replaced.</summary>
    Done,

    /// <summary>The text before the cursor was not what was expected, so nothing was changed.</summary>
    TextChanged,

    /// <summary>The keys could not be sent.</summary>
    Failed,

    /// <summary>The text before the cursor already had the new form (the app, Notepad for one, changed it first), so nothing was changed.</summary>
    AlreadyCorrect,
}

/// <summary>
/// Replaces text before the cursor. The typed characters are removed with Backspace and the new text is
/// typed as key presses, all in one batch, so the user's keys cannot land in the middle. The clipboard is
/// not used. Characters that have no key on the keyboard are typed one per call. Before anything is
/// changed, the text before the cursor is checked when the app exposes it (UI Automation).
/// </summary>
public static class TextReplacer
{
    private const ushort VK_BACK = 0x08;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CAPITAL = 0x14;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    // Pause after each key event. Notepad drops keys that arrive in a burst. The timer is set to 1 ms
    // (see TimerResolution) so this pause is really about this long, not the default 15 ms.
    private const int KeyStepMilliseconds = 15;

    // The keys are seen here before the app has shown them, so the text before the caret is read again
    // for a short time until it matches.
    private const int VerifySettleMilliseconds = 250;
    private const int VerifyPollMilliseconds = 15;

    /// <summary>
    /// Replaces expectedBefore (the text just before the cursor) with newText. Nothing is changed
    /// when the text before the cursor is known and does not match expectedBefore.
    /// </summary>
    /// <param name="requireReadable">
    /// When true (undo), the text must be read first, Notepad included, and nothing is changed if it cannot be
    /// read. Browsers cannot be read, so they are not refused.
    /// </param>
    /// <param name="keyDownCount">
    /// KeyboardHook.RealKeyDownCount when the key that finished the word was typed. The replacement is cancelled,
    /// and the text stays as typed, if any key has been typed since. -1 turns that check off.
    /// </param>
    /// <param name="holdToken">
    /// The token of a hold the caller already owns (KeyboardHook.BeginHold): the user's keys, the one that finished the
    /// word included, are already held, so the text is changed directly and the caller ends the hold. 0 when the caller has
    /// no hold: keys are then held for the length of this call.
    /// </param>
    public static ReplaceResult ReplaceBeforeCaret(string expectedBefore, string newText, bool requireReadable = false, int keyDownCount = -1, int holdToken = 0)
    {
        if (holdToken != 0)
        {
            if (!KeyboardHook.TryBeginTyping(holdToken))
            {
                Log.Write("replace", "skipped: the hold ended before the correction was ready; the text stays as typed");
                return ReplaceResult.TextChanged;
            }
            return ReplaceCore(expectedBefore, newText, requireReadable, IsBrowserInFront(), keyDownCount, shiftAllowed: true);
        }

        // The user's keys are held while the replacement is typed and typed again, in order, afterwards. A single
        // SendInput batch is not enough on its own: with a keyboard hook installed, a key typed at the same moment
        // was measured landing inside the batch (137 of 200 runs at 20 ms per key).
        bool batch = IsBrowserInFront();
        return KeyboardHook.HoldKeysWhile(() => ReplaceCore(expectedBefore, newText, requireReadable, batch, keyDownCount, shiftAllowed: false));
    }

    // Right before keys are sent: no modifier key is down (a Shift that is down would turn the replacement's
    // letters into capitals). Alt is waited for by the undo. Keys typed from the start of the hold are held,
    // so they cannot reach the text.
    private static bool ModifiersAreUp(bool requireReadable) => !KeyboardHook.ModifierKeyDown(includeAlt: !requireReadable);

    private static ReplaceResult ReplaceCore(string expectedBefore, string newText, bool requireReadable, bool batch, int keyDownCount, bool shiftAllowed)
    {
        if (expectedBefore.Length == 0) return ReplaceResult.Failed;

        // The hold is already on, so keys typed from now on cannot reach the text. A key typed before it
        // started has, and the replacement is then cancelled.
        if (keyDownCount >= 0 && KeyboardHook.RealKeyDownCount != keyDownCount)
        {
            Log.Write("replace", "cancelled: a key was typed before the replacement started; the text stays as typed");
            return ReplaceResult.TextChanged;
        }

        bool verifyAfter = false;
        if (!requireReadable && IsProcessInFront(NoReadProcesses))
        {
            // Notepad can change the word itself (its own autocorrect) before this runs, so the Backspaces are
            // counted from the text that is really there, never from the typed keys.
            var plan = PlanFromRealText(expectedBefore, newText);
            if (plan.Result is { } early) return early;
            expectedBefore = plan.RealBefore;
            verifyAfter = true;
        }
        else
        {
            string? before = ReadSettledBeforeCaret(expectedBefore, readSlowApps: requireReadable);
            if (before is not null && before != expectedBefore)
            {
                Log.Write("replace", $"skipped: the {before.Length} characters before the caret differ from the {expectedBefore.Length} that were typed");
                return ReplaceResult.TextChanged;
            }
            if (before is null && requireReadable && !IsBrowserInFront())
            {
                Log.Write("replace", $"refused: text before caret not readable, nothing changed ({expectedBefore.Length} characters)");
                return ReplaceResult.TextChanged;
            }
            Log.Write("replace", before is null
                ? $"text before caret not readable, replacing {expectedBefore.Length} characters without a check"
                : $"text before caret verified ({expectedBefore.Length} characters)");
        }

        WaitForAltRelease();
        bool capsLock = (GetKeyState(VK_CAPITAL) & 1) != 0;
        timeBeginPeriod(1);

        // A word is corrected before its delimiter is delivered, and a delimiter such as ! or ? is typed with Shift, so
        // Shift is usually still down. It is released for the correction and pressed again afterwards, so the delimiter
        // reaches the app with its Shift. Ctrl, Alt and Win are never worked around.
        bool shiftReleased = false;
        if (shiftAllowed)
        {
            if (KeyboardHook.ShortcutModifierDown())
            {
                Log.Write("replace", "cancelled: a modifier key is down; the text stays as typed");
                return ReplaceResult.TextChanged;
            }
            if (KeyboardHook.ShiftDownForApp)
            {
                SendRawKey(VK_SHIFT, ShiftScanCode, up: true, extended: false);
                shiftReleased = true;
            }
        }
        else if (!ModifiersAreUp(requireReadable))
        {
            Log.Write("replace", "cancelled: a modifier key is down; the text stays as typed");
            return ReplaceResult.TextChanged;
        }

        int shared = SharedPrefixLength(expectedBefore, newText);
        bool sent = SendEdit(expectedBefore, newText, capsLock, batch);
        // Pressed again even if the user has let go meanwhile: the release is among the held keys and follows the delimiter.
        if (shiftReleased) SendRawKey(VK_SHIFT, ShiftScanCode, up: false, extended: false);
        if (!sent) return ReplaceResult.Failed;

        Log.Write("replace", $"replaced {expectedBefore.Length} characters with {newText.Length}: {CountElements(expectedBefore, shared)} Backspaces and {newText.Length - shared} characters sent");
        if (verifyAfter) RepairIfChanged(newText, capsLock);
        return ReplaceResult.Done;
    }

    /// <summary>
    /// Turns the text before the caret from "before" into "after": Backspace removes one character each time, then the
    /// new text is typed. Only the part that differs is replaced: the characters both texts share at the start stay.
    /// The caret is at the end, so the rest is deleted from the end and typed again.
    /// </summary>
    private static bool SendEdit(string before, string after, bool capsLock, bool batch)
    {
        int shared = SharedPrefixLength(before, after);

        // Each group is one key (its press and release) or one Unicode character; each is sent in one call.
        var groups = new List<List<INPUT>>();
        for (int i = CountElements(before, shared); i > 0; i--)
        {
            groups.Add(new List<INPUT> { MakeKey(VK_BACK, 0), MakeKey(VK_BACK, KEYEVENTF_KEYUP) });
        }
        foreach (char c in after.AsSpan(shared))
        {
            var keys = new List<INPUT>(4);
            if (TryKeyPresses(c, capsLock, keys))
            {
                groups.Add(keys);
            }
            else
            {
                // No key for this character (for example "…"): sent as a Unicode character.
                groups.Add(new List<INPUT> { MakeCharacter(c, 0), MakeCharacter(c, KEYEVENTF_KEYUP) });
            }
        }

        // Browsers take a whole batch of keys at once (sending it that way is what keeps them up with fast
        // typing). Other apps, such as Notepad, drop keys that come in a burst (89 of 100 replacements came out
        // wrong when measured), so each key is paced.
        if (batch) return Send(groups.SelectMany(g => g).ToList());
        foreach (var group in groups)
        {
            if (!Send(group)) return false;
            Thread.Sleep(KeyStepMilliseconds);
        }
        return true;
    }

    private const ushort ShiftScanCode = 0x2A;

    /// <summary>
    /// How many characters at the start both texts share, so they can stay. The shared part never ends inside a character made
    /// of several UTF-16 units (an emoji, a letter with a combining mark): a Backspace removes the whole character.
    /// </summary>
    private static int SharedPrefixLength(string before, string after)
    {
        if (InputMarks.OldPath) return 0;
        int shared = 0;
        while (shared < before.Length && shared < after.Length && before[shared] == after[shared]) shared++;
        if (shared == 0 || (shared == before.Length && shared == after.Length)) return shared;

        var inBefore = new HashSet<int>(System.Globalization.StringInfo.ParseCombiningCharacters(before)) { before.Length };
        var inAfter = new HashSet<int>(System.Globalization.StringInfo.ParseCombiningCharacters(after)) { after.Length };
        while (shared > 0 && !(inBefore.Contains(shared) && inAfter.Contains(shared))) shared--;
        return shared;
    }

    /// <summary>How many characters (not UTF-16 units) the text has from index start on: one Backspace removes each.</summary>
    private static int CountElements(string text, int start)
    {
        int count = 0;
        foreach (int index in System.Globalization.StringInfo.ParseCombiningCharacters(text))
        {
            if (index >= start) count++;
        }
        return count;
    }

    // How long Notepad is given to finish its own change to the word before the result is checked.
    private const int RepairSettleMilliseconds = 30;

    /// <summary>
    /// Notepad only. Notepad's own autocorrect can change the word in the short time between reading it and typing
    /// over it, which leaves the wrong characters deleted. The text is read again; when it is not the intended text
    /// but is close to it, it is replaced once more from what is really there.
    /// </summary>
    private static void RepairIfChanged(string intended, bool capsLock)
    {
        Thread.Sleep(RepairSettleMilliseconds);
        string? tail = ReadWindow(intended.Length + RealTextSlack, readSlowApps: true, allowShort: true);
        if (tail is null || tail.EndsWith(intended, StringComparison.Ordinal)) return;

        string? candidate = ClosestEnding(tail, intended, out int _);
        if (candidate is null || !ModifiersAreUp(requireReadable: false)) return;

        Log.Write("replace", $"text changed under the replacement; repairing {candidate.Length} characters from what is really there");
        SendEdit(candidate, intended, capsLock, batch: false);
    }

    /// <summary>The ending of tail, starting at a word boundary, that is closest to text (within MaxEditsFromTyped edits), or null.</summary>
    private static string? ClosestEnding(string tail, string text, out int distanceFound)
    {
        string? best = null;
        int bestDistance = MaxEditsFromTyped + 1;
        int lowest = Math.Max(1, text.Length - MaxEditsFromTyped);
        int highest = Math.Min(tail.Length, text.Length + MaxEditsFromTyped);
        for (int length = lowest; length <= highest; length++)
        {
            bool atBoundary = length == tail.Length || !char.IsLetterOrDigit(tail[tail.Length - length - 1]);
            if (!atBoundary) continue;
            string candidate = tail[^length..];
            int distance = EditDistance(candidate, text);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        distanceFound = bestDistance;
        return best;
    }

    private readonly record struct RealTextPlan(ReplaceResult? Result, string RealBefore);

    // How many characters beyond the typed ones are read, so that a word Notepad made longer is still seen whole.
    private const int RealTextSlack = 12;

    // The largest number of edits between the typed word and what Notepad made of it for the text to count as
    // the same word. Anything further apart is left alone: it is probably not this word.
    private const int MaxEditsFromTyped = 3;

    /// <summary>
    /// Notepad only. Reads the text really before the caret and decides what to replace:
    /// as typed, then the typed text is replaced; already the new form (Notepad corrected it), nothing is done;
    /// a different form close to what was typed, then that text is replaced; unreadable or unrelated, nothing is done.
    /// </summary>
    private static RealTextPlan PlanFromRealText(string typed, string target)
    {
        var clock = Stopwatch.StartNew();
        string? tail = ReadTailSettled(typed, target, typed.Length + RealTextSlack);
        long readMilliseconds = clock.ElapsedMilliseconds;

        if (tail is null)
        {
            Log.Write("replace", $"notepad read {readMilliseconds} ms: text not readable, nothing changed");
            return new RealTextPlan(ReplaceResult.TextChanged, typed);
        }
        if (tail.EndsWith(typed, StringComparison.Ordinal))
        {
            Log.Write("replace", $"notepad read {readMilliseconds} ms: text is as typed");
            return new RealTextPlan(null, typed);
        }
        if (tail.EndsWith(target, StringComparison.Ordinal))
        {
            Log.Write("replace", $"notepad read {readMilliseconds} ms: text already has the corrected form, nothing changed");
            return new RealTextPlan(ReplaceResult.AlreadyCorrect, typed);
        }

        // Something else is there: the closest ending of the real text that starts at a word boundary.
        string? best = ClosestEnding(tail, typed, out int _);

        if (best is null)
        {
            Log.Write("replace", $"notepad read {readMilliseconds} ms: text differs from what was typed, nothing changed");
            return new RealTextPlan(ReplaceResult.TextChanged, typed);
        }
        Log.Write("replace", $"notepad read {readMilliseconds} ms: text was changed to something else ({best.Length} characters), replacing what is there");
        return new RealTextPlan(null, best);
    }

    /// <summary>The text before the caret once it ends with typed or target, or the last reading after the settle time.</summary>
    private static string? ReadTailSettled(string typed, string target, int count)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            string? tail = ReadWindow(count, readSlowApps: true, allowShort: true);
            if (tail is null
                || tail.EndsWith(typed, StringComparison.Ordinal)
                || tail.EndsWith(target, StringComparison.Ordinal)
                || clock.ElapsedMilliseconds >= VerifySettleMilliseconds)
            {
                return tail;
            }
            Thread.Sleep(VerifyPollMilliseconds);
        }
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>
    /// Adds the key presses that type c to the batch, using the keyboard's own keys (with Shift when needed).
    /// Returns false when the character has no such key, so it has to be sent as a Unicode character.
    /// </summary>
    private static bool TryKeyPresses(char c, bool capsLock, List<INPUT> batch)
    {
        short scan = VkKeyScanEx(c, ForegroundLayout());
        if (scan == -1) return false;

        int shiftState = (scan >> 8) & 0xFF;
        if ((shiftState & ~1) != 0) return false; // Ctrl or Alt would be needed.

        bool shift = (shiftState & 1) != 0;
        // Caps Lock flips the letters, so Shift is set the other way for letters while it is on.
        if (char.IsLetter(c) && capsLock) shift = !shift;

        // A capital letter is sent as the character itself: a Shift press inside a quick batch was measured being lost
        // in Chrome ("I'm" typed as "i'm"), while the character cannot lose it.
        if (shift && char.IsLetter(c)) return false;

        ushort vk = (ushort)(scan & 0xFF);
        if (shift) batch.Add(MakeKey(VK_SHIFT, 0));
        batch.Add(MakeKey(vk, 0));
        batch.Add(MakeKey(vk, KEYEVENTF_KEYUP));
        if (shift) batch.Add(MakeKey(VK_SHIFT, KEYEVENTF_KEYUP));
        return true;
    }

    /// <summary>
    /// The text before the cursor once it matches expected, or the last reading if it never does within
    /// the settle time. Null when the app does not expose its text.
    /// </summary>
    private static string? ReadSettledBeforeCaret(string expected, bool readSlowApps)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            string? before = TextBeforeCaret(expected.Length, readSlowApps);
            if (before is null || before == expected || clock.ElapsedMilliseconds >= VerifySettleMilliseconds)
            {
                return before;
            }
            Thread.Sleep(VerifyPollMilliseconds);
        }
    }

    /// <summary>
    /// The last count characters before the cursor, or null when the focused app does not expose
    /// its text. Reads through UI Automation; never throws.
    /// </summary>
    public static string? TextBeforeCaret(int count, bool readSlowApps = false) => ReadWindow(count, readSlowApps, allowShort: false);

    /// <summary>
    /// The text before the caret, at most count characters. With allowShort, fewer characters are returned when the
    /// text is shorter (near the start of a document); otherwise null is returned then.
    /// </summary>
    private static string? ReadWindow(int count, bool readSlowApps, bool allowShort)
    {
        // Browsers answer these reads slowly and fall behind the typing (seconds in Chrome), so their
        // text is not read; the typed characters are then replaced as typed. Notepad is skipped for plain
        // checks (its read takes about 85 ms), but replacements and the check after a correction read it (readSlowApps).
        if (IsBrowserInFront() || (!readSlowApps && IsProcessInFront(NoReadProcesses))) return null;

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null) return null;

            if (focused.TryGetCurrentPattern(TextPattern.Pattern, out object pattern))
            {
                var text = (TextPattern)pattern;
                var selections = text.GetSelection();
                if (selections.Length == 0) return null;

                // Only the characters just before the caret are read. Reading the whole text before the caret
                // gets slower as the document grows, and Notepad's reads were the slow part of each correction.
                var beforeCaret = selections[0].Clone();
                beforeCaret.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -count);
                string window = beforeCaret.GetText(count);
                if (window.Length < count && !allowShort) return null;
                return window;
            }

            // Some text boxes expose only their value. The caret is taken to be at the end of the value,
            // which holds because clicks and arrow keys end the typing context.
            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
            {
                string value = ((ValuePattern)valuePattern).Current.Value;
                if (value.Length < count) return allowShort ? value : null;
                return value[^count..];
            }
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException)
        {
            return null;
        }
    }

    private static readonly string[] BrowserProcesses = ["chrome", "msedge", "firefox", "brave", "opera"];

    // Notepad answers the text read in about 85 ms, which is longer than a correction should take. Its
    // corrections rely on the typed characters tracked by the app instead; clicks and arrow keys reset that.
    private static readonly string[] NoReadProcesses = ["notepad"];

    private static bool IsBrowserInFront() => IsProcessInFront(BrowserProcesses);

    private static bool IsProcessInFront(string[] names)
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint processId);
        try
        {
            string name = Process.GetProcessById((int)processId).ProcessName;
            return names.Any(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return true;
        var array = inputs.ToArray();
        uint sent = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
        return sent == array.Length;
    }

    /// <summary>Sends one key event (press or release) with its scan code, as the hook saw it. Used to type held keys again.</summary>
    internal static bool SendRawKey(ushort virtualKey, ushort scanCode, bool up, bool extended)
    {
        uint flags = (up ? KEYEVENTF_KEYUP : 0u) | (extended ? KEYEVENTF_EXTENDEDKEY : 0u);
        INPUT input = MakeKey(virtualKey, flags);
        input.u.ki.wScan = scanCode;
        return Send(new List<INPUT> { input });
    }

    private static INPUT MakeKey(ushort virtualKey, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = InputMarks.Own,
            },
        },
    };

    private static INPUT MakeCharacter(char c, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = flags | KEYEVENTF_UNICODE,
                time = 0,
                dwExtraInfo = InputMarks.Own,
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short VkKeyScanEx(char ch, IntPtr layout);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    /// <summary>
    /// The keyboard layout of the window being typed into. Key codes depend on the layout, so the layout
    /// must be the target window's, not this app's. Falls back to this thread's layout if there is no window.
    /// </summary>
    private static IntPtr ForegroundLayout()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return GetKeyboardLayout(0);
        uint threadId = GetWindowThreadProcessId(window, out _);
        return GetKeyboardLayout(threadId);
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_MENU = 0x12;

    // Alt+U is the undo key. While Alt is still down, Windows reads Backspace and letters as Alt shortcuts
    // and the replacement lands wrong, so the replacement waits for Alt to be released (at most one second).
    private static void WaitForAltRelease()
    {
        var clock = Stopwatch.StartNew();
        while ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0 && clock.ElapsedMilliseconds < 1000)
        {
            Thread.Sleep(10);
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
