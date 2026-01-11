using System.Globalization;
using System.Text.Json;
using Xbox360MemoryCarver.Core.Carving;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Carving;

/// <summary>
///     Tests for CarveEntry and CarveManifest.
/// </summary>
public sealed class CarveManifestTests : IDisposable
{
    private readonly string _testDir;

    public CarveManifestTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"CarveManifestTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
    }

    #region CarveEntry Tests

    [Fact]
    public void CarveEntry_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var entry = new CarveEntry();

        // Assert
        Assert.Equal("", entry.FileType);
        Assert.Equal(0, entry.Offset);
        Assert.Equal(0, entry.SizeInDump);
        Assert.Equal(0, entry.SizeOutput);
        Assert.Equal("", entry.Filename);
        Assert.Null(entry.OriginalPath);
        Assert.False(entry.IsCompressed);
        Assert.Null(entry.ContentType);
        Assert.False(entry.IsPartial);
        Assert.Null(entry.Notes);
        Assert.Null(entry.Metadata);
    }

    [Fact]
    public void CarveEntry_AllProperties_CanBeSet()
    {
        // Arrange & Act
        var entry = new CarveEntry
        {
            FileType = "ddx",
            Offset = 12345,
            SizeInDump = 1024,
            SizeOutput = 2048,
            Filename = "texture_001.dds",
            OriginalPath = "textures/architecture/wall.ddx",
            IsCompressed = true,
            ContentType = "DXT1",
            IsPartial = false,
            Notes = "Test note",
            Metadata = new Dictionary<string, object> { ["width"] = 512, ["height"] = 512 }
        };

        // Assert
        Assert.Equal("ddx", entry.FileType);
        Assert.Equal(12345, entry.Offset);
        Assert.Equal(1024, entry.SizeInDump);
        Assert.Equal(2048, entry.SizeOutput);
        Assert.Equal("texture_001.dds", entry.Filename);
        Assert.Equal("textures/architecture/wall.ddx", entry.OriginalPath);
        Assert.True(entry.IsCompressed);
        Assert.Equal("DXT1", entry.ContentType);
        Assert.False(entry.IsPartial);
        Assert.Equal("Test note", entry.Notes);
        Assert.NotNull(entry.Metadata);
        Assert.Equal(512, Convert.ToInt32(entry.Metadata["width"], CultureInfo.InvariantCulture));
    }

    #endregion

    #region SaveAsync Tests

    [Fact]
    public async Task SaveAsync_CreatesManifestFile()
    {
        // Arrange
        var entries = new List<CarveEntry>
        {
            new() { FileType = "dds", Offset = 0, SizeInDump = 100, Filename = "test.dds" }
        };

        // Act
        await CarveManifest.SaveAsync(_testDir, entries);

        // Assert
        var manifestPath = Path.Combine(_testDir, "manifest.json");
        Assert.True(File.Exists(manifestPath));
    }

    [Fact]
    public async Task SaveAsync_WritesValidJson()
    {
        // Arrange
        var entries = new List<CarveEntry>
        {
            new() { FileType = "dds", Offset = 100, SizeInDump = 200, Filename = "test.dds" }
        };

        // Act
        await CarveManifest.SaveAsync(_testDir, entries);

        // Assert
        var manifestPath = Path.Combine(_testDir, "manifest.json");
        var json = await File.ReadAllTextAsync(manifestPath);
        var parsed = JsonSerializer.Deserialize<List<CarveEntry>>(json);
        Assert.NotNull(parsed);
        Assert.Single(parsed);
    }

    [Fact]
    public async Task SaveAsync_EmptyList_CreatesEmptyArrayJson()
    {
        // Arrange
        List<CarveEntry> entries = [];

        // Act
        await CarveManifest.SaveAsync(_testDir, entries);

        // Assert
        var manifestPath = Path.Combine(_testDir, "manifest.json");
        var json = await File.ReadAllTextAsync(manifestPath);
        Assert.Equal("[]", json.Trim());
    }

    [Fact]
    public async Task SaveAsync_MultipleEntries_SavesAll()
    {
        // Arrange
        var entries = new List<CarveEntry>
        {
            new() { FileType = "dds", Offset = 0, Filename = "tex1.dds" },
            new() { FileType = "png", Offset = 1000, Filename = "img.png" },
            new() { FileType = "nif", Offset = 2000, Filename = "model.nif" }
        };

        // Act
        await CarveManifest.SaveAsync(_testDir, entries);

        // Assert
        var manifestPath = Path.Combine(_testDir, "manifest.json");
        var json = await File.ReadAllTextAsync(manifestPath);
        var parsed = JsonSerializer.Deserialize<List<CarveEntry>>(json);
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed.Count);
    }

    #endregion

    #region LoadAsync Tests

    [Fact]
    public async Task LoadAsync_ExistingManifest_ReturnsEntries()
    {
        // Arrange
        var entries = new List<CarveEntry>
        {
            new() { FileType = "xma", Offset = 500, SizeInDump = 1024, Filename = "audio.xma" }
        };
        await CarveManifest.SaveAsync(_testDir, entries);
        var manifestPath = Path.Combine(_testDir, "manifest.json");

        // Act
        var loaded = await CarveManifest.LoadAsync(manifestPath);

        // Assert
        Assert.Single(loaded);
        Assert.Equal("xma", loaded[0].FileType);
        Assert.Equal(500, loaded[0].Offset);
        Assert.Equal(1024, loaded[0].SizeInDump);
        Assert.Equal("audio.xma", loaded[0].Filename);
    }

    [Fact]
    public async Task LoadAsync_EmptyArrayJson_ReturnsEmptyList()
    {
        // Arrange
        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "[]");

        // Act
        var loaded = await CarveManifest.LoadAsync(manifestPath);

        // Assert
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task LoadAsync_NonexistentFile_ThrowsException()
    {
        // Arrange
        var manifestPath = Path.Combine(_testDir, "nonexistent.json");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => CarveManifest.LoadAsync(manifestPath));
    }

    #endregion

    #region Round-trip Tests

    [Fact]
    public async Task SaveAndLoad_PreservesAllProperties()
    {
        // Arrange
        var entries = new List<CarveEntry>
        {
            new()
            {
                FileType = "ddx",
                Offset = 12345,
                SizeInDump = 1024,
                SizeOutput = 2048,
                Filename = "texture_001.dds",
                OriginalPath = "textures/architecture/wall.ddx",
                IsCompressed = true,
                ContentType = "DXT1",
                IsPartial = true,
                Notes = "Test note with special chars: <>&\"'",
                Metadata = new Dictionary<string, object>
                {
                    ["width"] = 512,
                    ["height"] = 256,
                    ["format"] = "DXT1"
                }
            }
        };

        // Act
        await CarveManifest.SaveAsync(_testDir, entries);
        var manifestPath = Path.Combine(_testDir, "manifest.json");
        var loaded = await CarveManifest.LoadAsync(manifestPath);

        // Assert
        Assert.Single(loaded);
        var entry = loaded[0];
        Assert.Equal("ddx", entry.FileType);
        Assert.Equal(12345, entry.Offset);
        Assert.Equal(1024, entry.SizeInDump);
        Assert.Equal(2048, entry.SizeOutput);
        Assert.Equal("texture_001.dds", entry.Filename);
        Assert.Equal("textures/architecture/wall.ddx", entry.OriginalPath);
        Assert.True(entry.IsCompressed);
        Assert.Equal("DXT1", entry.ContentType);
        Assert.True(entry.IsPartial);
        Assert.Equal("Test note with special chars: <>&\"'", entry.Notes);
        Assert.NotNull(entry.Metadata);
    }

    [Fact]
    public async Task SaveAndLoad_LargeOffsets_PreservesCorrectly()
    {
        // Arrange - Test with offsets > 2GB
        var entries = new List<CarveEntry>
        {
            new() { FileType = "dds", Offset = 0x100000000L, Filename = "large_offset.dds" } // 4GB offset
        };

        // Act
        await CarveManifest.SaveAsync(_testDir, entries);
        var manifestPath = Path.Combine(_testDir, "manifest.json");
        var loaded = await CarveManifest.LoadAsync(manifestPath);

        // Assert
        Assert.Single(loaded);
        Assert.Equal(0x100000000L, loaded[0].Offset);
    }

    [Fact]
    public async Task SaveAndLoad_NullableFieldsAsNull_PreservesCorrectly()
    {
        // Arrange
        var entries = new List<CarveEntry>
        {
            new()
            {
                FileType = "nif",
                Offset = 0,
                Filename = "model.nif",
                OriginalPath = null,
                ContentType = null,
                Notes = null,
                Metadata = null
            }
        };

        // Act
        await CarveManifest.SaveAsync(_testDir, entries);
        var manifestPath = Path.Combine(_testDir, "manifest.json");
        var loaded = await CarveManifest.LoadAsync(manifestPath);

        // Assert
        Assert.Single(loaded);
        Assert.Null(loaded[0].OriginalPath);
        Assert.Null(loaded[0].ContentType);
        Assert.Null(loaded[0].Notes);
        Assert.Null(loaded[0].Metadata);
    }

    #endregion
}