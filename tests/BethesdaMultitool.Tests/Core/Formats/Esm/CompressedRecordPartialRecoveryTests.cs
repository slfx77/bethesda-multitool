using System.Buffers.Binary;
using System.IO.Compression;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Conversion;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm;

/// <summary>
///     Pins the lenient partial-recovery decompressors used for memory-dump data preservation: a complete
///     stream round-trips fully; a stream truncated (as a partial dump capture cuts a compressed record's
///     zlib payload) yields its correct decompressible PREFIX rather than nothing; and an implausible size
///     prefix (signature-scan false positive) recovers nothing. The recovered prefix being a true prefix
///     of the original is what makes feeding it to the subrecord iterator safe.
/// </summary>
public class CompressedRecordPartialRecoveryTests
{
    // Deterministic, low-compressibility bytes so the compressed stream is sizeable enough to truncate.
    private static byte[] MakeData(int n)
    {
        var d = new byte[n];
        uint s = 0x12345678;
        for (var i = 0; i < n; i++)
        {
            s = (s * 1664525) + 1013904223;
            d[i] = (byte)(s >> 24);
        }

        return d;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data, 0, data.Length);
        }

        return ms.ToArray();
    }

    [Fact]
    public void DecompressZlibPartial_CompleteStream_ReturnsFullPayload()
    {
        var data = MakeData(64 * 1024);
        var (got, isComplete) = EsmHelpers.DecompressZlibPartial(ZlibCompress(data), data.Length);

        Assert.True(isComplete);
        Assert.Equal(data, got);
    }

    [Fact]
    public void DecompressZlibPartial_TruncatedStream_RecoversCorrectLeadingPrefix()
    {
        var data = MakeData(64 * 1024);
        var compressed = ZlibCompress(data);
        var truncated = compressed[..(int)(compressed.Length * 0.7)];

        var (got, isComplete) = EsmHelpers.DecompressZlibPartial(truncated, data.Length);

        Assert.False(isComplete);
        Assert.InRange(got.Length, 1, data.Length - 1);
        // The recovered bytes must be a true prefix of the original (DEFLATE never corrupts already-emitted
        // output when later input is missing) — this is what makes partial subrecord parsing safe.
        Assert.Equal(data[..got.Length], got);
    }

    [Fact]
    public void DecompressZlibPartial_HeaderOnly_RecoversNothing()
    {
        var (got, isComplete) = EsmHelpers.DecompressZlibPartial([0x78, 0x9C], 100);
        Assert.Empty(got);
        Assert.False(isComplete);
    }

    [Fact]
    public void DecompressRecordDataPartial_Truncated_RecoversPrefix()
    {
        var data = MakeData(64 * 1024);
        var compressed = ZlibCompress(data);
        var truncatedZlib = compressed[..(int)(compressed.Length * 0.7)];

        // Record payload = 4-byte decompressed-size prefix + the (truncated) zlib stream.
        var payload = new byte[4 + truncatedZlib.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)data.Length);
        truncatedZlib.CopyTo(payload, 4);

        var (got, isComplete) = EsmParser.DecompressRecordDataPartial(payload, bigEndian: false);

        Assert.False(isComplete);
        Assert.InRange(got.Length, 1, data.Length - 1);
        Assert.Equal(data[..got.Length], got);
    }

    [Fact]
    public void DecompressRecordDataPartial_BigEndianSizePrefix_Recovers()
    {
        var data = MakeData(32 * 1024);
        var compressed = ZlibCompress(data);
        var truncatedZlib = compressed[..(int)(compressed.Length * 0.7)];

        var payload = new byte[4 + truncatedZlib.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)data.Length); // Xbox 360 BE size prefix
        truncatedZlib.CopyTo(payload, 4);

        var (got, isComplete) = EsmParser.DecompressRecordDataPartial(payload, bigEndian: true);

        Assert.False(isComplete);
        Assert.Equal(data[..got.Length], got);
    }

    [Theory]
    [InlineData(0u)] // zero size = not a real compressed record
    [InlineData(0x7FFF_FFFFu)] // absurdly large (> 16 MB cap)
    public void DecompressRecordDataPartial_ImplausibleSizePrefix_RecoversNothing(uint declaredSize)
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, declaredSize);
        // Plausible-looking zlib header after the prefix, to prove the size-prefix gate is what rejects it.
        payload[4] = 0x78;
        payload[5] = 0x9C;

        var (got, isComplete) = EsmParser.DecompressRecordDataPartial(payload, bigEndian: false);

        Assert.Empty(got);
        Assert.False(isComplete);
    }
}

