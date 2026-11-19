using Streamy.Core.Protocol;

namespace Streamy.Core.Tests;

/// <summary>Byte vectors ported from scrcpy 5.0.1 app/tests/test_device_msg_deserialize.c.</summary>
public class DeviceMessageTests
{
    [Fact]
    public void Clipboard()
    {
        byte[] input = [0x00, 0x00, 0x00, 0x00, 0x03, 0x41, 0x42, 0x43];
        Assert.True(DeviceMessage.TryRead(input, out var msg, out int consumed));
        Assert.Equal(8, consumed);
        Assert.Equal(new DeviceMessage.Clipboard("ABC"), msg);
    }

    [Fact]
    public void ClipboardBig()
    {
        var input = new byte[ScrcpyProtocol.DeviceMessageMaxSize];
        int len = ScrcpyProtocol.DeviceMessageTextMaxLength;
        input[1] = (byte)(len >> 24);
        input[2] = (byte)(len >> 16);
        input[3] = (byte)(len >> 8);
        input[4] = (byte)len;
        input.AsSpan(5).Fill((byte)'a');

        Assert.True(DeviceMessage.TryRead(input, out var msg, out int consumed));
        Assert.Equal(ScrcpyProtocol.DeviceMessageMaxSize, consumed);
        Assert.Equal(len, ((DeviceMessage.Clipboard)msg!).Text.Length);
    }

    [Fact]
    public void AckClipboard()
    {
        byte[] input = [0x01, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        Assert.True(DeviceMessage.TryRead(input, out var msg, out int consumed));
        Assert.Equal(9, consumed);
        Assert.Equal(new DeviceMessage.AckClipboard(0x0102030405060708UL), msg);
    }

    [Fact]
    public void UhidOutput()
    {
        byte[] input = [0x02, 0, 42, 0, 5, 1, 2, 3, 4, 5];
        Assert.True(DeviceMessage.TryRead(input, out var msg, out int consumed));
        Assert.Equal(10, consumed);
        var uhid = Assert.IsType<DeviceMessage.UhidOutput>(msg);
        Assert.Equal(42, uhid.Id);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, uhid.Data);
    }

    [Fact]
    public void PartialMessageWaitsForMoreBytes()
    {
        byte[] input = [0x00, 0x00, 0x00, 0x00, 0x03, 0x41];
        Assert.False(DeviceMessage.TryRead(input, out _, out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void UnknownTypeThrows() => Assert.Throws<InvalidDataException>(() => DeviceMessage.TryRead([0x7F], out _, out _));
}
