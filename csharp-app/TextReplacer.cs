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
    public static ReplaceResult ReplaceBeforeCaret(string expectedBefore, string newText, bool requireReadable = false)
    {
        // The user's keys are held while the correction is typed, so they cannot land inside it.
        return KeyboardHook.HoldKeysWhile(() => ReplaceCore(expectedBefore, newText, requireReadable));
    }

    private static ReplaceResult ReplaceCore(string expectedBefore, string newText, bool requireReadable)
    {
        if (expectedBefore.Length == 0) return ReplaceResult.Failed;

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

        WaitForAltRelease();
        bool capsLock = (GetKeyState(VK_CAPITAL) & 1) != 0;
        timeBeginPeriod(1);

        // Backspace removes one character before the caret each time, then the new text is typed.
        // Only the part that differs is replaced: the characters both texts share at the start stay.
        // The caret is at the end, so the rest is deleted from the end and typed again.
        int shared = 0;
        while (shared < expectedBefore.Length && shared < newText.Length && expectedBefore[shared] == newText[shared]) shared++;

        // Each group is one key (its press and release) or one Unicode character; each is sent in one call.
        var groups = new List<List<INPUT>>();
        for (int i = shared; i < expectedBefore.Length; i++)
        {
            groups.Add(new List<INPUT> { MakeKey(VK_BACK, 0), MakeKey(VK_BACK, KEYEVENTF_KEYUP) });
        }
        foreach (char c in newText.AsSpan(shared))
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
        // typing). Other apps, such as Notepad, drop keys that come in a burst, so each key is paced.
        if (IsBrowserInFront())
        {
            if (!Send(groups.SelectMany(g => g).ToList())) return ReplaceResult.Failed;
        }
        else
        {
            foreach (var group in groups)
            {
                if (!Send(group)) return ReplaceResult.Failed;
                Thread.Sleep(KeyStepMilliseconds);
            }
        }

        Log.Write("replace", $"replaced {expectedBefore.Length} characters with {newText.Length}");
        return ReplaceResult.Done;
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
    public static string? TextBeforeCaret(int count, bool readSlowApps = false)
    {
        // Browsers answer these reads slowly and fall behind the typing (seconds in Chrome), so their
        // text is not read; the typed characters are then replaced as typed. Notepad is skipped while
        // typing (its read is slow), but a check after a correction can read it (readSlowApps).
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
                if (window.Length < count) return null;
                return window;
            }

            // Some text boxes expose only their value. The caret is taken to be at the end of the value,
            // which holds because clicks and arrow keys end the typing context.
            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
            {
                string value = ((ValuePattern)valuePattern).Current.Value;
                if (value.Length < count) return null;
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
