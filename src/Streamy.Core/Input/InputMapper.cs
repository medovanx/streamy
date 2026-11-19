using Streamy.Core.Geometry;
using Streamy.Core.Protocol;

namespace Streamy.Core.Input;

public enum MouseButton
{
    Left,
    Right,
    Middle,
    Back,    // XButton1
    Forward, // XButton2
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
}

/// <summary>
/// Turns PC mouse and keyboard events into scrcpy control messages. Platform-free so the rules are unit tested:
/// <list type="bullet">
/// <item>Left button acts as a finger: press, drag, long press.</item>
/// <item>Ctrl + left drag pinches: a second, virtual finger mirrors the first around the screen centre.</item>
/// <item>Right click is Back (or wakes the screen), middle click is Home, the side buttons open Recents and the notifications, as in scrcpy.</item>
/// <item>The wheel scrolls at the pointer.</item>
/// <item>Keys without text, and any key with Ctrl or Alt, go as key events; printable characters go as text (see <see cref="KeyMap"/>).</item>
/// </list>
/// </summary>
public sealed class InputMapper
{
    private bool _fingerDown;
    private bool _pinching;
    private readonly HashSet<int> _keysDown = [];

    /// <summary>The window and frame sizes; update whenever either changes.</summary>
    public ViewMapper View { get; set; }

    public bool IsDragging => _fingerDown;

    public IReadOnlyList<byte[]> MouseDown(MouseButton button, int x, int y, KeyModifiers mods)
    {
        if (View.FrameWidth <= 0) return [];
        switch (button)
        {
            case MouseButton.Left:
                if (_fingerDown) return [];
                _fingerDown = true;
                _pinching = (mods & KeyModifiers.Ctrl) != 0;
                return Touch(AndroidMotionAction.Down, x, y);
            case MouseButton.Right:
                return [ControlMessage.BackOrScreenOn(AndroidKeyAction.Down)];
            case MouseButton.Middle:
                return [Key(AndroidKeyAction.Down, AndroidKeycode.Home)];
            case MouseButton.Back:
                return [Key(AndroidKeyAction.Down, AndroidKeycode.AppSwitch)];
            case MouseButton.Forward:
                return [ControlMessage.ExpandNotificationPanel()];
            default:
                return [];
        }
    }

    public IReadOnlyList<byte[]> MouseMove(int x, int y) =>
        _fingerDown ? Touch(AndroidMotionAction.Move, x, y) : [];

    public IReadOnlyList<byte[]> MouseUp(MouseButton button, int x, int y)
    {
        switch (button)
        {
            case MouseButton.Left:
                if (!_fingerDown) return [];
                var msgs = Touch(AndroidMotionAction.Up, x, y);
                _fingerDown = false;
                _pinching = false;
                return msgs;
            case MouseButton.Right:
                return [ControlMessage.BackOrScreenOn(AndroidKeyAction.Up)];
            case MouseButton.Middle:
                return [Key(AndroidKeyAction.Up, AndroidKeycode.Home)];
            case MouseButton.Back:
                return [Key(AndroidKeyAction.Up, AndroidKeycode.AppSwitch)];
            default:
                return [];
        }
    }

    /// <summary>Lifts the finger, e.g. when the window loses the mouse mid-drag.</summary>
    public IReadOnlyList<byte[]> CancelDrag(int x, int y) => MouseUp(MouseButton.Left, x, y);

    /// <param name="wheelDelta">Windows wheel delta: 120 per notch, positive away from the user (up or right).</param>
    public IReadOnlyList<byte[]> Wheel(int x, int y, int wheelDelta, bool horizontal)
    {
        if (View.FrameWidth <= 0 || wheelDelta == 0) return [];
        float notches = wheelDelta / 120f;
        // Same signs on both sides: Windows and Android both count the wheel away from the user (up) and tilting
        // right as positive (Android AXIS_VSCROLL / AXIS_HSCROLL), so the values pass straight through as in scrcpy.
        var pos = View.Map(x, y);
        return horizontal
            ? [ControlMessage.InjectScroll(pos, notches, 0, AndroidMotionButtons.None)]
            : [ControlMessage.InjectScroll(pos, 0, notches, AndroidMotionButtons.None)];
    }

    /// <summary>
    /// A key went down. Returns the key event to send, or null when the key should arrive as text instead
    /// (through the window's character message).
    /// </summary>
    public byte[]? KeyDown(int vk, KeyModifiers mods, uint repeat)
    {
        var code = KeyMap.FromVirtualKey(vk);
        if (code == AndroidKeycode.Unknown) return null;
        bool shortcut = (mods & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != 0
            && (mods & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != (KeyModifiers.Ctrl | KeyModifiers.Alt); // Ctrl+Alt is AltGr: text
        if (!shortcut && !KeyMap.IsNonText(vk)) return null;
        _keysDown.Add(vk);
        return ControlMessage.InjectKeycode(AndroidKeyAction.Down, code, repeat, Meta(mods));
    }

    /// <summary>A key went up. Only keys whose press was sent as a key event are released.</summary>
    public byte[]? KeyUp(int vk, KeyModifiers mods)
    {
        if (!_keysDown.Remove(vk)) return null;
        return ControlMessage.InjectKeycode(AndroidKeyAction.Up, KeyMap.FromVirtualKey(vk), 0, Meta(mods));
    }

    /// <summary>Releases every key still held, e.g. when the window loses focus.</summary>
    public IReadOnlyList<byte[]> ReleaseAllKeys()
    {
        var msgs = _keysDown.Select(vk => ControlMessage.InjectKeycode(AndroidKeyAction.Up, KeyMap.FromVirtualKey(vk), 0, AndroidMetaState.None)).ToList();
        _keysDown.Clear();
        return msgs;
    }

    public static AndroidMetaState Meta(KeyModifiers mods)
    {
        var m = AndroidMetaState.None;
        if ((mods & KeyModifiers.Shift) != 0) m |= AndroidMetaState.ShiftOn | AndroidMetaState.ShiftLeftOn;
        if ((mods & KeyModifiers.Ctrl) != 0) m |= AndroidMetaState.CtrlOn | AndroidMetaState.CtrlLeftOn;
        if ((mods & KeyModifiers.Alt) != 0) m |= AndroidMetaState.AltOn | AndroidMetaState.AltLeftOn;
        return m;
    }

    private static byte[] Key(AndroidKeyAction action, AndroidKeycode code) =>
        ControlMessage.InjectKeycode(action, code, 0, AndroidMetaState.None);

    private IReadOnlyList<byte[]> Touch(AndroidMotionAction action, int x, int y)
    {
        var pos = View.Map(x, y);
        float pressure = action == AndroidMotionAction.Up ? 0f : 1f;
        var buttons = action == AndroidMotionAction.Up ? AndroidMotionButtons.None : AndroidMotionButtons.Primary;
        var actionButton = action == AndroidMotionAction.Move ? AndroidMotionButtons.None : AndroidMotionButtons.Primary;
        var finger = ControlMessage.InjectTouch(action, ScrcpyProtocol.PointerIdMouse, pos, pressure, actionButton, buttons);
        if (!_pinching) return [finger];

        var mirrored = pos with { X = pos.FrameWidth - 1 - pos.X, Y = pos.FrameHeight - 1 - pos.Y };
        var virtualFinger = ControlMessage.InjectTouch(action, ScrcpyProtocol.PointerIdVirtualFinger, mirrored, pressure, actionButton, buttons);
        return [finger, virtualFinger];
    }
}
