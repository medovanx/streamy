using Streamy.Core.Protocol;

namespace Streamy.Core.Tests;

public class StreamPacketTests
{
    [Fact]
    public void SessionPacket()
    {
        byte[] h = [0x80, 0, 0, 0x01, 0, 0, 0x04, 0x38, 0, 0, 0x09, 0x60];
        var p = PacketHeader.Parse(h);
        Assert.True(p.IsSession);
        Assert.True(p.ClientResized);
        Assert.Equal(1080, p.Width);
        Assert.Equal(2400, p.Height);
    }

    [Fact]
    public void ConfigPacket()
    {
        byte[] h = [0x40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x20];
        var p = PacketHeader.Parse(h);
        Assert.False(p.IsSession);
        Assert.True(p.IsConfig);
        Assert.False(p.IsKeyFrame);
        Assert.Equal(32, p.PayloadSize);
    }

    [Fact]
    public void KeyFramePacketKeepsItsPts()
    {
        // key frame flag (bit 61) + PTS 0x0102030405
        byte[] h = [0x20, 0, 0, 0x01, 0x02, 0x03, 0x04, 0x05, 0, 0x01, 0, 0];
        var p = PacketHeader.Parse(h);
        Assert.True(p.IsKeyFrame);
        Assert.False(p.IsConfig);
        Assert.Equal(0x0102030405UL, p.Pts);
        Assert.Equal(65536, p.PayloadSize);
    }

    [Fact]
    public void ZeroSizeIsRejected() => Assert.Throws<InvalidDataException>(() => PacketHeader.Parse(new byte[12]));

    [Theory]
    [InlineData(new byte[] { 0x68, 0x32, 0x36, 0x34 }, CodecId.H264)]
    [InlineData(new byte[] { 0x68, 0x32, 0x36, 0x35 }, CodecId.H265)]
    [InlineData(new byte[] { 0x6F, 0x70, 0x75, 0x73 }, CodecId.Opus)]
    [InlineData(new byte[] { 0, 0, 0, 0 }, CodecId.Disabled)]
    [InlineData(new byte[] { 0, 0, 0, 1 }, CodecId.Error)]
    public void Codec(byte[] bytes, CodecId expected) => Assert.Equal(expected, PacketHeader.ReadCodec(bytes));

    [Fact]
    public void DeviceNameStopsAtNul()
    {
        var field = new byte[ScrcpyProtocol.DeviceNameFieldLength];
        "SM-A366B"u8.CopyTo(field);
        Assert.Equal("SM-A366B", DeviceMeta.ParseName(field));
    }
}
