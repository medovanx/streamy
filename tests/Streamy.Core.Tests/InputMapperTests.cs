using System.Buffers.Binary;
using Streamy.Core.Geometry;
using Streamy.Core.Input;
using Streamy.Core.Protocol;

namespace Streamy.Core.Tests;

public class InputMapperTests
{
    // 1080x2400 phone in a 540x1200 window: exactly half size, no bars.
    private static InputMapper Mapper() => new() { View = new ViewMapper(540, 1200, 1080, 2400) };

    private static (ControlMessageType Type, byte Action, ulong Pointer, int X, int Y) Touch(byte[] m) =>
        ((ControlMessageType)m[0], m[1], BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(2)),
         BinaryPrimitives.ReadInt32BigEndian(m.AsSpan(10)), BinaryPrimitives.ReadInt32BigEndian(m.AsSpan(14)));

    [Fact]
    public void LeftDragIsOneFinger()
    {
        var m = Mapper();
        var down = Assert.Single(m.MouseDown(MouseButton.Left, 100, 200, KeyModifiers.None));
        Assert.Equal((ControlMessageType.InjectTouchEvent, (byte)AndroidMotionAction.Down, ScrcpyProtocol.PointerIdMouse, 200, 400), Touch(down));

        var move = Assert.Single(m.MouseMove(150, 250));
        Assert.Equal(((byte)AndroidMotionAction.Move, 300, 500), (Touch(move).Action, Touch(move).X, Touch(move).Y));

        var up = Assert.Single(m.MouseUp(MouseButton.Left, 150, 250));
        Assert.Equal((byte)AndroidMotionAction.Up, Touch(up).Action);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(up.AsSpan(22))); // no pressure when lifted
        Assert.False(m.IsDragging);
    }

    [Fact]
    public void MovingWithoutAButtonSendsNothing() => Assert.Empty(Mapper().MouseMove(10, 10));

    [Fact]
    public void CtrlDragAddsAMirroredVirtualFinger()
    {
        var m = Mapper();
        var msgs = m.MouseDown(MouseButton.Left, 100, 200, KeyModifiers.Ctrl);
        Assert.Equal(2, msgs.Count);
        var v = Touch(msgs[1]);
        Assert.Equal(ScrcpyProtocol.PointerIdVirtualFinger, v.Pointer);
        Assert.Equal((1079 - 200, 2399 - 400), (v.X, v.Y));

        Assert.Equal(2, m.MouseMove(120, 220).Count);
        Assert.Equal(2, m.MouseUp(MouseButton.Left, 120, 220).Count);

        // The next plain drag is a single finger again.
        Assert.Single(m.MouseDown(MouseButton.Left, 1, 1, KeyModifiers.None));
    }

    [Fact]
    public void RightClickIsBackOrScreenOn()
    {
        var m = Mapper();
        Assert.Equal(new byte[] { 0x04, 0x00 }, Assert.Single(m.MouseDown(MouseButton.Right, 0, 0, KeyModifiers.None)));
        Assert.Equal(new byte[] { 0x04, 0x01 }, Assert.Single(m.MouseUp(MouseButton.Right, 0, 0)));
    }

    [Fact]
    public void MiddleClickIsHome()
    {
        var down = Assert.Single(Mapper().MouseDown(MouseButton.Middle, 0, 0, KeyModifiers.None));
        Assert.Equal(ControlMessage.InjectKeycode(AndroidKeyAction.Down, AndroidKeycode.Home, 0, AndroidMetaState.None), down);
    }

    [Fact]
    public void WheelUpScrollsUp()
    {
        var msg = Assert.Single(Mapper().Wheel(270, 600, 120, horizontal: false));
        Assert.Equal((byte)ControlMessageType.InjectScrollEvent, msg[0]);
        Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(msg.AsSpan(13)));      // h
        Assert.Equal(2048, BinaryPrimitives.ReadInt16BigEndian(msg.AsSpan(15)));   // v: 1 notch = 1/16 of the range
    }

    [Fact]
    public void NothingIsSentBeforeTheFirstFrame()
    {
        var m = new InputMapper();
        Assert.Empty(m.MouseDown(MouseButton.Left, 1, 1, KeyModifiers.None));
        Assert.Empty(m.Wheel(1, 1, 120, false));
    }

    [Fact]
    public void LettersGoAsTextButCtrlLettersGoAsKeys()
    {
        var m = Mapper();
        Assert.Null(m.KeyDown(0x41, KeyModifiers.None, 0));          // A -> text
        Assert.Null(m.KeyDown(0x41, KeyModifiers.Shift, 0));         // Shift+A -> text
        Assert.Null(m.KeyDown(0x41, KeyModifiers.Ctrl | KeyModifiers.Alt, 0)); // AltGr -> text

        var ctrlA = m.KeyDown(0x41, KeyModifiers.Ctrl, 0);
        Assert.Equal(ControlMessage.InjectKeycode(AndroidKeyAction.Down, AndroidKeycode.A, 0,
            AndroidMetaState.CtrlOn | AndroidMetaState.CtrlLeftOn), ctrlA);
        Assert.NotNull(m.KeyUp(0x41, KeyModifiers.Ctrl));
    }

    [Fact]
    public void EnterGoesAsAKeyWithRepeat()
    {
        var m = Mapper();
        var down = m.KeyDown(0x0D, KeyModifiers.None, 3);
        Assert.Equal(ControlMessage.InjectKeycode(AndroidKeyAction.Down, AndroidKeycode.Enter, 3, AndroidMetaState.None), down);
    }

    [Fact]
    public void OnlyKeysSentDownAreReleased()
    {
        var m = Mapper();
        Assert.Null(m.KeyUp(0x41, KeyModifiers.None)); // A went as text
        m.KeyDown(0x25, KeyModifiers.None, 0);
        m.KeyDown(0x0D, KeyModifiers.None, 0);
        Assert.Equal(2, m.ReleaseAllKeys().Count);
        Assert.Null(m.KeyUp(0x25, KeyModifiers.None));
    }
}

public class TextInjectionTests
{
    [Theory]
    [InlineData("hello, world!", false)]
    [InlineData("~", false)]
    [InlineData("\u0645\u0631\u062D\u0628\u0627", true)] // Arabic
    [InlineData("caf\u00E9", true)]
    [InlineData("\uD83D\uDE00", true)] // emoji
    public void NonAsciiGoesThroughTheClipboard(string text, bool paste) => Assert.Equal(paste, TextInjection.NeedsPaste(text));
}
