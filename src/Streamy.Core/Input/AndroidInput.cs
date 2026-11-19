namespace Streamy.Core.Input;

/// <summary>android.view.KeyEvent actions.</summary>
public enum AndroidKeyAction : byte
{
    Down = 0,
    Up = 1,
}

/// <summary>android.view.MotionEvent actions.</summary>
public enum AndroidMotionAction : byte
{
    Down = 0,
    Up = 1,
    Move = 2,
    Cancel = 3,
    Outside = 4,
    PointerDown = 5,
    PointerUp = 6,
    HoverMove = 7,
    Scroll = 8,
    HoverEnter = 9,
    HoverExit = 10,
    ButtonPress = 11,
    ButtonRelease = 12,
}

[Flags]
public enum AndroidMotionButtons : uint
{
    None = 0,
    Primary = 1 << 0,
    Secondary = 1 << 1,
    Tertiary = 1 << 2,
    Back = 1 << 3,
    Forward = 1 << 4,
}

[Flags]
public enum AndroidMetaState : uint
{
    None = 0,
    AltOn = 0x02,
    AltLeftOn = 0x10,
    AltRightOn = 0x20,
    ShiftOn = 0x01,
    ShiftLeftOn = 0x40,
    ShiftRightOn = 0x80,
    CtrlOn = 0x1000,
    CtrlLeftOn = 0x2000,
    CtrlRightOn = 0x4000,
    MetaOn = 0x10000,
    MetaLeftOn = 0x20000,
    MetaRightOn = 0x40000,
    CapsLockOn = 0x100000,
    NumLockOn = 0x200000,
}

/// <summary>The subset of android.view.KeyEvent key codes Streamy sends.</summary>
public enum AndroidKeycode
{
    Unknown = 0,
    Home = 3,
    Back = 4,
    Num0 = 7,
    Num9 = 16,
    DpadUp = 19,
    DpadDown = 20,
    DpadLeft = 21,
    DpadRight = 22,
    VolumeUp = 24,
    VolumeDown = 25,
    Power = 26,
    A = 29,
    Z = 54,
    Comma = 55,
    Period = 56,
    Tab = 61,
    Space = 62,
    Enter = 66,
    Del = 67,
    Grave = 68,
    Minus = 69,
    Equals = 70,
    LeftBracket = 71,
    RightBracket = 72,
    Backslash = 73,
    Semicolon = 74,
    Apostrophe = 75,
    Slash = 76,
    Menu = 82,
    PageUp = 92,
    PageDown = 93,
    Escape = 111,
    ForwardDel = 112,
    MoveHome = 122,
    MoveEnd = 123,
    Insert = 124,
    F1 = 131,
    F12 = 142,
    VolumeMute = 164,
    AppSwitch = 187,
}
