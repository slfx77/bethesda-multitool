using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Tests for <see cref="SubrecordEncoder" /> — verifies typed values are written as PC
///     little-endian bytes and that the framed subrecord header layout is correct.
/// </summary>
public class SubrecordEncoderTests
{
    [Fact]
    public void WriteSubrecord_EmitsSignatureLengthAndPayload()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            SubrecordEncoder.WriteSubrecord(writer, "FLTV", new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        }

        var bytes = stream.ToArray();
        Assert.Equal(10, bytes.Length); // 4 sig + 2 length + 4 payload
        Assert.Equal((byte)'F', bytes[0]);
        Assert.Equal((byte)'L', bytes[1]);
        Assert.Equal((byte)'T', bytes[2]);
        Assert.Equal((byte)'V', bytes[3]);
        // Length is 4 in little-endian.
        Assert.Equal(0x04, bytes[4]);
        Assert.Equal(0x00, bytes[5]);
        // Payload bytes preserved.
        Assert.Equal(0xAA, bytes[6]);
        Assert.Equal(0xBB, bytes[7]);
        Assert.Equal(0xCC, bytes[8]);
        Assert.Equal(0xDD, bytes[9]);
    }

    [Fact]
    public void WriteUInt32_WritesLittleEndian()
    {
        Span<byte> buf = stackalloc byte[8];
        SubrecordEncoder.WriteUInt32(buf, 2, 0x11223344);

        Assert.Equal(0x00, buf[0]);
        Assert.Equal(0x00, buf[1]);
        Assert.Equal(0x44, buf[2]);
        Assert.Equal(0x33, buf[3]);
        Assert.Equal(0x22, buf[4]);
        Assert.Equal(0x11, buf[5]);
    }

    [Fact]
    public void WriteFloat_RoundTripsExact()
    {
        Span<byte> buf = stackalloc byte[4];
        SubrecordEncoder.WriteFloat(buf, 0, 1.34f);

        var roundTrip = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(buf);
        Assert.Equal(1.34f, roundTrip);
    }

    [Fact]
    public void WriteStringSubrecord_NullTerminates()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            SubrecordEncoder.WriteStringSubrecord(writer, "EDID", "fActorMult");
        }

        var bytes = stream.ToArray();
        // 4 sig + 2 length + 10 chars + 1 NUL = 17.
        Assert.Equal(17, bytes.Length);
        Assert.Equal(0x0B, bytes[4]); // length = 11
        Assert.Equal(0x00, bytes[5]);
        Assert.Equal((byte)'f', bytes[6]);
        Assert.Equal(0x00, bytes[16]);
    }

    [Fact]
    public void WriteSubrecord_EmitsXxxxPrefixForPayloadOver64K()
    {
        // v19 fix: payloads >64KB use the XXXX extended-size form rather than throwing.
        // Detailed byte-layout assertions live in V19ValidationFixesTests.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var huge = new byte[ushort.MaxValue + 1];
        SubrecordEncoder.WriteSubrecord(writer, "DATA", huge);
        Assert.True(stream.Length > huge.Length);
    }
}
