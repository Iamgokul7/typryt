using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Typryt;

/// <summary>
/// Replaces text in the focused window. Deletes characters with Backspace, then pastes
/// the new text with Ctrl+V. Pasting is more reliable than typing Unicode characters:
/// it keeps spaces and does not drop or duplicate characters.
///
/// The user's clipboard text is saved and restored shortly after the paste. Injected keys
/// are flagged by Windows, so KeyboardHook ignores them.
/// </summary>
public static class TextReplacer
{
    private const ushort VK_BACK = 0x08;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // Time for the target app to read the clipboard after Ctrl+V before it is restored.
    private const int RestoreDelayMilliseconds = 400;

    // Pause between key events so each key is handled in order by the target app.
    private const int KeyGapMilliseconds = 8;

    /// <summary>Deletes deleteCount characters before the caret, then pastes text.</summary>
    public static void Replace(int deleteCount, string text)
    {
        // The clipboard must be used on the UI thread.
        string? saved = null;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            saved = dispatcher.Invoke(() => SetClipboardForPaste(text));
        }
        else
        {
            saved = SetClipboardForPaste(text);
        }

        // Keys are sent one at a time with short pauses, so the target app handles each
        // one in order. A single batch let the backspaces and the paste overlap.
        for (int i = 0; i < deleteCount; i++)
        {
            SendKey(VK_BACK, 0);
            SendKey(VK_BACK, KEYEVENTF_KEYUP);
            Thread.Sleep(KeyGapMilliseconds);
        }
        SendKey(VK_CONTROL, 0);
        Thread.Sleep(KeyGapMilliseconds);
        SendKey(VK_V, 0);
        Thread.Sleep(KeyGapMilliseconds);
        SendKey(VK_V, KEYEVENTF_KEYUP);
        SendKey(VK_CONTROL, KEYEVENTF_KEYUP);

        ScheduleRestore(text, saved);
    }

    /// <summary>Saves the user's clipboard text and puts the replacement on the clipboard. UI thread only.</summary>
    private static string? SetClipboardForPaste(string text)
    {
        string? saved = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                saved = System.Windows.Clipboard.GetText();
            }
        }
        catch (COMException)
        {
            // Clipboard busy: paste anyway and skip the restore.
        }

        SetClipboardText(text);
        return saved;
    }

    private static void SendKey(ushort virtualKey, uint flags)
    {
        var input = MakeKey(virtualKey, flags);
        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        if (sent != 1)
        {
            throw new InvalidOperationException("SendInput failed for a key event.");
        }
    }

    /// <summary>
    /// Restores the user's clipboard after the paste has been read. Runs on a timer so the
    /// keyboard hook is not blocked. The clipboard is only restored if it still holds our text.
    /// </summary>
    private static void ScheduleRestore(string pastedText, string? saved)
    {
        Task.Delay(RestoreDelayMilliseconds).ContinueWith(_ =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            dispatcher.Invoke(() =>
            {
                try
                {
                    if (!System.Windows.Clipboard.ContainsText() || System.Windows.Clipboard.GetText() != pastedText) return;
                    if (saved is null) System.Windows.Clipboard.Clear();
                    else System.Windows.Clipboard.SetText(saved);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Clipboard busy: leave our text there.
                }
            });
        });
    }

    private static void SetClipboardText(string text)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
        }
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
                dwExtraInfo = IntPtr.Zero,
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
}
