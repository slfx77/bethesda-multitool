using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Games;

/// <summary>
///     Pins classic-install detection over synthetic marker layouts in temp directories: conjunctive
///     marker sets, the <c>|</c> any-of alternatives, the Fallout 1 / Fallout 2 disambiguation (both
///     roots carry the same DAT pair), and the bounded walk-up from a file to its install root.
/// </summary>
public class ClassicGameLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("classic-locator-").FullName;

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
            File.WriteAllBytes(path, [0x00]);
        }

        return install;
    }

    [Fact]
    public void DetectFromDirectory_Arena_MatchesOnBothMarkers()
    {
        var install = MakeInstall("arena", "GLOBAL.BSA", "TEMPLATE.DAT");
        Assert.Equal(BethesdaGame.Arena, ClassicGameLocator.DetectFromDirectory(install)?.Game);
    }

    [Fact]
    public void DetectFromDirectory_MarkerSetsAreConjunctive()
    {
        // GLOBAL.BSA alone is not an Arena install — a stray file must not claim the whole directory.
        var install = MakeInstall("half-arena", "GLOBAL.BSA");
        Assert.Null(ClassicGameLocator.DetectFromDirectory(install));
    }

    [Fact]
    public void DetectFromDirectory_DisambiguatesFallout1FromFallout2()
    {
        // Both games carry MASTER.DAT + CRITTER.DAT at the root; only the executable/config entry
        // separates them, so each layout must resolve to its own game and never the sibling.
        var fo1 = MakeInstall("fo1", "MASTER.DAT", "CRITTER.DAT", "FALLOUTW.EXE");
        var fo2 = MakeInstall("fo2", "master.dat", "critter.dat", "FALLOUT2.EXE");

        Assert.Equal(BethesdaGame.Fallout1, ClassicGameLocator.DetectFromDirectory(fo1)?.Game);
        Assert.Equal(BethesdaGame.Fallout2, ClassicGameLocator.DetectFromDirectory(fo2)?.Game);
    }

    [Fact]
    public void DetectFromDirectory_AnyOfAlternatives_AcceptEachForm()
    {
        // Fallout 1's third marker entry lists three alternatives; each alone must satisfy it.
        var viaCdExe = MakeInstall("fo1-cd", "MASTER.DAT", "CRITTER.DAT", "FALLOUT.EXE");
        var viaCfg = MakeInstall("fo1-cfg", "MASTER.DAT", "CRITTER.DAT", "fallout.cfg");

        Assert.Equal(BethesdaGame.Fallout1, ClassicGameLocator.DetectFromDirectory(viaCdExe)?.Game);
        Assert.Equal(BethesdaGame.Fallout1, ClassicGameLocator.DetectFromDirectory(viaCfg)?.Game);
    }

    [Fact]
    public void DetectFromDirectory_DatPairWithoutAnyFalloutExecutable_MatchesNeitherGame()
    {
        var ambiguous = MakeInstall("dat-pair-only", "MASTER.DAT", "CRITTER.DAT");
        Assert.Null(ClassicGameLocator.DetectFromDirectory(ambiguous));
    }

    [Fact]
    public void DetectFromDirectory_Daggerfall_MatchesNestedArena2Markers()
    {
        var dagger = MakeInstall("dagger", @"ARENA2\ARCH3D.BSA", @"ARENA2\MAPS.BSA", "FALL.EXE");
        var profile = ClassicGameLocator.DetectFromDirectory(dagger);

        Assert.Equal(BethesdaGame.Daggerfall, profile?.Game);
        Assert.Equal("ARENA2", profile!.ClassicLooseRoot);
    }

    [Fact]
    public void DetectFromDirectory_MissingDirectory_ReturnsNull()
    {
        Assert.Null(ClassicGameLocator.DetectFromDirectory(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void DetectRootForFile_ResolvesFromInsideTheDataTree()
    {
        // A file three levels under the Fallout root (DATA\SOUND\MUSIC\*.ACM) must climb to the install.
        var fo1 = MakeInstall("fo1-deep", "MASTER.DAT", "CRITTER.DAT", "FALLOUTW.EXE",
            @"DATA\SOUND\MUSIC\01HUB.ACM");

        var result = ClassicGameLocator.DetectRootForFile(Path.Combine(fo1, @"DATA\SOUND\MUSIC\01HUB.ACM"));

        Assert.Equal(BethesdaGame.Fallout1, result?.Profile.Game);
        Assert.Equal(fo1, result?.Root);
    }

    [Fact]
    public void DetectRootForFile_ArchiveBesideTheMarkers_ResolvesImmediately()
    {
        var arena = MakeInstall("arena-file", "GLOBAL.BSA", "TEMPLATE.DAT");
        var result = ClassicGameLocator.DetectRootForFile(Path.Combine(arena, "GLOBAL.BSA"));

        Assert.Equal(BethesdaGame.Arena, result?.Profile.Game);
        Assert.Equal(arena, result?.Root);
    }

    [Fact]
    public void DetectRootForFile_BeyondTheProbeDepth_ReturnsNull()
    {
        // The walk is bounded at 4 ancestors: a file buried five directories under the root must not
        // resolve (probing arbitrarily deep unrelated paths is the cost this bound caps).
        var fo1 = MakeInstall("fo1-toodeep", "MASTER.DAT", "CRITTER.DAT", "FALLOUTW.EXE",
            @"a\b\c\d\e\buried.bin");

        Assert.Null(ClassicGameLocator.DetectRootForFile(Path.Combine(fo1, @"a\b\c\d\e\buried.bin")));
    }

    [Fact]
    public void DetectRootForFile_UnrelatedFile_ReturnsNull()
    {
        var stray = MakeInstall("stray", "readme.txt");
        Assert.Null(ClassicGameLocator.DetectRootForFile(Path.Combine(stray, "readme.txt")));
    }
}
