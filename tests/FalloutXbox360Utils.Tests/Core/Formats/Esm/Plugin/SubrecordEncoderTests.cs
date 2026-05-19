using System.Buffers.Binary;
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

        var roundTrip = BinaryPrimitives.ReadSingleLittleEndian(buf);
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

    // ====================================================================================
    // XXXX extended-size subrecord (payload > 64KB)
    // Payloads that exceed uint16 must emit the XXXX prefix carrying the real size as a
    // uint32, followed by the real signature with size=0. Required for WRLD OFST tables.
    // ====================================================================================

    [Fact]
    public void WriteSubrecord_EmitsXxxxPrefixWhenPayloadExceeds64Kb()
    {
        var payload = new byte[100_000]; // > 65535
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        SubrecordEncoder.WriteSubrecord(bw, "OFST", payload);
        bw.Flush();

        var bytes = ms.ToArray();

        // Total bytes = XXXX header (6) + XXXX payload (4) + real header (6) + payload (100_000).
        Assert.Equal(6 + 4 + 6 + payload.Length, bytes.Length);

        // First subrecord is "XXXX" with size=4 carrying the real payload size.
        Assert.Equal((byte)'X', bytes[0]);
        Assert.Equal((byte)'X', bytes[1]);
        Assert.Equal((byte)'X', bytes[2]);
        Assert.Equal((byte)'X', bytes[3]);
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
        Assert.Equal(100_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6, 4)));

        // Second subrecord is the real sig "OFST" with size=0 (real size in XXXX prefix).
        Assert.Equal((byte)'O', bytes[10]);
        Assert.Equal((byte)'F', bytes[11]);
        Assert.Equal((byte)'S', bytes[12]);
        Assert.Equal((byte)'T', bytes[13]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2)));

        // Payload bytes follow verbatim.
        Assert.Equal(payload[0], bytes[16]);
        Assert.Equal(payload[^1], bytes[^1]);
    }

    [Fact]
    public void WriteSubrecord_NormalPathUnchangedForSmallPayload()
    {
        var payload = new byte[100];
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        SubrecordEncoder.WriteSubrecord(bw, "DATA", payload);
        bw.Flush();

        var bytes = ms.ToArray();

        // Total = single 6-byte header + 100 payload = 106. No XXXX prefix.
        Assert.Equal(106, bytes.Length);
        Assert.Equal((byte)'D', bytes[0]);
        Assert.Equal((byte)'A', bytes[1]);
        Assert.Equal((byte)'T', bytes[2]);
        Assert.Equal((byte)'A', bytes[3]);
        Assert.Equal(100, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
    }

    [Fact]
    public void WriteSubrecord_BoundaryAtExactly65535()
    {
        var payload = new byte[ushort.MaxValue]; // 65535 — still fits in uint16, no XXXX
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        SubrecordEncoder.WriteSubrecord(bw, "OFST", payload);
        bw.Flush();

        var bytes = ms.ToArray();
        Assert.Equal(6 + payload.Length, bytes.Length);
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
    }

    [Fact]
    public void WriteSubrecord_OneOverBoundaryUsesXxxx()
    {
        var payload = new byte[ushort.MaxValue + 1]; // 65536 — must use XXXX
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        SubrecordEncoder.WriteSubrecord(bw, "OFST", payload);
        bw.Flush();

        var bytes = ms.ToArray();
        Assert.Equal(6 + 4 + 6 + payload.Length, bytes.Length);
        Assert.Equal((byte)'X', bytes[0]);
    }
}
