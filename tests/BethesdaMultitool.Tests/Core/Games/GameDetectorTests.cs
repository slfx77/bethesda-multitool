using System.Text;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Games;

/// <summary>
///     End-to-end detection: structural probe (TES3 magic, Oblivion 20-byte header), HEDR-version
///     coarse guess for the 24-byte family, and — the key consolidation win — master/filename
///     refinement that overrides the ambiguous version float (Skyrim 0.94 ≈ FO3 0.94).
/// </summary>
public class GameDetectorTests
{
    [Fact]
    public void DetectFromBytes_Tes3Magic_ReturnsMorrowind()
    {
        Assert.Equal(BethesdaGame.Morrowind, GameDetector.DetectFromBytes(BuildTes3Header()).Game);
    }

    [Fact]
    public void DetectFromBytes_OblivionTwentyByteHeader_ReturnsOblivion()
    {
        Assert.Equal(BethesdaGame.Oblivion, GameDetector.DetectFromBytes(BuildOblivionHeader()).Game);
    }

    [Fact]
    public void DetectFromBytes_Fallout76Version_ReturnsFallout76()
    {
        Assert.Equal(BethesdaGame.Fallout76, GameDetector.DetectFromBytes(BuildTes4Header24(263.0f)).Game);
    }

    [Fact]
    public void DetectFromBytes_NewVegasVersion_ReturnsNewVegas()
    {
        Assert.Equal(BethesdaGame.FalloutNewVegas, GameDetector.DetectFromBytes(BuildTes4Header24(1.34f)).Game);
    }

    [Fact]
    public void DetectFromFile_FilenameRefinesOverAmbiguousVersion()
    {
        // HEDR version 0.94 alone resolves to Fallout 3; the filename "Skyrim.esm" must win.
        var dir = Path.Combine(Path.GetTempPath(), $"gamedetect_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Skyrim.esm");
        File.WriteAllBytes(path, BuildTes4Header24(0.94f));
        try
        {
            Assert.Equal(BethesdaGame.Skyrim, GameDetector.DetectFromFile(path).Game);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DetectFromFile_NoNameHint_FallsBackToVersion()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gamedetect_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "plugin.esm"); // no game name in path or masters
        File.WriteAllBytes(path, BuildTes4Header24(263.0f));
        try
        {
            Assert.Equal(BethesdaGame.Fallout76, GameDetector.DetectFromFile(path).Game);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DetectFromFile_MissingPath_ReturnsUnknown()
    {
        Assert.Equal(BethesdaGame.Unknown, GameDetector.DetectFromFile("does-not-exist.esm").Game);
    }

    private static byte[] BuildTes3Header()
    {
        var data = new byte[16];
        "TES3"u8.CopyTo(data);
        return data;
    }

    /// <summary>Minimal little-endian TES4 24-byte header with a HEDR subrecord carrying <paramref name="version" />.</summary>
    private static byte[] BuildTes4Header24(float version)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        bw.Write("TES4"u8.ToArray()); // 0
        bw.Write(0u); // 4  data size
        bw.Write(0u); // 8  flags
        bw.Write(0u); // 12 form ID
        bw.Write(0u); // 16 VCS info
        bw.Write((ushort)0); // 20 form version
        bw.Write((ushort)0); // 22 unknown -> record header ends at 24
        bw.Write("HEDR"u8.ToArray()); // 24 first subrecord signature
        bw.Write((ushort)12); // 28 HEDR data length
        bw.Write(version); // 30 version float
        bw.Write(0u); // 34 record count
        bw.Write(0u); // 38 next object ID
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Oblivion's 20-byte record header (no form-version trailer) with HEDR at offset 20.</summary>
    private static byte[] BuildOblivionHeader()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        bw.Write("TES4"u8.ToArray()); // 0
        bw.Write(0u); // 4  data size
        bw.Write(0u); // 8  flags
        bw.Write(0u); // 12 form ID
        bw.Write(0u); // 16 VCS info -> 20-byte header ends at 20
        bw.Write("HEDR"u8.ToArray()); // 20 first subrecord signature
        bw.Write((ushort)12); // 24 HEDR data length
        bw.Write(0.8f); // 26 version float (Oblivion ~0.8)
        bw.Write(0u); // 30 record count
        bw.Write(0u); // 34 next object ID
        bw.Flush();
        return ms.ToArray();
    }
}