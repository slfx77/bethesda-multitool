using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.AssetPacking;

public class Xbox360FolderDetectorTests : IDisposable
{
    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(),
        $"x360-detect-{Guid.NewGuid():N}");

    public Xbox360FolderDetectorTests()
    {
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public void DetectIsXbox360Format_NonExistentFolder_ReturnsFalse()
    {
        var path = Path.Combine(_scratchRoot, "does-not-exist");
        Assert.False(Xbox360FolderDetector.DetectIsXbox360Format(path));
    }

    [Fact]
    public void DetectIsXbox360Format_EmptyFolder_ReturnsFalse()
    {
        Assert.False(Xbox360FolderDetector.DetectIsXbox360Format(_scratchRoot));
    }

    [Fact]
    public void DetectIsXbox360Format_BigEndianEsm_ReturnsTrue()
    {
        // Xbox 360 ESM has bytes "4SET" at offset 0 (reversed "TES4")
        File.WriteAllBytes(
            Path.Combine(_scratchRoot, "Sample.esm"),
            [(byte)'4', (byte)'S', (byte)'E', (byte)'T', 0, 0, 0, 0]);

        Assert.True(Xbox360FolderDetector.DetectIsXbox360Format(_scratchRoot));
    }

    [Fact]
    public void DetectIsXbox360Format_LittleEndianEsm_ReturnsFalse()
    {
        // PC ESM has bytes "TES4" at offset 0
        File.WriteAllBytes(
            Path.Combine(_scratchRoot, "Sample.esm"),
            [(byte)'T', (byte)'E', (byte)'S', (byte)'4', 0, 0, 0, 0]);

        Assert.False(Xbox360FolderDetector.DetectIsXbox360Format(_scratchRoot));
    }

    [Fact]
    public void DetectIsXbox360Format_BigEndianEsp_ReturnsTrue()
    {
        // Also covers .esp extension
        File.WriteAllBytes(
            Path.Combine(_scratchRoot, "Sample.esp"),
            [(byte)'4', (byte)'S', (byte)'E', (byte)'T', 0, 0, 0, 0]);

        Assert.True(Xbox360FolderDetector.DetectIsXbox360Format(_scratchRoot));
    }

    [Fact]
    public void DetectIsXbox360Format_PrefersEsmOverBsa_WhenBothPresent()
    {
        // When the folder has both a PC ESM and (anything that looks like) a BSA, the
        // ESM check fires first and short-circuits. Verify by giving a PC ESM and a
        // bogus BSA that would throw on parse — should still return false.
        File.WriteAllBytes(
            Path.Combine(_scratchRoot, "PC.esm"),
            [(byte)'T', (byte)'E', (byte)'S', (byte)'4', 0, 0, 0, 0]);
        File.WriteAllBytes(
            Path.Combine(_scratchRoot, "garbage.bsa"),
            [0, 0, 0, 0, 0, 0, 0, 0]);

        Assert.False(Xbox360FolderDetector.DetectIsXbox360Format(_scratchRoot));
    }
}
