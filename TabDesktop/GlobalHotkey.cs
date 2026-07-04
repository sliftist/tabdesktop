using System.Runtime.InteropServices;
using System.Windows.Input;
using TabDesktop.Interop;

namespace TabDesktop;

// Global hotkey via a low-level keyboard hook rather than RegisterHotKey: the shell registers the Win+letter combos at logon (Win+A is Quick Settings), so RegisterHotKey can never claim them, while a hook sees the keystroke before the shell and can swallow it.
public sealed class GlobalHotkey : IDisposable
{
    // Injected on a swallowed Win combo so the shell sees some key between Win-down and Win-up; otherwise the press looks like a bare Win tap and the Start menu opens. 0xE8 is an unassigned virtual key, so nothing else reacts to it.
    private const byte DummyVirtualKey = 0xE8;

    // Field (not a local) so the GC can't collect the delegate while the native hook still calls it.
    private readonly NativeMethods.LowLevelKeyboardProc proc;
    private readonly IntPtr hook;
    private readonly Action pressed;
    private HotkeyCombo? combo;
    // Auto-repeat delivers a stream of keydowns while the key is held; only the first one before the matching keyup should fire.
    private bool fired;

    public GlobalHotkey(Action pressed)
    {
        this.pressed = pressed;
        proc = HookProc;
        hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, proc, NativeMethods.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            AppLog.Write(nameof(GlobalHotkey), $"SetWindowsHookEx failed, error {Marshal.GetLastWin32Error()}");
        }
    }

    public bool IsInstalled => hook != IntPtr.Zero;

    public void SetCombo(HotkeyCombo? value)
    {
        combo = value;
    }

    private Action<HotkeyCombo>? captureCombo;
    private Action? captureEscape;
    // Swallowed modifier keydowns never reach the OS input state, so GetAsyncKeyState reports them as up; capture must track modifier state itself from the events it eats.
    private readonly HashSet<uint> captureModifiersDown = new();

    private static readonly uint[] ModifierVks =
    {
        NativeMethods.VK_LWIN, NativeMethods.VK_RWIN,
        NativeMethods.VK_SHIFT, NativeMethods.VK_LSHIFT, NativeMethods.VK_RSHIFT,
        NativeMethods.VK_CONTROL, NativeMethods.VK_LCONTROL, NativeMethods.VK_RCONTROL,
        NativeMethods.VK_MENU, NativeMethods.VK_LMENU, NativeMethods.VK_RMENU,
    };

    public bool IsCapturing => captureCombo is not null;

    // While capturing (the settings hotkey box is focused), every keystroke system-wide is swallowed and fed here instead — the only way the user can press combos the shell owns (Win+A opens Quick Settings the instant it leaks through) and have them land in the box.
    public void BeginCapture(Action<HotkeyCombo> onCombo, Action onEscape)
    {
        captureCombo = onCombo;
        captureEscape = onEscape;
        // Modifiers already held when capture starts were pressed before we started swallowing, so the OS state is still accurate for them.
        captureModifiersDown.Clear();
        foreach (uint vk in ModifierVks)
        {
            if (IsDown((int)vk))
            {
                captureModifiersDown.Add(vk);
            }
        }
    }

    public void EndCapture()
    {
        captureCombo = null;
        captureEscape = null;
        captureModifiersDown.Clear();
    }

    private bool CaptureModifierDown(int genericVk, int leftVk, int rightVk)
    {
        return captureModifiersDown.Contains((uint)genericVk) || captureModifiersDown.Contains((uint)leftVk) || captureModifiersDown.Contains((uint)rightVk);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // An exception escaping into the native hook chain is fatal (or silently kills the hook); pass the keystroke through instead.
        try
        {
            return HookProcCore(nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(GlobalHotkey), ex.ToString());
            return NativeMethods.CallNextHookEx(hook, nCode, wParam, lParam);
        }
    }

    private IntPtr HookProcCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(hook, nCode, wParam, lParam);
        }
        long msg = wParam.ToInt64();
        NativeMethods.KBDLLHOOKSTRUCT data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        if (captureCombo is not null)
        {
            // Injected events (including our own dummy key) aren't the user pressing a combo; let them through untouched.
            if ((data.flags & NativeMethods.LLKHF_INJECTED) != 0)
            {
                return NativeMethods.CallNextHookEx(hook, nCode, wParam, lParam);
            }
            if (ModifierVks.Contains(data.vkCode))
            {
                if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
                {
                    captureModifiersDown.Add(data.vkCode);
                }
                else if (msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
                {
                    captureModifiersDown.Remove(data.vkCode);
                }
            }
            else if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                if (data.vkCode == NativeMethods.VK_ESCAPE)
                {
                    captureEscape?.Invoke();
                }
                else
                {
                    HotkeyCombo? captured = HotkeyCombo.FromPressedKey(
                        KeyInterop.KeyFromVirtualKey((int)data.vkCode),
                        CaptureModifierDown(NativeMethods.VK_LWIN, NativeMethods.VK_LWIN, NativeMethods.VK_RWIN),
                        CaptureModifierDown(NativeMethods.VK_CONTROL, NativeMethods.VK_LCONTROL, NativeMethods.VK_RCONTROL),
                        CaptureModifierDown(NativeMethods.VK_MENU, NativeMethods.VK_LMENU, NativeMethods.VK_RMENU),
                        CaptureModifierDown(NativeMethods.VK_SHIFT, NativeMethods.VK_LSHIFT, NativeMethods.VK_RSHIFT));
                    if (captured is not null)
                    {
                        captureCombo(captured);
                    }
                }
            }
            return new IntPtr(1);
        }
        if (combo is HotkeyCombo current)
        {
            uint vk = data.vkCode;
            if (vk == current.VirtualKey)
            {
                if (msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
                {
                    fired = false;
                }
                else if ((msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN) && ModifiersMatch(current))
                {
                    if (!fired)
                    {
                        fired = true;
                        if (current.Win)
                        {
                            NativeMethods.keybd_event(DummyVirtualKey, 0, 0, IntPtr.Zero);
                            NativeMethods.keybd_event(DummyVirtualKey, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
                        }
                        AppLog.Write(nameof(GlobalHotkey), $"Hotkey {current} pressed");
                        pressed();
                    }
                    return new IntPtr(1);
                }
            }
        }
        return NativeMethods.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    // Exact match on all four modifiers, so e.g. Win+Shift+A doesn't trigger a Win+A hotkey.
    private static bool ModifiersMatch(HotkeyCombo combo)
    {
        return (IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN)) == combo.Win
            && IsDown(NativeMethods.VK_CONTROL) == combo.Ctrl
            && IsDown(NativeMethods.VK_MENU) == combo.Alt
            && IsDown(NativeMethods.VK_SHIFT) == combo.Shift;
    }

    private static bool IsDown(int vk)
    {
        return (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(hook);
        }
    }
}
