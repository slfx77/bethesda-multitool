using Xbox360MemoryCarver.Core.Formats.Xma;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Parsers;

/// <summary>
///     Tests for XmaFormat.
/// </summary>
public class XmaFormatTests
{
    private readonly XmaFormat _parser = new();

    #region Magic Bytes Tests

    [Fact]
    public void ParseHeader_ValidRiffWave_WithXma2Chunk_ReturnsResult()
    {
        // Arrange
        var data = CreateXmaHeader(true);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("XMA", result.Format);
        Assert.True((bool)result.Metadata["isXma"]);
    }

    [Fact]
    public void ParseHeader_ValidRiffWave_WithFmtXmaFormat_ReturnsResult()
    {
        // Arrange
        var data = CreateXmaHeader(false, 0x0165);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("XMA", result.Format);
    }

    [Fact]
    public void ParseHeader_ValidRiffWave_WithXma2Format_ReturnsResult()
    {
        // Arrange
        var data = CreateXmaHeader(false, 0x0166);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("XMA", result.Format);
    }

    [Fact]
    public void ParseHeader_InvalidMagic_ReturnsNull()
    {
        // Arrange
        var data = new byte[64];
        "XXXX"u8.CopyTo(data.AsSpan(0));

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_RiffButNotWave_ReturnsNull()
    {
        // Arrange - RIFF but AVI format instead of WAVE
        var data = new byte[64];
        "RIFF"u8.CopyTo(data.AsSpan(0));
        WriteUInt32LE(data, 4, 1000);
        "AVI "u8.CopyTo(data.AsSpan(8));

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_RiffWave_ButNotXma_ReturnsNull()
    {
        // Arrange - RIFF WAVE with PCM format (not XMA)
        var data = CreateXmaHeader(false, 0x0001); // PCM

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Size Tests

    [Fact]
    public void ParseHeader_ReturnsCorrectFileSize()
    {
        // Arrange - RIFF size of 5000 means total file size of 5008 (size + 8)
        var data = CreateXmaHeader(riffSize: 5000);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(5008, result.EstimatedSize);
    }

    [Fact]
    public void ParseHeader_TooSmallSize_ReturnsNull()
    {
        // Arrange - size < 44 is invalid
        var data = CreateXmaHeader(riffSize: 20);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_TooLargeSize_ReturnsNull()
    {
        // Arrange - size > 100MB is invalid
        var data = CreateXmaHeader(riffSize: 150 * 1024 * 1024);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Metadata Tests

    [Fact]
    public void ParseHeader_SetsIsXmaMetadata()
    {
        // Arrange
        var data = CreateXmaHeader(true);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.True((bool)result.Metadata["isXma"]);
    }

    [Fact]
    public void ParseHeader_WithFmtChunk_IncludesFormatTag()
    {
        // Arrange
        var data = CreateXmaHeader(false, 0x0165);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal((ushort)0x0165, result.Metadata["formatTag"]);
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateXmaHeader(
        bool useXma2Chunk = true,
        ushort formatTag = 0x0165,
        uint riffSize = 10000)
    {
        var data = new byte[256];

        // RIFF header
        "RIFF"u8.CopyTo(data.AsSpan(0));
        WriteUInt32LE(data, 4, riffSize);
        "WAVE"u8.CopyTo(data.AsSpan(8));

        var chunkOffset = 12;

        if (useXma2Chunk)
        {
            // XMA2 chunk
            "XMA2"u8.CopyTo(data.AsSpan(chunkOffset));
            WriteUInt32LE(data, chunkOffset + 4, 32); // chunk size
            chunkOffset += 40;
        }
        else
        {
            // fmt chunk with XMA format tag
            "fmt "u8.CopyTo(data.AsSpan(chunkOffset));
            WriteUInt32LE(data, chunkOffset + 4, 16); // chunk size
            WriteUInt16LE(data, chunkOffset + 8, formatTag);
            chunkOffset += 24;
        }

        // data chunk
        "data"u8.CopyTo(data.AsSpan(chunkOffset));
        WriteUInt32LE(data, chunkOffset + 4, riffSize - (uint)chunkOffset);

        return data;
    }

    private static void WriteUInt32LE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteUInt16LE(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    #endregion
}