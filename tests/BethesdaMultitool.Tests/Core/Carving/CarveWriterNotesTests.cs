using BethesdaMultitool.Core.Carving;
using BethesdaMultitool.Core.Formats;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Carving;

/// <summary>
///     Tests that boundary-fallback metadata surfaces as a manifest note on both the
///     direct-write and converted-file paths of CarveWriter.
/// </summary>
public sealed class CarveWriterNotesTests : IDisposable
{
    private readonly string _testDir;

    public CarveWriterNotesTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"CarveWriterNotesTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
    }

    [Fact]
    public async Task WriteFileAsync_ManifestFilenameAlwaysNamesAFileThatExists()
    {
        // The manifest used to record the REQUESTED path while the writer's collision retry wrote a
        // GUID-suffixed one, so entries named files that were not on disk. Hold the intended path
        // open to force that retry and assert the manifest follows the bytes.
        CarveEntry? captured = null;
        var writer = new CarveWriter(new Dictionary<string, IFileConverter>(), false, false, e => captured = e);
        var outputFile = PrepareOutputFile("images", "collide.png");

        await using (new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await writer.WriteFileAsync(
                new WriteFileParams(outputFile, [1, 2, 3], 0x100, "png", 3, null, null));
        }

        Assert.NotNull(captured);
        var manifestPath = Path.Combine(Path.GetDirectoryName(outputFile)!, captured!.Filename);
        Assert.True(
            File.Exists(manifestPath),
            $"Manifest names '{captured.Filename}' but no such file exists in the output folder.");
        Assert.NotEqual(Path.GetFileName(outputFile), captured.Filename);
    }

    [Fact]
    public async Task WriteFileAsync_DirectPath_BoundaryFallbackMetadata_AppendsNote()
    {
        // Arrange
        CarveEntry? captured = null;
        var writer = new CarveWriter(new Dictionary<string, IFileConverter>(), false, false, e => captured = e);
        var outputFile = PrepareOutputFile("images", "test.png");
        var metadata = new Dictionary<string, object>
        {
            ["boundaryFallback"] = true,
            ["boundaryFallbackReason"] = "IEND not found in parse window"
        };

        // Act
        await writer.WriteFileAsync(new WriteFileParams(outputFile, [1, 2, 3], 0x100, "png", 3, null, metadata));

        // Assert
        Assert.NotNull(captured);
        Assert.Equal("Size estimated (boundary fallback): IEND not found in parse window", captured!.Notes);
    }

    [Fact]
    public async Task WriteFileAsync_DirectPath_TruncatedAndFallback_CombinesNotes()
    {
        // Arrange
        CarveEntry? captured = null;
        var writer = new CarveWriter(new Dictionary<string, IFileConverter>(), false, false, e => captured = e);
        var outputFile = PrepareOutputFile("images", "partial.png");
        var metadata = new Dictionary<string, object> { ["boundaryFallback"] = true };

        // Act
        await writer.WriteFileAsync(new WriteFileParams(outputFile, [1, 2, 3], 0x200, "png", 3, null, metadata,
            CarveResidency.FromPresentRuns([new CarveHole(0, 8)], 10)));

        // Assert - coverage note keeps its lead position, fallback note is appended
        Assert.NotNull(captured);
        Assert.StartsWith("Memory coverage", captured!.Notes);
        Assert.Contains("Size estimated (boundary fallback)", captured.Notes);
    }

    [Fact]
    public async Task WriteFileAsync_CarriesHolePositionsAndSplitsTailFromInteriorLoss()
    {
        // Coverage alone cannot distinguish a zero-filled hole from a run of legitimate zero bytes,
        // and cannot distinguish a file cut short at its end from one damaged in the middle.
        CarveEntry? captured = null;
        var writer = new CarveWriter(new Dictionary<string, IFileConverter>(), false, false, e => captured = e);
        var outputFile = PrepareOutputFile("images", "holed.png");

        // Present: [0,16) and [32,48). Missing: [16,32) interior and [48,64) tail.
        var residency = CarveResidency.FromPresentRuns([new CarveHole(0, 16), new CarveHole(32, 16)], 64);
        await writer.WriteFileAsync(
            new WriteFileParams(outputFile, [1, 2, 3], 0x600, "png", 64, null, null, residency));

        Assert.NotNull(captured);
        Assert.True(captured!.IsPartial);
        Assert.True(captured.TailTruncated);
        Assert.Equal(0.5, captured.Coverage);
        Assert.Equal(
            [new CarveHole(16, 16), new CarveHole(48, 16)],
            captured.Holes);
        Assert.Contains("interior hole", captured.Notes);
    }

    [Fact]
    public async Task WriteFileAsync_FullyResidentFileRecordsCompleteCoverageAndNoHoles()
    {
        CarveEntry? captured = null;
        var writer = new CarveWriter(new Dictionary<string, IFileConverter>(), false, false, e => captured = e);
        var outputFile = PrepareOutputFile("images", "whole.png");

        await writer.WriteFileAsync(
            new WriteFileParams(outputFile, [1, 2, 3], 0x700, "png", 3, null, null));

        Assert.NotNull(captured);
        Assert.False(captured!.IsPartial);
        Assert.False(captured.TailTruncated);
        Assert.Equal(1.0, captured.Coverage);
        Assert.Null(captured.Holes);
    }

    [Fact]
    public async Task WriteFileAsync_DirectPath_NoFallbackMetadata_NoNote()
    {
        // Arrange
        CarveEntry? captured = null;
        var writer = new CarveWriter(new Dictionary<string, IFileConverter>(), false, false, e => captured = e);
        var outputFile = PrepareOutputFile("images", "clean.png");

        // Act
        await writer.WriteFileAsync(new WriteFileParams(outputFile, [1, 2, 3], 0x300, "png", 3, null,
            new Dictionary<string, object>()));

        // Assert
        Assert.NotNull(captured);
        Assert.Null(captured!.Notes);
    }

    [Fact]
    public async Task WriteFileAsync_ConvertedPath_BoundaryFallbackMetadata_AppendsNote()
    {
        // Arrange - stub converter succeeds, so the converted-file manifest path is taken
        CarveEntry? captured = null;
        var converters = new Dictionary<string, IFileConverter> { ["ddx"] = new StubConverter() };
        var writer = new CarveWriter(converters, true, false, e => captured = e);
        var outputFile = PrepareOutputFile("ddx", "tex.ddx");
        var metadata = new Dictionary<string, object>
        {
            ["boundaryFallback"] = true,
            ["boundaryFallbackReason"] = "no boundary token found; size = header + 0.7*uncompressed"
        };

        // Act
        await writer.WriteFileAsync(new WriteFileParams(outputFile, [1, 2, 3], 0x400, "ddx_3xdo", 3, null, metadata));

        // Assert - the converted entry carries both the converter note and the fallback note
        Assert.NotNull(captured);
        Assert.Equal("converted", captured!.ContentType);
        Assert.Equal(
            "Converted OK; Size estimated (boundary fallback): no boundary token found; size = header + 0.7*uncompressed",
            captured.Notes);
    }

    [Fact]
    public async Task WriteFileAsync_ConvertedPath_NoFallbackMetadata_KeepsConverterNotesOnly()
    {
        // Arrange
        CarveEntry? captured = null;
        var converters = new Dictionary<string, IFileConverter> { ["ddx"] = new StubConverter() };
        var writer = new CarveWriter(converters, true, false, e => captured = e);
        var outputFile = PrepareOutputFile("ddx", "clean.ddx");

        // Act
        await writer.WriteFileAsync(new WriteFileParams(outputFile, [1, 2, 3], 0x500, "ddx_3xdo", 3, null,
            new Dictionary<string, object>()));

        // Assert
        Assert.NotNull(captured);
        Assert.Equal("Converted OK", captured!.Notes);
    }

    private string PrepareOutputFile(string folder, string filename)
    {
        var dir = Path.Combine(_testDir, folder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, filename);
    }

    private sealed class StubConverter : IFileConverter
    {
        public string TargetExtension => ".dds";
        public string TargetFolder => "textures";
        public bool IsInitialized => true;
        public int ConvertedCount => 0;
        public int FailedCount => 0;

        public bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata)
        {
            return true;
        }

        public Task<ConversionResult> ConvertAsync(byte[] data, IReadOnlyDictionary<string, object>? metadata = null)
        {
            return Task.FromResult(new ConversionResult
            {
                Success = true,
                OutputData = [9, 9],
                Notes = "Converted OK"
            });
        }

        public bool Initialize(bool verbose = false, Dictionary<string, object>? options = null)
        {
            return true;
        }
    }
}
