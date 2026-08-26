using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats;
using BethesdaMultitool.Core.Formats.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.FaceGen;

/// <summary>
///     Tests for the FaceGen carver formats (EGM/EGT/TRI). Size math is pinned against the
///     existing rendering-side parsers (EgmParser, EgtParser, TriParser): the computed size
///     must be exactly the byte count those parsers need to parse successfully.
/// </summary>
public class FaceGenFormatTests
{
    #region EGM

    [Fact]
    public void EgmParse_ValidHeader_ReturnsExactSize()
    {
        // Arrange - vc=5, sym=2, asym=1: 64 + 3 * (4 + 5*6) = 166; buffer padded with garbage
        var data = CreateEgmFixture(5, 2, 1, out var expectedSize);

        // Act
        var result = new EgmFormat().Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("EGM", result.Format);
        Assert.Equal(expectedSize, result.EstimatedSize);
        Assert.Equal(5, result.Metadata["vertexCount"]);
        Assert.Equal(2, result.Metadata["symMorphCount"]);
        Assert.Equal(1, result.Metadata["asymMorphCount"]);
    }

    [Fact]
    public void EgmParse_SizePinnedAgainstEgmParser()
    {
        // Arrange
        var data = CreateEgmFixture(7, 3, 2, out _);
        var result = new EgmFormat().Parse(data);
        Assert.NotNull(result);

        // Assert - exactly EstimatedSize bytes parse; one byte fewer does not
        Assert.NotNull(EgmParser.Parse(data.AsSpan(0, result.EstimatedSize).ToArray()));
        Assert.Null(EgmParser.Parse(data.AsSpan(0, result.EstimatedSize - 1).ToArray()));
    }

    [Fact]
    public void EgmParse_WrongMagic_ReturnsNull()
    {
        var data = CreateEgmFixture(5, 1, 0, out _);
        "FREGT003"u8.CopyTo(data.AsSpan(0)); // EGT magic on an EGM parse

        Assert.Null(new EgmFormat().Parse(data));
    }

    [Theory]
    [InlineData(0u)] // zero vertices
    [InlineData(500_001u)] // above sanity gate
    public void EgmParse_InsaneVertexCount_ReturnsNull(uint vertexCount)
    {
        var data = new byte[128];
        "FREGM002"u8.CopyTo(data.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), vertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);

        Assert.Null(new EgmFormat().Parse(data));
    }

    [Fact]
    public void EgmParse_MorphCountAboveGate_ReturnsNull()
    {
        var data = new byte[128];
        "FREGM002"u8.CopyTo(data.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 201);

        Assert.Null(new EgmFormat().Parse(data));
    }

    #endregion

    #region EGT

    [Fact]
    public void EgtParse_UnalignedCols_UsesAlign8RowStride()
    {
        // Arrange - cols=3 aligns to 8: 64 + 1 * (4 + 3*8*2) = 116 (matches the
        // FaceGenTextureMorpherTests fixture layout)
        var data = CreateEgtFixture(rows: 2, cols: 3, symCount: 1, asymCount: 0, out var expectedSize);
        Assert.Equal(116, expectedSize);

        // Act
        var result = new EgtFormat().Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("EGT", result.Format);
        Assert.Equal(expectedSize, result.EstimatedSize);
        Assert.Equal(2, result.Metadata["rows"]);
        Assert.Equal(3, result.Metadata["cols"]);
    }

    [Fact]
    public void EgtParse_AlignedCols_ReturnsExactSize()
    {
        // Arrange - cols=16 is already 8-aligned: 64 + 2 * (4 + 3*16*4)
        var data = CreateEgtFixture(rows: 4, cols: 16, symCount: 2, asymCount: 0, out var expectedSize);
        Assert.Equal(64 + 2 * (4 + 3 * 16 * 4), expectedSize);

        // Act
        var result = new EgtFormat().Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedSize, result.EstimatedSize);
    }

    [Fact]
    public void EgtParse_SizePinnedAgainstEgtParser()
    {
        // Arrange - unaligned cols to exercise the align8 stride in both implementations
        var data = CreateEgtFixture(rows: 3, cols: 5, symCount: 2, asymCount: 1, out _);
        var result = new EgtFormat().Parse(data);
        Assert.NotNull(result);

        // Assert - exactly EstimatedSize bytes parse; one byte fewer does not
        Assert.NotNull(EgtParser.Parse(data.AsSpan(0, result.EstimatedSize).ToArray()));
        Assert.Null(EgtParser.Parse(data.AsSpan(0, result.EstimatedSize - 1).ToArray()));
    }

    [Fact]
    public void EgtParse_WrongMagic_ReturnsNull()
    {
        var data = CreateEgtFixture(2, 3, 1, 0, out _);
        "FREGM002"u8.CopyTo(data.AsSpan(0)); // EGM magic on an EGT parse

        Assert.Null(new EgtFormat().Parse(data));
    }

    [Fact]
    public void EgtParse_ZeroDimensions_ReturnsNull()
    {
        var data = new byte[128];
        "FREGT003"u8.CopyTo(data.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0); // rows = 0
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 8);

        Assert.Null(new EgtFormat().Parse(data));
    }

    #endregion

    #region TRI

    [Fact]
    public void TriParse_ReturnsHeaderDerivedFloorWithFallbackMetadata()
    {
        // Arrange - vc=10, block1=4: floor = 64 + (10 + 4) * 12 = 232
        var data = CreateTriFixture(10, 4, out var floorSize);
        Assert.Equal(232, floorSize);

        // Act
        var result = new TriFormat().Parse(data);

        // Assert - floor size plus the WS4 boundary-fallback flag (tail is variable)
        Assert.NotNull(result);
        Assert.Equal("TRI", result.Format);
        Assert.Equal(floorSize, result.EstimatedSize);
        Assert.True(Assert.IsType<bool>(result.Metadata["boundaryFallback"]));
        Assert.Equal("TRI tail is variable; size is the header-derived floor",
            result.Metadata["boundaryFallbackReason"]);
    }

    [Fact]
    public void TriParse_FloorPinnedAgainstTriParser()
    {
        // Arrange
        var data = CreateTriFixture(10, 4, out _);
        var result = new TriFormat().Parse(data);
        Assert.NotNull(result);

        // Assert - the floor is exactly what TriParser needs for its two Vector3 blocks
        Assert.NotNull(TriParser.Parse(data.AsSpan(0, result.EstimatedSize).ToArray()));
        Assert.Null(TriParser.Parse(data.AsSpan(0, result.EstimatedSize - 1).ToArray()));
    }

    [Fact]
    public void TriParse_WrongMagic_ReturnsNull()
    {
        var data = CreateTriFixture(10, 4, out _);
        "FREGM002"u8.CopyTo(data.AsSpan(0));

        Assert.Null(new TriFormat().Parse(data));
    }

    [Fact]
    public void TriParse_InsaneVertexCount_ReturnsNull()
    {
        var data = new byte[128];
        "FRTRI003"u8.CopyTo(data.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 500_001);

        Assert.Null(new TriFormat().Parse(data));
    }

    #endregion

    #region Registry

    [Theory]
    [InlineData("egm", ".egm")]
    [InlineData("egt", ".egt")]
    [InlineData("tri", ".tri")]
    public void FormatRegistry_ContainsFaceGenFormats_WithScanningEnabled(string formatId, string extension)
    {
        var format = FormatRegistry.GetByFormatId(formatId);

        Assert.NotNull(format);
        Assert.True(format!.EnableSignatureScanning);
        Assert.Equal(extension, format.Extension);
        Assert.Equal("facegen", format.OutputFolder);
        Assert.Equal(FileCategory.Model, format.Category);
        Assert.Single(format.Signatures);
    }

    [Theory]
    [InlineData("egm", "FREGM002")]
    [InlineData("egt", "FREGT003")]
    [InlineData("tri", "FRTRI003")]
    public void FormatRegistry_FaceGenSignatures_UseEightByteMagics(string signatureId, string magic)
    {
        var format = FormatRegistry.GetBySignatureId(signatureId);

        Assert.NotNull(format);
        var signature = Assert.Single(format!.Signatures);
        Assert.Equal(magic, System.Text.Encoding.ASCII.GetString(signature.MagicBytes));
    }

    #endregion

    #region Fixtures

    private static byte[] CreateEgmFixture(uint vertexCount, uint symCount, uint asymCount, out int exactSize)
    {
        exactSize = (int)(64 + (symCount + asymCount) * (4 + vertexCount * 6));
        var data = new byte[exactSize + 512]; // trailing garbage beyond the true size
        data.AsSpan(exactSize).Fill(0xCD);
        "FREGM002"u8.CopyTo(data.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), vertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), symCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), asymCount);
        return data;
    }

    private static byte[] CreateEgtFixture(uint rows, uint cols, uint symCount, uint asymCount, out int exactSize)
    {
        var alignedCols = (cols + 7) & ~7u;
        exactSize = (int)(64 + (symCount + asymCount) * (4 + 3 * alignedCols * rows));
        var data = new byte[exactSize + 512];
        data.AsSpan(exactSize).Fill(0xCD);
        "FREGT003"u8.CopyTo(data.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), rows);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), cols);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), symCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), asymCount);
        return data;
    }

    private static byte[] CreateTriFixture(uint vertexCount, uint vertexBlock1Count, out int floorSize)
    {
        floorSize = (int)(64 + (vertexCount + vertexBlock1Count) * 12);
        var data = new byte[floorSize + 512];
        data.AsSpan(floorSize).Fill(0xCD);
        "FRTRI003"u8.CopyTo(data.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), vertexCount); // header word 0
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), vertexBlock1Count); // header word 5
        return data;
    }

    #endregion
}
