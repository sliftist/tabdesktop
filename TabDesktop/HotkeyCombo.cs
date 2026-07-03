using System.Windows.Input;
using TabDesktop.Interop;

namespace TabDesktop;

// A global-hotkey combination, persisted as text like "Win+A" or "Ctrl+Shift+F2".
public sealed record HotkeyCombo(bool Win, bool Ctrl, bool Alt, bool Shift, Key Key)
{
    public uint ModifierFlags =>
        (Win ? NativeMethods.MOD_WIN : 0)
        | (Ctrl ? NativeMethods.MOD_CONTROL : 0)
        | (Alt ? NativeMethods.MOD_ALT : 0)
        | (Shift ? NativeMethods.MOD_SHIFT : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Win) parts.Add("Win");
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public static HotkeyCombo? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool win = false, ctrl = false, alt = false, shift = false;
        Key? key = null;
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "win":
                case "windows":
                    win = true;
                    break;
                case "ctrl":
                case "control":
                    ctrl = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                default:
                    if (!Enum.TryParse(part, true, out Key parsed)) return null;
                    key = parsed;
                    break;
            }
        }
        if (key is null) return null;
        return new HotkeyCombo(win, ctrl, alt, shift, key.Value);
    }

    // Builds the combo the user is pressing in the capture box; null while only modifiers are down, and for bare non-function keys — a global hotkey without a modifier would swallow ordinary typing system-wide.
    public static HotkeyCombo? FromKeyEvent(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return null;
        }
        ModifierKeys mods = Keyboard.Modifiers;
        var combo = new HotkeyCombo(mods.HasFlag(ModifierKeys.Windows), mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Shift), key);
        bool functionKey = key >= Key.F1 && key <= Key.F24;
        if (combo.ModifierFlags == 0 && !functionKey) return null;
        return combo;
    }
}
