using Streamy.Core.Input;
using Streamy.Core.Protocol;

namespace Streamy.Core.Tests;

/// <summary>Byte vectors ported from scrcpy 5.0.1 app/tests/test_control_msg_serialize.c.</summary>
public class ControlMessageTests
{
    [Fact]
    public void InjectKeycode()
    {
        var buf = ControlMessage.InjectKeycode(AndroidKeyAction.Up, AndroidKeycode.Enter, 5,
            AndroidMetaState.ShiftOn | AndroidMetaState.ShiftLeftOn);
        Assert.Equal(new byte[]
        {
            0x00, 0x01,
            0x00, 0x00, 0x00, 0x42,
            0x00, 0x00, 0x00, 0x05,
            0x00, 0x00, 0x00, 0x41,
        }, buf);
    }

    [Fact]
    public void InjectText()
    {
        var buf = ControlMessage.InjectText("hello, world!");
        Assert.Equal(18, buf.Length);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x0d }, buf[..5]);
        Assert.Equal("hello, world!"u8.ToArray(), buf[5..]);
    }

    [Fact]
    public void InjectTextLongIsCutTo300Bytes()
    {
        var buf = ControlMessage.InjectText(new string('a', 500));
        Assert.Equal(5 + 300, buf.Length);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x01, 0x2c }, buf[..5]);
    }

    [Fact]
    public void InjectTextNeverSplitsACharacter()
    {
        // 150 two-byte Arabic letters = 300 bytes; one more must be dropped whole, not half.
        var buf = ControlMessage.InjectText(new string('ب', 151));
        Assert.Equal(5 + 300, buf.Length);
    }

    [Fact]
    public void InjectTouchEvent()
    {
        var buf = ControlMessage.InjectTouch(AndroidMotionAction.Down, 0x1234567887654321UL,
            new DevicePosition(100, 200, 1080, 1920), 1.0f, AndroidMotionButtons.Primary, AndroidMotionButtons.Primary);
        Assert.Equal(new byte[]
        {
            0x02,
            0x00,
            0x12, 0x34, 0x56, 0x78, 0x87, 0x65, 0x43, 0x21,
            0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00, 0xc8,
            0x04, 0x38, 0x07, 0x80,
            0xff, 0xff,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x01,
        }, buf);
    }

    [Fact]
    public void InjectScrollEvent()
    {
        var buf = ControlMessage.InjectScroll(new DevicePosition(260, 1026, 1080, 1920), 16, -16, AndroidMotionButtons.Primary);
        Assert.Equal(new byte[]
        {
            0x03,
            0x00, 0x00, 0x01, 0x04, 0x00, 0x00, 0x04, 0x02,
            0x04, 0x38, 0x07, 0x80,
            0x7F, 0xFF,
            0x80, 0x00,
            0x00, 0x00, 0x00, 0x01,
        }, buf);
    }

    [Fact]
    public void BackOrScreenOn() => Assert.Equal(new byte[] { 0x04, 0x01 }, ControlMessage.BackOrScreenOn(AndroidKeyAction.Up));

    [Fact]
    public void SingleByteMessages()
    {
        Assert.Equal(new byte[] { 0x05 }, ControlMessage.ExpandNotificationPanel());
        Assert.Equal(new byte[] { 0x06 }, ControlMessage.ExpandSettingsPanel());
        Assert.Equal(new byte[] { 0x07 }, ControlMessage.CollapsePanels());
        Assert.Equal(new byte[] { 0x0B }, ControlMessage.RotateDevice());
        Assert.Equal(new byte[] { 0x0F }, ControlMessage.OpenHardKeyboardSettings());
        Assert.Equal(new byte[] { 0x11 }, ControlMessage.ResetVideo());
        Assert.Equal(new byte[] { 0x13 }, ControlMessage.CameraZoomIn());
        Assert.Equal(new byte[] { 0x14 }, ControlMessage.CameraZoomOut());
    }

    [Fact]
    public void GetClipboard() => Assert.Equal(new byte[] { 0x08, 0x01 }, ControlMessage.GetClipboard(CopyKey.Copy));

    [Fact]
    public void SetClipboard()
    {
        var buf = ControlMessage.SetClipboard(0x0102030405060708UL, "hello, world!", paste: true);
        Assert.Equal(27, buf.Length);
        Assert.Equal(new byte[]
        {
            0x09,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            1,
            0x00, 0x00, 0x00, 0x0d,
        }, buf[..14]);
        Assert.Equal("hello, world!"u8.ToArray(), buf[14..]);
    }

    [Fact]
    public void SetClipboardLongFillsTheMaximumMessage()
    {
        var buf = ControlMessage.SetClipboard(1, new string('a', ScrcpyProtocol.ClipboardTextMaxLength + 10), paste: false);
        Assert.Equal(ScrcpyProtocol.ControlMessageMaxSize, buf.Length);
    }

    [Fact]
    public void SetDisplayPower() => Assert.Equal(new byte[] { 0x0A, 0x01 }, ControlMessage.SetDisplayPower(true));

    [Fact]
    public void StartApp()
    {
        Assert.Equal(new byte[] { 0x10, 7, (byte)'f', (byte)'i', (byte)'r', (byte)'e', (byte)'f', (byte)'o', (byte)'x' },
            ControlMessage.StartApp("firefox"));
    }

    [Fact]
    public void CameraSetTorch() => Assert.Equal(new byte[] { 0x12, 0x01 }, ControlMessage.CameraSetTorch(true));

    [Fact]
    public void ResizeDisplay() =>
        Assert.Equal(new byte[] { 0x15, 1920 >> 8, 1920 & 0xff, 1080 >> 8, 1080 & 0xff }, ControlMessage.ResizeDisplay(1920, 1080));

    [Fact]
    public void ScanFile()
    {
        var buf = ControlMessage.ScanFile("/sdcard/Download");
        Assert.Equal(21, buf.Length);
        Assert.Equal(new byte[] { 0x16, 0x00, 0x00, 0x00, 0x10 }, buf[..5]);
        Assert.Equal("/sdcard/Download"u8.ToArray(), buf[5..]);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 0x8000)]
    [InlineData(1f, 0xffff)]
    public void PressureFixedPoint(float f, int expected) => Assert.Equal((ushort)expected, ControlMessage.FloatToU16Fixed(f));
}
