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

    // Alt key state tracked from the hook's own key events.
    private bool _altDown;

    private const int VK_U = 0x55;

    public event Action<KeyInput>? KeyPressed;

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
        if (_hookId == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard hook.");
        }
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int message = wParam.ToInt32();
        bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
        bool isUp = message == WM_KEYUP || message == WM_SYSKEYUP;
        if (nCode >= 0 && (isDown || isUp))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & LLKHF_INJECTED) != 0;
            int vk = (int)info.vkCode;

            // Track Alt from its own key events. Asking Windows for the Alt state at the moment
            // U is pressed can be out of date when the keys are pressed close together.
            if (!injected && vk is VK_MENU or VK_LMENU or VK_RMENU)
            {
                _altDown = isDown;
            }

            if (!injected && isDown && vk == UndoVirtualKey && _altDown && !IsDown(VK_CONTROL))
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

            // Modifier keys alone (Alt, Shift, Ctrl, Win, Caps Lock) type nothing. Reporting them would
            // count as typing and would block undo, because Alt+U would look like typing after a correction.
            if (!injected && isDown && !IsModifierKey(vk))
            {
                KeyPressed?.Invoke(Translate(info));
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // An unassigned virtual key that no application uses, so it has no visible effect.
    private const byte VK_UNASSIGNED = 0xE8;
    private const uint KEYEVENTF_KEYUP_KB = 0x0002;

    private static void SendHarmlessKey()
    {
        keybd_event(VK_UNASSIGNED, 0, 0, UIntPtr.Zero);
        keybd_event(VK_UNASSIGNED, 0, KEYEVENTF_KEYUP_KB, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static bool IsModifierKey(int vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_CAPITAL
        or 0x5B or 0x5C        // Left and right Windows keys
        or 0xA0 or 0xA1        // Left and right Shift
        or 0xA2 or 0xA3        // Left and right Ctrl
        or 0xA4 or 0xA5;       // Left and right Alt

    /// <summary>Turns a raw key into the KeyInput the word detector understands.</summary>
    private static KeyInput Translate(KBDLLHOOKSTRUCT info)
    {
        int vk = (int)info.vkCode;

        // Shortcuts (Ctrl or Alt combinations) are never typed text.
        if (IsDown(VK_CONTROL) || IsDown(VK_MENU))
        {
            return new KeyInput(null, IsBackspace: false, IsBreak: true);
        }

        if (vk == VK_BACK)
        {
            return new KeyInput(null, IsBackspace: true, IsBreak: false);
        }

        if (vk is VK_TAB or VK_RETURN or VK_ESCAPE or VK_DELETE or VK_HOME or VK_END
            or VK_PRIOR or VK_NEXT or VK_LEFT or VK_UP or VK_RIGHT or VK_DOWN)
        {
            return new KeyInput(null, IsBackspace: false, IsBreak: true);
        }

        char? character = ToCharacter(vk, info.scanCode);
        return new KeyInput(character, IsBackspace: false, IsBreak: false);
    }

    /// <summary>Returns the printed character for a key, or null when it prints nothing.</summary>
    private static char? ToCharacter(int vk, uint scanCode)
    {
        var state = new byte[256];
        if (IsDown(VK_SHIFT)) state[VK_SHIFT] = 0x80;
        if (IsToggled(VK_CAPITAL)) state[VK_CAPITAL] = 0x01;

        var buffer = new StringBuilder(4);
        int count = ToUnicode((uint)vk, scanCode, state, buffer, buffer.Capacity, 0);
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
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out] StringBuilder pwszBuff, int cchBuff, uint wFlags);
}
