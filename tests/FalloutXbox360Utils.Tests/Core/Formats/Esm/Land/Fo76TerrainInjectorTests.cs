using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Land;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Land;

/// <summary>
///     Tests for Fallout 76 detection (HEDR version threshold) and the BTD terrain injector's gating.
///     The full inject → render path is verified end-to-end against the real SeventySix.esm +
///     Appalachia.btd (the worldspace heightmap matches the direct BTD render); these lock the
///     detection threshold and the no-op guards that protect every other game.
/// </summary>
public class Fo76TerrainInjectorTests
{
    [Fact]
    public void Detect_Fallout76HedrVersion_ReturnsFallout76()
    {
        // SeventySix.esm's real HEDR version is 263.0 — far above every other TES4 game (max 1.7).
        var header = BuildTes4Header(263.0f);
        Assert.Equal(BethesdaGame.Fallout76, PluginFormat.Detect(header).Game);
    }

    [Theory]
    [InlineData(1.34f, BethesdaGame.FalloutNewVegas)] // FNV must NOT be swept into FO76
    [InlineData(1.32f, BethesdaGame.FalloutNewVegas)]
    public void Detect_BelowThreshold_StaysPreviousGame(float version, BethesdaGame expected)
    {
        var header = BuildTes4Header(version);
        Assert.Equal(expected, PluginFormat.Detect(header).Game);
    }

    [Fact]
    public void Inject_NonFallout76Plugin_ReturnsZero()
    {
        var path = WriteTempEsm(BuildTes4Header(1.34f)); // New Vegas version
        try
        {
            Assert.Equal(0, Fo76TerrainInjector.Inject(new RecordCollection(), path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Inject_Fallout76WithoutTerrainFolder_ReturnsZero()
    {
        // Detected as FO76, but no Data\Terrain\*.btd alongside it -> nothing to inject.
        var dir = Path.Combine(Path.GetTempPath(), $"fo76inj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "SeventySix.esm");
        File.WriteAllBytes(path, BuildTes4Header(263.0f));
        try
        {
            Assert.Equal(0, Fo76TerrainInjector.Inject(new RecordCollection(), path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string WriteTempEsm(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"esm_{Guid.NewGuid():N}.esm");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Minimal little-endian TES4 file header with a HEDR subrecord carrying <paramref name="version" />.</summary>
    private static byte[] BuildTes4Header(float version)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        bw.Write("TES4"u8.ToArray()); // 0
        bw.Write(0u);                 // 4  data size
        bw.Write(0u);                 // 8  flags
        bw.Write(0u);                 // 12 form ID
        bw.Write(0u);                 // 16 VCS info
        bw.Write((ushort)0);          // 20 form version
        bw.Write((ushort)0);          // 22 unknown -> record header ends at 24
        bw.Write("HEDR"u8.ToArray()); // 24 first subrecord signature
        bw.Write((ushort)12);         // 28 HEDR data length
        bw.Write(version);            // 30 version float
        bw.Write(0u);                 // 34 record count
        bw.Write(0u);                 // 38 next object ID
        bw.Flush();
        return ms.ToArray();
    }
}
