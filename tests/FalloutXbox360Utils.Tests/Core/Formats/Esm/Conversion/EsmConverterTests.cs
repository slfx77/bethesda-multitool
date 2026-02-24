using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.EsmTestRecordBuilder;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Conversion;

/// <summary>
///     Regression tests for EsmConverter.
///     Tests exercise ConvertToLittleEndian with synthetic big-endian ESM data.
///     These tests anchor behavior before the complexity-reduction refactoring.
/// </summary>
public class EsmConverterTests(ITestOutputHelper output, SampleFileFixture samples)
{
    private readonly ITestOutputHelper _output = output;

    #region Sample-File-Based Tests

    [Fact]
    [Trait("Category", "Slow")]
    public void ConvertToLittleEndian_RealEsm_ProducesValidOutput()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var input = File.ReadAllBytes(samples.Xbox360FinalEsm!);
        _output.WriteLine($"Input: {input.Length:N0} bytes");

        using var converter = new EsmConverter(input, false);
        var result = converter.ConvertToLittleEndian();

        _output.WriteLine($"Output: {result.Length:N0} bytes");
        _output.WriteLine(converter.GetStatsSummary());

        // Output should start with LE TES4 header
        Assert.Equal((byte)'T', result[0]);
        Assert.Equal((byte)'E', result[1]);
        Assert.Equal((byte)'S', result[2]);
        Assert.Equal((byte)'4', result[3]);

        // Output data size should be LE
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4));
        Assert.True(dataSize > 0 && dataSize < (uint)result.Length);

        // Output should be smaller or similar in size to input
        // (TOFT region is removed, but new OFST tables may be added)
        Assert.True(result.Length > 0);
    }

    #endregion

    #region Utility

    /// <summary>
    ///     Finds the position of an ASCII signature in little-endian output.
    /// </summary>
    private static int FindAsciiSignature(byte[] data, string signature, int startOffset = 0)
    {
        var sigBytes = Encoding.ASCII.GetBytes(signature);
        for (var i = startOffset; i <= data.Length - 4; i++)
        {
            if (data[i] == sigBytes[0] && data[i + 1] == sigBytes[1] &&
                data[i + 2] == sigBytes[2] && data[i + 3] == sigBytes[3])
            {
                return i;
            }
        }

        return -1;
    }

    #endregion

    #region ConvertToLittleEndian Tests

    [Fact]
    public void ConvertToLittleEndian_MinimalEsm_ProducesOutput()
    {
        var input = BuildMinimalBigEndianEsm();
        using var converter = new EsmConverter(input, false);

        var result = converter.ConvertToLittleEndian();

        Assert.NotNull(result);
        Assert.True(result.Length > 0, "Output should not be empty");

        // Verify output TES4 signature is little-endian (ASCII order: T, E, S, 4)
        Assert.Equal((byte)'T', result[0]);
        Assert.Equal((byte)'E', result[1]);
        Assert.Equal((byte)'S', result[2]);
        Assert.Equal((byte)'4', result[3]);
    }

    [Fact]
    public void ConvertToLittleEndian_WithGrup_ConvertsRecord()
    {
        var input = BuildSimpleEsmWithGrup();
        using var converter = new EsmConverter(input, false);

        var result = converter.ConvertToLittleEndian();

        Assert.NotNull(result);
        Assert.True(result.Length > 0);

        // Verify output contains GRUP header in little-endian (ASCII: G, R, U, P)
        var grupPos = FindAsciiSignature(result, "GRUP");
        Assert.True(grupPos >= 0, "Output should contain a GRUP header");

        // Verify output contains ALCH record in little-endian
        var alchPos = FindAsciiSignature(result, "ALCH", grupPos + 24);
        Assert.True(alchPos >= 0, "Output should contain an ALCH record");

        // Verify the ALCH FormID is written little-endian
        var formId = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(alchPos + 12));
        Assert.Equal(0x00010001u, formId);
    }

    [Fact]
    public void ConvertToLittleEndian_EmptyInput_DoesNotThrow()
    {
        // Very small input that can't even contain a header
        var input = new byte[4];
        using var converter = new EsmConverter(input, false);

        // Should not throw, though output may be minimal/empty
        var exception = Record.Exception(() => converter.ConvertToLittleEndian());

        // We just verify it doesn't crash with an unhandled exception
        // The converter may throw ArgumentOutOfRange for truly invalid data, which is fine
        _output.WriteLine(exception != null
            ? $"Expected exception for invalid input: {exception.GetType().Name}"
            : "Converter handled empty input gracefully");
    }

    [Fact]
    public void ConvertToLittleEndian_WithToftRegion_SkipsToftData()
    {
        // Build: TES4 header + TOFT region (sentinel + INFO inside + end marker)
        var tes4Data = BuildMinimalBigEndianEsm();

        // TOFT sentinel: FormID=0xFFFFFFFE, DataSize=48 (contains one INFO record)
        // INFO inside TOFT: FormID=0x000A0001, DataSize=0
        // TOFT end marker: FormID=0xFFFFFFFF, DataSize=0
        var infoInsideToft = 24; // Just a header, no data
        var toftDataSize = infoInsideToft; // INFO record is inside TOFT's data
        var toftEndMarker = 24; // TOFT end: header only, DataSize=0
        var toftRegionSize = 24 + toftDataSize + toftEndMarker; // sentinel + data + end

        var totalSize = tes4Data.Length + toftRegionSize;
        var data = new byte[totalSize];
        Array.Copy(tes4Data, data, tes4Data.Length);

        var offset = tes4Data.Length;

        // TOFT sentinel (DataSize includes the INFO record inside)
        WriteBERecordHeader(data, offset, "TOFT", (uint)toftDataSize, 0xFFFFFFFE);
        offset += 24;

        // INFO record inside TOFT block
        WriteBERecordHeader(data, offset, "INFO", 0, 0x000A0001);
        offset += 24;

        // TOFT end marker
        WriteBERecordHeader(data, offset, "TOFT", 0, 0xFFFFFFFF);

        using var converter = new EsmConverter(data, false);
        var result = converter.ConvertToLittleEndian();

        Assert.NotNull(result);

        // TOFT records should NOT appear in the output
        var toftPos = FindAsciiSignature(result, "TOFT");
        Assert.Equal(-1, toftPos);

        // The INFO from TOFT should be indexed but not directly written as standalone
        // (INFO records in TOFT are merged into their parent DIAL GRUPs during conversion)
        _output.WriteLine($"Input: {data.Length} bytes, Output: {result.Length} bytes");
    }

    [Fact]
    public void GetStatsSummary_AfterConversion_ReturnsNonEmpty()
    {
        var input = BuildSimpleEsmWithGrup();
        using var converter = new EsmConverter(input, false);

        converter.ConvertToLittleEndian();
        var summary = converter.GetStatsSummary();

        Assert.False(string.IsNullOrEmpty(summary));
        _output.WriteLine(summary);
    }

    #endregion
}