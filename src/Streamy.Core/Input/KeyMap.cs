namespace Streamy.Core.Input;

/// <summary>
/// Maps Windows virtual-key codes (as plain ints, so Core stays Windows-free) to Android key codes.
/// Printable characters are normally sent as text instead; this map covers keys that have no text
/// (Enter, Backspace, arrows, F-keys) and letters/digits used with Ctrl or Alt.
/// </summary>
public static class KeyMap
{
    private static readonly Dictionary<int, AndroidKeycode> Special = new()
    {
        [0x08] = AndroidKeycode.Del,          // VK_BACK
        [0x09] = AndroidKeycode.Tab,
        [0x0D] = AndroidKeycode.Enter,
        [0x1B] = AndroidKeycode.Escape,
        [0x20] = AndroidKeycode.Space,
        [0x21] = AndroidKeycode.PageUp,
        [0x22] = AndroidKeycode.PageDown,
        [0x23] = AndroidKeycode.MoveEnd,
        [0x24] = AndroidKeycode.MoveHome,
        [0x25] = AndroidKeycode.DpadLeft,
        [0x26] = AndroidKeycode.DpadUp,
        [0x27] = AndroidKeycode.DpadRight,
        [0x28] = AndroidKeycode.DpadDown,
        [0x2D] = AndroidKeycode.Insert,
        [0x2E] = AndroidKeycode.ForwardDel,
        [0xAD] = AndroidKeycode.VolumeMute,
        [0xAE] = AndroidKeycode.VolumeDown,
        [0xAF] = AndroidKeycode.VolumeUp,
        [0xBA] = AndroidKeycode.Semicolon,    // VK_OEM_1
        [0xBB] = AndroidKeycode.Equals,       // VK_OEM_PLUS
        [0xBC] = AndroidKeycode.Comma,
        [0xBD] = AndroidKeycode.Minus,
        [0xBE] = AndroidKeycode.Period,
        [0xBF] = AndroidKeycode.Slash,        // VK_OEM_2
        [0xC0] = AndroidKeycode.Grave,        // VK_OEM_3
        [0xDB] = AndroidKeycode.LeftBracket,  // VK_OEM_4
        [0xDC] = AndroidKeycode.Backslash,    // VK_OEM_5
        [0xDD] = AndroidKeycode.RightBracket, // VK_OEM_6
        [0xDE] = AndroidKeycode.Apostrophe,   // VK_OEM_7
    };

    public static AndroidKeycode FromVirtualKey(int vk)
    {
        if (vk is >= 0x41 and <= 0x5A) return AndroidKeycode.A + (vk - 0x41);
        if (vk is >= 0x30 and <= 0x39) return AndroidKeycode.Num0 + (vk - 0x30);
        if (vk is >= 0x70 and <= 0x7B) return AndroidKeycode.F1 + (vk - 0x70);
        return Special.GetValueOrDefault(vk, AndroidKeycode.Unknown);
    }

    /// <summary>
    /// Keys that should go as key events even without a modifier, because they carry no text.
    /// Letters, digits and punctuation without Ctrl/Alt are sent as text so the phone's layout is ignored.
    /// </summary>
    public static bool IsNonText(int vk) =>
        vk is 0x08 or 0x09 or 0x0D or 0x1B or (>= 0x21 and <= 0x28) or 0x2D or 0x2E or (>= 0x70 and <= 0x7B) or (>= 0xAD and <= 0xAF);
}
