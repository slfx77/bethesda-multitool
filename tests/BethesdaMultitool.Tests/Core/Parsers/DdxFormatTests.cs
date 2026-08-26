using System.Text;
using BethesdaMultitool.Core.Formats.Ddx;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Parsers;

/// <summary>
///     Tests for DdxFormat.
/// </summary>
public class DdxFormatTests
{
    private readonly DdxFormat _parser = new();

    #region Size Estimation Tests

    [Fact]
    public void ParseHeader_ReturnsPositiveEstimatedSize()
    {
        // Arrange
        var data = Create3XdoHeader(256, 256);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.EstimatedSize > 0);
    }

    #endregion

    #region Magic Bytes Tests

    [Fact]
    public void ParseHeader_3XDOMagic_ReturnsResult()
    {
        // Arrange
        var data = Create3XdoHeader(256, 256);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("3XDO", result.Format);
        // DDX is implicitly Xbox 360 format
        Assert.True(result.Metadata.ContainsKey("width"));
    }

    [Fact]
    public void ParseHeader_3XDRMagic_ReturnsResult()
    {
        // Arrange
        var data = Create3XdrHeader(256, 256);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("3XDR", result.Format);
        // DDX is implicitly Xbox 360 format
        Assert.True(result.Metadata.ContainsKey("width"));
    }

    [Fact]
    public void ParseHeader_InvalidMagic_ReturnsNull()
    {
        // Arrange
        var data = new byte[100];
        "XXXX"u8.CopyTo(data.AsSpan(0));

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region GPU Format Validation Tests

    [Fact]
    public void ParseHeader_UnknownGpuFormat_ReturnsNull()
    {
        // Arrange - create header with an unknown/invalid GPU format byte (0xFF)
        var data = CreateDdxHeaderWithFormat("3XDO", 256, 256, 4, 0xFF);

        // Act
        var result = _parser.Parse(data);

        // Assert - unknown GPU formats should be rejected to reduce false positives
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0x12, "DXT1")] // Known DXT1 format
    [InlineData(0x52, "DXT1")] // Known DXT1 format (alternate)
    [InlineData(0x14, "DXT5")] // Known DXT5 format
    [InlineData(0x54, "DXT5")] // Known DXT5 format (alternate)
    public void ParseHeader_KnownGpuFormat_ReturnsResult(int formatByte, string expectedFormat)
    {
        // Arrange
        var data = CreateDdxHeaderWithFormat("3XDO", 256, 256, 4, (byte)formatByte);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedFormat, result.Metadata["formatName"]);
    }

    #endregion

    #region Dimension Tests

    [Theory]
    [InlineData(64, 64)]
    [InlineData(128, 128)]
    [InlineData(256, 256)]
    [InlineData(512, 512)]
    [InlineData(1024, 1024)]
    [InlineData(2048, 2048)]
    [InlineData(4096, 4096)]
    [InlineData(256, 512)] // Non-square
    [InlineData(1024, 256)] // Non-square
    public void ParseHeader_ValidDimensions_ReturnsCorrectDimensions(int width, int height)
    {
        // Arrange
        var data = Create3XdoHeader(width, height);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(width, result.Metadata["width"]);
        Assert.Equal(height, result.Metadata["height"]);
    }

    [Fact]
    public void ParseHeader_OversizedDimensions_ReturnsNull()
    {
        // Arrange - dimensions > 4096 are invalid
        var data = Create3XdoHeader(8192, 8192);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Version Tests

    [Fact]
    public void ParseHeader_ValidVersion_ReturnsResult()
    {
        // Arrange - version 4 is common
        var data = Create3XdoHeader(256, 256);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(4, result.Metadata["version"]);
    }

    [Fact]
    public void ParseHeader_InvalidVersion_ReturnsNull()
    {
        // Arrange - version < 3 is invalid
        var data = Create3XdoHeader(256, 256, 2);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Framing Walk Tests

    [Fact]
    public void Parse_SingleLzxStream_ReturnsExactFramedExtent()
    {
        // Arrange - one 0xFF-terminated stream; the header's 0x3C total is already covered by it,
        // so no second stream may be admitted.
        var stream = SyntheticDdxPayload.FrameStream(new byte[4096]);
        var header = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256,
            uncompressedSize: 4096, firstStreamLength: (uint)stream.Length);
        var data = SyntheticDdxPayload.Concat(header, stream);

        // Act
        var result = _parser.Parse(data);

        // Assert - the walk lands exactly on the end of the payload, no heuristic involved
        Assert.NotNull(result);
        Assert.Equal(data.Length, result.EstimatedSize);
        Assert.Equal(1, result.Metadata["lzxStreamCount"]);
        Assert.True(Assert.IsType<bool>(result.Metadata["headerStreamLengthAgrees"]));
        Assert.False(result.Metadata.ContainsKey("boundaryFallback"));
    }

    [Fact]
    public void Parse_TwoLzxStreams_WalksBothWhenHeaderTotalDemandsIt()
    {
        // Arrange - the real [mips stream][main stream] layout: stream 1 alone falls short of the
        // 0x3C total, so stream 2 is admitted.
        var first = SyntheticDdxPayload.FrameStream(new byte[4096]);
        var second = SyntheticDdxPayload.FrameStream(new byte[2048]);
        var header = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256,
            uncompressedSize: 4096 + 2048, firstStreamLength: (uint)first.Length);
        var data = SyntheticDdxPayload.Concat(header, first, second);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(data.Length, result.EstimatedSize);
        Assert.Equal(2, result.Metadata["lzxStreamCount"]);
        Assert.False(result.Metadata.ContainsKey("boundaryFallback"));
    }

    [Fact]
    public void Parse_SecondStreamCorruptMidway_KeepsItsCleanlyFramedPrefix()
    {
        // Arrange - the dump-copy shape (measured on rugsmall01.ddx in Fallout_Debug.xex.dmp):
        // stream 1 is intact, stream 2's first chunk frames fine and the next one is garbage.
        // Dropping the whole of stream 2 there loses recoverable mips, so the prefix is kept.
        var first = SyntheticDdxPayload.FrameStream(new byte[4096]);
        var goodChunk = SyntheticDdxPayload.FrameRawContinuationChunk(1000, 1000);
        var corruptTail = new byte[512];
        corruptTail[0] = 0x98; // declares 0x980B compressed => over MaxTotalChunkSize
        corruptTail[1] = 0x0B;
        var header = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256,
            uncompressedSize: 4096 + SyntheticDdxPayload.ChunkPayloadMax,
            firstStreamLength: (uint)first.Length);
        var data = SyntheticDdxPayload.Concat(header, first, goodChunk, corruptTail);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(SyntheticDdxPayload.HeaderSize + first.Length + goodChunk.Length, result.EstimatedSize);
        Assert.Equal(2, result.Metadata["lzxStreamCount"]);
        Assert.False(result.Metadata.ContainsKey("boundaryFallback"));
    }

    [Fact]
    public void Parse_SecondStreamPresent_NotAdmittedWhenHeaderTotalAlreadySatisfied()
    {
        // Arrange - identical bytes to the two-stream case, but the header says stream 1 already
        // carries the whole uncompressed surface, so the trailing bytes belong to another file.
        var first = SyntheticDdxPayload.FrameStream(new byte[4096]);
        var second = SyntheticDdxPayload.FrameStream(new byte[2048]);
        var header = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256,
            uncompressedSize: 4096, firstStreamLength: (uint)first.Length);
        var data = SyntheticDdxPayload.Concat(header, first, second);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(SyntheticDdxPayload.HeaderSize + first.Length, result.EstimatedSize);
        Assert.Equal(1, result.Metadata["lzxStreamCount"]);
    }

    [Fact]
    public void Parse_ZeroSizedChunkHeader_RejectsWalkAndFallsBack()
    {
        // Arrange - an all-zero payload. Without the zero-size guard the walk would "advance"
        // 2 bytes per chunk all the way to the end of the window and report success.
        var header = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256, uncompressedSize: 65536);
        var data = SyntheticDdxPayload.Concat(header, new byte[4096]);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0, result.Metadata["lzxStreamCount"]);
        Assert.True(Assert.IsType<bool>(result.Metadata["boundaryFallback"]));
        Assert.Equal(
            "LZX chunk framing walk failed and no boundary token found; size = header + 0.7*uncompressed",
            result.Metadata["boundaryFallbackReason"]);
        Assert.Equal(0x44 + Math.Max(100, 65536 * 7 / 10), result.EstimatedSize);
    }

    [Theory]
    [InlineData(0x9800, true)] // total 0x9802 — inside MaxTotalChunkSize (0x980A)
    [InlineData(0x980B, false)] // total 0x980D — over it, so the chunk is not a chunk
    public void Parse_ChunkSizeAgainstMaxTotalChunkSize(int declaredCompressedSize, bool expectWalkToSucceed)
    {
        // Arrange - a continuation chunk of the declared size (fully present in the buffer, so the
        // cap is the only thing that can reject it), then a normal terminating chunk.
        var continuation = SyntheticDdxPayload.FrameRawContinuationChunk(
            declaredCompressedSize, declaredCompressedSize);
        var terminal = SyntheticDdxPayload.FrameStream(new byte[1024]);
        var header = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256,
            uncompressedSize: SyntheticDdxPayload.ChunkPayloadMax + 1024);
        var data = SyntheticDdxPayload.Concat(header, continuation, terminal);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        if (expectWalkToSucceed)
        {
            Assert.Equal(data.Length, result.EstimatedSize);
            Assert.Equal(1, result.Metadata["lzxStreamCount"]);
            Assert.False(result.Metadata.ContainsKey("boundaryFallback"));
        }
        else
        {
            Assert.Equal(0, result.Metadata["lzxStreamCount"]);
            Assert.True(Assert.IsType<bool>(result.Metadata["boundaryFallback"]));
        }
    }

    [Fact]
    public void Parse_TruncatedFinalChunk_RejectsWalkAndFallsBack()
    {
        // Arrange - the terminal chunk's declared span runs past the end of the buffer.
        var stream = SyntheticDdxPayload.FrameStream(new byte[4096]);
        var header = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256, uncompressedSize: 4096);
        var data = SyntheticDdxPayload.Concat(header, stream[..^5]);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0, result.Metadata["lzxStreamCount"]);
        Assert.True(Assert.IsType<bool>(result.Metadata["boundaryFallback"]));
    }

    [Fact]
    public void Parse_NextDdxHeaderImmediatelyAfterStream_WalkStopsThereWithoutFallback()
    {
        // Arrange - a complete stream followed by the next file's header. The walk ends exactly at
        // the boundary on its own, so no fallback metadata is emitted.
        var stream = SyntheticDdxPayload.FrameStream(new byte[4096]);
        var header = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256,
            uncompressedSize: 4096, firstStreamLength: (uint)stream.Length);
        var next = SyntheticDdxPayload.BuildHeader("3XDO", 64, 64, uncompressedSize: 1024);
        var data = SyntheticDdxPayload.Concat(header, stream, next);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(SyntheticDdxPayload.HeaderSize + stream.Length, result.EstimatedSize);
        Assert.False(result.Metadata.ContainsKey("boundaryFallback"));
    }

    [Fact]
    public void Parse_BoundaryTokenInsideUnprovenTail_CapsTheCarveSize()
    {
        // Arrange - stream 1 terminates cleanly; stream 2 is an unterminated prefix (its second
        // chunk is garbage) that runs straight into the next file's header. Bytes no terminator
        // vouches for must never swallow the following file.
        var first = SyntheticDdxPayload.FrameStream(new byte[512]);
        var unprovenTail = SyntheticDdxPayload.FrameRawContinuationChunk(2000, 2000);
        SyntheticDdxPayload.BuildHeader("3XDO", 64, 64, uncompressedSize: 1024).CopyTo(unprovenTail, 2 + 500);
        var corruptTail = new byte[64];
        corruptTail[0] = 0x98; // over MaxTotalChunkSize, so stream 2 never reaches a terminator
        corruptTail[1] = 0x0B;
        var header = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256,
            uncompressedSize: 512 + SyntheticDdxPayload.ChunkPayloadMax,
            firstStreamLength: (uint)first.Length);
        var data = SyntheticDdxPayload.Concat(header, first, unprovenTail, corruptTail);

        var embeddedTokenOffset = SyntheticDdxPayload.HeaderSize + first.Length + 2 + 500;

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Equal("3XDO", Encoding.ASCII.GetString(data, embeddedTokenOffset, 4));
        Assert.NotNull(result);
        Assert.Equal(embeddedTokenOffset, result.EstimatedSize);
    }

    [Fact]
    public void Parse_FalseTokenInsideTerminatorProvenPayload_DoesNotTruncate()
    {
        // Arrange - a 4-byte magic inside real LZX output is a far weaker signal than the chunk
        // framing itself. Three files in the 26,122-file July 2010 corpus carry one (an "XEX2",
        // a PNG magic, and a header that even passes the next-DDX validation) and capping on it
        // would have cost them up to 97% of their bytes.
        var payload = new byte[4096];
        "XEX2"u8.CopyTo(payload.AsSpan(1024));
        var stream = SyntheticDdxPayload.FrameStream(payload);
        var header = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256,
            uncompressedSize: 4096, firstStreamLength: (uint)stream.Length);
        var data = SyntheticDdxPayload.Concat(header, stream);

        // Act
        var result = _parser.Parse(data);

        // Assert - the terminator proves the extent; the false token is ignored
        Assert.NotNull(result);
        Assert.Equal(data.Length, result.EstimatedSize);
    }

    #endregion

    #region Mip Count Tests

    [Theory]
    [InlineData(4096, 1)] // not even mip 0 (32768 bytes) fits
    [InlineData(65536, 9)] // the whole 256x256 DXT1 chain: 32768+8192+2048+512+128+32+8+8+8
    public void Parse_MipCount_DerivedFromUncompressedDataSize(uint uncompressedSize, int expectedMipCount)
    {
        // Arrange
        var header = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256, uncompressedSize: uncompressedSize);
        var data = SyntheticDdxPayload.Concat(header, new byte[256]);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal((int)uncompressedSize, result.Metadata["uncompressedSize"]);
        Assert.Equal(expectedMipCount, result.Metadata["mipCount"]);
    }

    [Fact]
    public void Parse_MipCount_IgnoresFetchConstantBits16To19()
    {
        // Arrange - bits 16-19 of the dword at 0x28 belong to the fetch constant's base_address
        // field (always 0 in a DDX file), NOT to a mip count. The old decode read them as
        // "mip count - 1" and therefore reported 1 mip for every file ever measured. Setting them
        // must not move the answer.
        var clean = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256, uncompressedSize: 65536);
        var polluted = SyntheticDdxPayload.BuildHeader("3XDO", 256, 256, uncompressedSize: 65536);
        polluted[0x29] |= 0x0F;

        // Act
        var cleanResult = _parser.Parse(SyntheticDdxPayload.Concat(clean, new byte[256]));
        var pollutedResult = _parser.Parse(SyntheticDdxPayload.Concat(polluted, new byte[256]));

        // Assert
        Assert.NotNull(cleanResult);
        Assert.NotNull(pollutedResult);
        Assert.Equal(9, cleanResult.Metadata["mipCount"]);
        Assert.Equal(cleanResult.Metadata["mipCount"], pollutedResult.Metadata["mipCount"]);
    }

    [Fact]
    public void Parse_NoDeclaredUncompressedSize_FallsBackToTiledMipChainTotal()
    {
        // Arrange - a zero dword at 0x3C means the tiled mip-0 extent plus the sequential tiled
        // mip chain (the same expression DdxParser uses for its decompress hint): for 256x256
        // DXT1 that is 32768 + 4 * 8192.
        var data = SyntheticDdxPayload.Concat(
            SyntheticDdxPayload.BuildHeader("3XDO", 256, 256), new byte[256]);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(32768 + 4 * 8192, result.Metadata["uncompressedSize"]);
    }

    #endregion

    #region Helper Methods

    private static byte[] Create3XdoHeader(int width, int height, ushort version = 4)
    {
        return CreateDdxHeader("3XDO", width, height, version);
    }

    private static byte[] Create3XdrHeader(int width, int height, ushort version = 4)
    {
        return CreateDdxHeader("3XDR", width, height, version);
    }

    private static byte[] CreateDdxHeader(string magic, int width, int height, ushort version)
    {
        return CreateDdxHeaderWithFormat(magic, width, height, version, 0x52); // DXT1 format
    }

    private static byte[] CreateDdxHeaderWithFormat(string magic, int width, int height, ushort version, byte gpuFormat)
    {
        // Create a minimal DDX header (0x44 = 68 bytes minimum)
        var data = new byte[200];

        // Magic at 0x00
        Encoding.ASCII.GetBytes(magic).CopyTo(data, 0);

        // Version at 0x07 (little-endian)
        data[7] = (byte)(version & 0xFF);
        data[8] = (byte)((version >> 8) & 0xFF);

        // Flags at 0x24 - must have high bit set (>= 0x80)
        data[0x24] = 0x80;

        // Fetch-constant dword 1 at 0x28 (big-endian). Its LOW byte is the GPU texture format;
        // bits 8-31 are the GPU base_address, which is always zero in a DDX file. Bits 16-19 are
        // NOT a mip count — reading them as one is the bug this fixture used to encode. Mip count
        // now comes from how much uncompressed data the file actually holds.
        uint formatDword = gpuFormat;
        data[0x28] = (byte)((formatDword >> 24) & 0xFF);
        data[0x29] = (byte)((formatDword >> 16) & 0xFF);
        data[0x2A] = (byte)((formatDword >> 8) & 0xFF);
        data[0x2B] = (byte)(formatDword & 0xFF);

        // Size dword at 0x2C (big-endian)
        // Bits 0-12: width - 1
        // Bits 13-25: height - 1
        // Clamp to valid range for the encoding
        var encodedWidth = Math.Max(0, Math.Min(width - 1, 0x1FFF));
        var encodedHeight = Math.Max(0, Math.Min(height - 1, 0x1FFF));
        var sizeDword = (uint)(encodedWidth & 0x1FFF) | (uint)((encodedHeight & 0x1FFF) << 13);
        data[0x2C] = (byte)((sizeDword >> 24) & 0xFF);
        data[0x2D] = (byte)((sizeDword >> 16) & 0xFF);
        data[0x2E] = (byte)((sizeDword >> 8) & 0xFF);
        data[0x2F] = (byte)(sizeDword & 0xFF);

        return data;
    }

    #endregion
}