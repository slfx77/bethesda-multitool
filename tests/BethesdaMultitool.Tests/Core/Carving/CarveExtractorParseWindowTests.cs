using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Carving;
using BethesdaMultitool.Core.Formats.Png;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Carving;

/// <summary>
///     End-to-end PrepareExtraction tests for the per-format parse window:
///     a PNG whose IEND sits past the old 64KB window must now carve at its true size.
/// </summary>
public sealed class CarveExtractorParseWindowTests : IDisposable
{
    private readonly string _testDir;

    public CarveExtractorParseWindowTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"CarveExtractorParseWindowTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
    }

    [Fact]
    public void PrepareExtraction_PngWithIendAt200K_CarvesTrueSize()
    {
        // Arrange - PNG at offset 1000, IEND at signature + 200KB
        const int pngOffset = 1000;
        const int iendRelative = 200 * 1024;
        var fileBytes = new byte[400 * 1024];
        WritePngHeader(fileBytes, pngOffset);
        fileBytes[pngOffset + iendRelative] = 0x49; // "IEND"
        fileBytes[pngOffset + iendRelative + 1] = 0x45;
        fileBytes[pngOffset + iendRelative + 2] = 0x4E;
        fileBytes[pngOffset + iendRelative + 3] = 0x44;

        var fixturePath = Path.Combine(_testDir, "fixture.bin");
        File.WriteAllBytes(fixturePath, fileBytes);

        using var mmf = MemoryMappedFile.CreateFromFile(
            fixturePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileBytes.Length, MemoryMappedFileAccess.Read);

        // Act
        var extraction = CarveExtractor.PrepareExtraction(
            accessor, fileBytes.Length, pngOffset, "png", new PngFormat(), _testDir);

        // Assert - the old 64KB window capped this carve; the true size is IEND + 8
        Assert.NotNull(extraction);
        Assert.Equal(iendRelative + 8, extraction.Value.FileSize);
        Assert.Equal(iendRelative + 8, extraction.Value.Data.Length);
        Assert.NotNull(extraction.Value.Metadata);
        Assert.False(extraction.Value.Metadata!.ContainsKey("boundaryFallback"));
    }

    private static void WritePngHeader(byte[] buffer, int offset)
    {
        // PNG signature
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(buffer, offset);

        // IHDR chunk: length 13, "IHDR", width 4, height 4
        new byte[] { 0x00, 0x00, 0x00, 0x0D }.CopyTo(buffer, offset + 8);
        "IHDR"u8.ToArray().CopyTo(buffer, offset + 12);
        new byte[] { 0x00, 0x00, 0x00, 0x04 }.CopyTo(buffer, offset + 16); // width
        new byte[] { 0x00, 0x00, 0x00, 0x04 }.CopyTo(buffer, offset + 20); // height
        buffer[offset + 24] = 0x08; // bit depth
        buffer[offset + 25] = 0x02; // color type
    }
}
