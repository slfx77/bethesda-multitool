using System.Buffers.Binary;
using System.IO.Compression;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Indexing;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Conversion;

/// <summary>
///     Tests for <see cref="EsmRecordCompression" />'s decompress-failure pass-through:
///     the fallback bytes must stay byte-identical to the historical behavior (LE size
///     prefix + original compressed payload) while the failure is now counted on
///     <see cref="EsmConversionStats" /> instead of vanishing silently.
/// </summary>
public class EsmRecordCompressionTests
{
    [Fact]
    public void ConvertCompressedRecordData_CorruptZlib_CountsFailureAndPreservesFallbackBytes()
    {
        // 4-byte BE decompressed-size prefix + garbage that fails the zlib header check.
        byte[] zlibGarbage = [0xFF, 0xFF, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        var input = new byte[4 + zlibGarbage.Length];
        BinaryPrimitives.WriteUInt32BigEndian(input, 0x40);
        zlibGarbage.CopyTo(input, 4);
        var stats = new EsmConversionStats();

        var result = EsmRecordCompression.ConvertCompressedRecordData(
            input, 0, input.Length, "NPC_", stats);

        // The fallback output is exactly what it always was: the BE-read size written back
        // little-endian, followed by the untouched compressed payload.
        Assert.NotNull(result);
        var expected = new byte[4 + zlibGarbage.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, 0x40);
        zlibGarbage.CopyTo(expected, 4);
        Assert.Equal(expected, result);

        Assert.Equal(1, stats.DecompressFailuresPassedThrough);
        Assert.Equal(1, stats.DecompressFailureRecordTypeCounts["NPC_"]);
    }

    [Fact]
    public void ConvertCompressedRecordData_ValidZlib_DoesNotCountAFailure()
    {
        // A valid zlib stream holding zero decompressed bytes: the subrecord conversion
        // loop is a no-op, so this isolates the counter from converter behavior.
        using var compressedStream = new MemoryStream();
        using (var zlibStream = new ZLibStream(compressedStream, CompressionLevel.Optimal, true))
        {
            zlibStream.Write(ReadOnlySpan<byte>.Empty);
        }

        var zlibData = compressedStream.ToArray();
        var input = new byte[4 + zlibData.Length];
        BinaryPrimitives.WriteUInt32BigEndian(input, 0);
        zlibData.CopyTo(input, 4);
        var stats = new EsmConversionStats();

        var result = EsmRecordCompression.ConvertCompressedRecordData(
            input, 0, input.Length, "GMST", stats);

        Assert.NotNull(result);
        Assert.Equal(0, stats.DecompressFailuresPassedThrough);
        Assert.Empty(stats.DecompressFailureRecordTypeCounts);
    }

    [Fact]
    public void GetStatsSummary_ReportsDecompressFailures_OnlyWhenPresent()
    {
        var stats = new EsmConversionStats();
        Assert.DoesNotContain("passed through raw", stats.GetStatsSummary(), StringComparison.Ordinal);

        stats.IncrementDecompressFailurePassedThrough("NPC_");
        stats.IncrementDecompressFailurePassedThrough("NPC_");
        stats.IncrementDecompressFailurePassedThrough("WEAP");

        var summary = stats.GetStatsSummary();
        Assert.Contains("Compressed records passed through raw", summary, StringComparison.Ordinal);
        Assert.Contains("3 records", summary, StringComparison.Ordinal);
        Assert.Contains("NPC_: 2", summary, StringComparison.Ordinal);
        Assert.Contains("WEAP: 1", summary, StringComparison.Ordinal);
    }
}
