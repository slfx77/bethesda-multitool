using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Xdbf;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Parsers;

/// <summary>
///     Tests for XdbfFormat.
/// </summary>
public class XdbfFormatTests
{
    private readonly XdbfFormat _parser = new();

    [Fact]
    public void Parse_ValidHeader_ReturnsResult()
    {
        // Arrange
        var data = CreateXdbfHeader(64 * 1024);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("XDBF", result.Format);
        Assert.Equal(16u, result.Metadata["entryCount"]);
    }

    [Fact]
    public void Parse_InvalidMagic_ReturnsNull()
    {
        // Arrange
        var data = new byte[1024];
        "XXXX"u8.CopyTo(data.AsSpan(0));

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Parse_ExcessiveEntryCount_ReturnsNull()
    {
        // Arrange
        var data = CreateXdbfHeader(1024, 20_000);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Parse_WideSpanNoBoundary_UsesFullDefaultSizeWithFallbackMetadata()
    {
        // Arrange - entry table implies >64KB; pre-fix the 64KB parse window collapsed the
        // default-size path to the window edge. With a 1MB span the 512KB default holds.
        var data = CreateXdbfHeader(1024 * 1024, entryTableOffset: 0x18000);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(512 * 1024, result.EstimatedSize);
        Assert.True(result.EstimatedSize > 64 * 1024);
        Assert.True(Assert.IsType<bool>(result.Metadata["boundaryFallback"]));
        Assert.Equal("no boundary signature found; default size used",
            result.Metadata["boundaryFallbackReason"]);
    }

    [Fact]
    public void Parse_BoundarySignatureBeyondOld64KWindow_FindsRealBoundary()
    {
        // Arrange - a known signature ("3XDO") past the old 64KB window is now reachable
        const int boundary = 200_000;
        var data = CreateXdbfHeader(1024 * 1024, entryTableOffset: 0x18000);
        "3XDO"u8.CopyTo(data.AsSpan(boundary));

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(boundary, result.EstimatedSize);
        Assert.False(result.Metadata.ContainsKey("boundaryFallback"));
    }

    private static byte[] CreateXdbfHeader(
        int bufferSize,
        uint entryCount = 16,
        uint entryTableOffset = 0x200,
        uint freeCount = 1)
    {
        var data = new byte[bufferSize];
        "XDBF"u8.CopyTo(data.AsSpan(0));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 0x10000); // version
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), entryCount);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), entryTableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), freeCount);
        return data;
    }
}
