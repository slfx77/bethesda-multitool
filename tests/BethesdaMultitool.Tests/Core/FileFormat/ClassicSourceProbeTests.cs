using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.FileFormat;

/// <summary>
///     Pins the classic-source arm of file-type detection: a file claims
///     <see cref="AnalysisFileType.ClassicGameData" /> only when it sits inside a detected classic
///     install AND is one of that game's declared artifacts — stray files inside an install
///     (manuals, DOSBox binaries) must stay Unknown, and the wildcard matcher must cover the exact
///     glob shapes the profiles declare.
/// </summary>
public class ClassicSourceProbeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("classic-probe-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }

        GC.SuppressFinalize(this);
    }

    private string MakeInstall(string name, params string[] relativeFiles)
    {
        var install = Path.Combine(_root, name);
        foreach (var relative in relativeFiles)
        {
            var path = Path.Combine(install, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // ≥4 magic-less bytes: FileTypeDetector short-circuits sub-4-byte files to Unknown
            // before any probe runs, and real classic artifacts are never that small.
            File.WriteAllBytes(path, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        }

        return install;
    }

    [Theory]
    [InlineData("patch*.dat", @"patch000.dat", true)]
    [InlineData("patch*.dat", @"patch.dat", true)]
    [InlineData("patch*.dat", @"master.dat", false)]
    [InlineData(@"ARENA2\*.BSA", @"ARENA2\ARCH3D.BSA", true)]
    [InlineData(@"ARENA2\*.BSA", @"arena2\maps.bsa", true)] // case-insensitive
    [InlineData(@"ARENA2\*.BSA", @"ARENA2\TEXTURE.001", false)]
    [InlineData(@"core\*.bos", @"core\tiles_0.bos", true)]
    [InlineData("GLOBAL.BSA", "GLOBAL.BSA", true)]
    [InlineData("GLOBAL.BSA", "GLOBAL.BSA.bak", false)]
    public void GlobMatches_CoversProfileGlobShapes(string glob, string path, bool expected)
    {
        Assert.Equal(expected, ClassicSourceProbe.GlobMatches(glob, path));
    }

    [Fact]
    public void Detect_ClassicArchiveInsideInstall_IsClassicGameData()
    {
        var fo1 = MakeInstall("fo1", "MASTER.DAT", "CRITTER.DAT", "FALLOUTW.EXE");
        Assert.Equal(
            AnalysisFileType.ClassicGameData,
            FileTypeDetector.Detect(Path.Combine(fo1, "MASTER.DAT")));
    }

    [Fact]
    public void Detect_StrayFileInsideInstall_StaysUnknown()
    {
        var fo1 = MakeInstall("fo1-stray", "MASTER.DAT", "CRITTER.DAT", "FALLOUTW.EXE", "Manual.pdf");
        Assert.Equal(AnalysisFileType.Unknown, FileTypeDetector.Detect(Path.Combine(fo1, "Manual.pdf")));
    }

    [Fact]
    public void Detect_ClassicArchiveOutsideAnyInstall_StaysUnknown()
    {
        var loose = MakeInstall("loose", "MASTER.DAT");
        Assert.Equal(AnalysisFileType.Unknown, FileTypeDetector.Detect(Path.Combine(loose, "MASTER.DAT")));
    }

    [Fact]
    public void TryDetect_GlobArtifactInSubdirectory_ResolvesProfileAndRoot()
    {
        var dagger = MakeInstall("dagger", @"ARENA2\ARCH3D.BSA", @"ARENA2\MAPS.BSA");
        var hit = ClassicSourceProbe.TryDetect(Path.Combine(dagger, @"ARENA2\MAPS.BSA"));

        Assert.Equal(BethesdaGame.Daggerfall, hit?.Profile.Game);
        Assert.Equal(dagger, hit?.Root);
    }

    [Fact]
    public void Detect_MagicBearingFileInsideInstall_KeepsItsMagicType()
    {
        // The classic probe is a LAST resort: a real plugin dropped inside a classic install must
        // still detect as EsmFile from its magic, never be reclassified by location.
        var fo1 = MakeInstall("fo1-esm", "MASTER.DAT", "CRITTER.DAT", "FALLOUTW.EXE");
        var esmPath = Path.Combine(fo1, "MASTER.DAT");
        File.WriteAllBytes(esmPath, "TES4"u8.ToArray());

        Assert.Equal(AnalysisFileType.EsmFile, FileTypeDetector.Detect(esmPath));
    }
}
