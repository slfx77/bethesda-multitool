using System.IO.Compression;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Games;

/// <summary>
///     Pins classic-install detection over synthetic marker layouts in temp directories: conjunctive
///     marker sets, the <c>|</c> any-of alternatives, the Fallout 1 / Fallout 2 disambiguation (both
///     roots carry the same DAT pair), and the bounded walk-up from a file to its install root.
/// </summary>
public sealed class ClassicGameLocatorTests : IDisposable
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

    /// <summary>
    ///     Re-spells an engine-style relative path (<c>@"ARENA2\ARCH3D.BSA"</c>) for the host — a
    ///     no-op on Windows. The layouts below are staged as HOST trees; without this a Unix run
    ///     writes one file literally named "ARENA2\ARCH3D.BSA" at the install root, which then
    ///     matches (or, for the depth test, fails to be deep) for the wrong reason.
    /// </summary>
    private static string Host(string relativePath)
    {
        return relativePath.Replace('\\', Path.DirectorySeparatorChar);
    }

    private string MakeInstall(string name, params string[] relativeFiles)
    {
        var install = Path.Combine(_root, name);
        foreach (var relative in relativeFiles)
        {
            var path = Path.Combine(install, Host(relative));
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

    [Theory]
    [InlineData("SYSTEM.CNF", @"DATA\ALL.DDF")]
    [InlineData("default.xbe", @"resx\all.ddf")]
    public void BrotherhoodOfSteel_BothConsoleLayoutsResolveFromDirectoriesAndArchiveNames(
        string bootMarker, string masterMarker)
    {
        var install = MakeInstall("bos", bootMarker, masterMarker);
        Assert.Equal(BethesdaGame.FalloutBrotherhoodOfSteel,
            ClassicGameLocator.DetectFromDirectory(install)?.Game);

        // Disc backends can report different casing and separators from the profile markers.
        string[] names = [bootMarker.ToLowerInvariant(), masterMarker.Replace('\\', '/').ToLowerInvariant()];
        Assert.Equal(BethesdaGame.FalloutBrotherhoodOfSteel,
            ClassicGameLocator.DetectFromArchiveNames(names)?.Game);
    }

    [Theory]
    [InlineData("SYSTEM.CNF")]
    [InlineData("default.xbe")]
    [InlineData(@"DATA\ALL.DDF")]
    [InlineData(@"resx\all.ddf")]
    [InlineData("default.xbe", @"resx\other.ddf")]
    public void BrotherhoodOfSteel_IncompleteOrUnrelatedLayoutsMatchNeitherSource(params string[] markers)
    {
        var install = MakeInstall("incomplete-bos", markers);
        Assert.Null(ClassicGameLocator.DetectFromDirectory(install));
        Assert.Null(ClassicGameLocator.DetectFromArchiveNames(markers));
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

        var result = ClassicGameLocator.DetectRootForFile(Path.Combine(fo1, Host(@"DATA\SOUND\MUSIC\01HUB.ACM")));

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

        Assert.Null(ClassicGameLocator.DetectRootForFile(Path.Combine(fo1, Host(@"a\b\c\d\e\buried.bin"))));
    }

    private string MakeJar(string name, params string[] entryNames)
    {
        var path = Path.Combine(_root, name);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var entry in entryNames)
        {
            using var writer = zip.CreateEntry(entry).Open();
            writer.WriteByte(0x00);
        }

        return path;
    }

    [Theory]
    [InlineData(BethesdaGame.Stormhold, "ESGame.class", "charin.dat", "monsterfilenamesin.dat")]
    [InlineData(BethesdaGame.Dawnstar, "ESGame.class", "datfiles.lmp", "imgfiles.lmp")]
    [InlineData(BethesdaGame.OblivionMobile, "eso.ver", "startup.scr", "lang_0.txt")]
    public void DetectFromArchive_ClaimsAJ2meJarFromItsEntryNames(BethesdaGame expected, params string[] markers)
    {
        // A J2ME title IS its JAR: the same marker set that identifies an unpacked directory
        // identifies the archive, and the archive's own path is the install root.
        var jar = MakeJar($"{expected}.jar", [.. markers, "META-INF/MANIFEST.MF", "a.class"]);
        Assert.Equal(expected, ClassicGameLocator.DetectFromArchive(jar)?.Game);
    }

    [Fact]
    public void DetectFromArchive_MarkerSetsAreConjunctiveInsideAJar()
    {
        // Both Stormhold and Dawnstar ship ESGame.class; only the second/third markers separate
        // them, and a JAR carrying neither pair is nobody's.
        var half = MakeJar("half.jar", "ESGame.class", "npcstrings.dat");
        Assert.Null(ClassicGameLocator.DetectFromArchive(half));
    }

    [Fact]
    public void DetectFromArchive_ReadsNestedEntryNamesWithEitherSeparator()
    {
        // Markers are matched against separator-normalised entry names, so a marker written with a
        // backslash still meets an entry the zip writer stored with a forward slash.
        var jar = MakeJar("psp.jar", "PSP_GAME/PARAM.SFO", "PSP_GAME/SYSDIR/EBOOT.BIN", "PSP_GAME/USRDIR/GR.ARC");
        Assert.Equal(BethesdaGame.OblivionPsp, ClassicGameLocator.DetectFromArchive(jar)?.Game);
    }

    [Fact]
    public void DetectFromArchive_RejectsNonZipFilesAndMissingPaths()
    {
        var notZip = Path.Combine(_root, "GLOBAL.BSA");
        File.WriteAllBytes(notZip, [0x00, 0x01, 0x02, 0x03]);
        Assert.Null(ClassicGameLocator.DetectFromArchive(notZip));
        Assert.Null(ClassicGameLocator.DetectFromArchive(Path.Combine(_root, "absent.jar")));

        // Right magic, but not an archive: the BCL reader fails and the probe answers null rather than throwing.
        var truncated = Path.Combine(_root, "truncated.jar");
        File.WriteAllBytes(truncated, [(byte)'P', (byte)'K', 3, 4, 0, 0]);
        Assert.Null(ClassicGameLocator.DetectFromArchive(truncated));
    }

    [Fact]
    public void DetectFromDirectory_Shadowkey_MatchesTheSymbianApplicationDirectory()
    {
        // The root is the 6R51 application directory itself, not the Symbian system tree above it.
        var app = MakeInstall(@"shadowkey\system\apps\6R51", "6R51.APP", "azra.zon", "StringTable.eng");
        var profile = ClassicGameLocator.DetectFromDirectory(app);

        Assert.Equal(BethesdaGame.Shadowkey, profile?.Game);
        Assert.Equal(string.Empty, profile!.ClassicLooseRoot);
        Assert.Null(ClassicGameLocator.DetectFromDirectory(Path.Combine(_root, "shadowkey")));
    }

    [Fact]
    public void DetectRootForFile_ShadowkeyScriptInASubdirectory_ClimbsToTheApplicationDirectory()
    {
        var app = MakeInstall(@"shadowkey2\system\apps\6R51", "6R51.APP", "azra.zon", "StringTable.eng",
            @"Armor\iron_boots.s");

        var result = ClassicGameLocator.DetectRootForFile(Path.Combine(app, @"Armor\iron_boots.s"));

        Assert.Equal(BethesdaGame.Shadowkey, result?.Profile.Game);
        Assert.Equal(app, result?.Root);
    }

    [Fact]
    public void DetectFromDirectory_OblivionPsp_MatchesAnExtractedUmdTree()
    {
        var umd = MakeInstall("psp", @"PSP_GAME\PARAM.SFO", @"PSP_GAME\SYSDIR\EBOOT.BIN", @"PSP_GAME\USRDIR\GR.ARC");

        // A PSP tree without the game's data pack is some other PSP title, not Oblivion.
        var otherPsp = MakeInstall("other-psp", @"PSP_GAME\PARAM.SFO", @"PSP_GAME\SYSDIR\EBOOT.BIN");
        Assert.Null(ClassicGameLocator.DetectFromDirectory(otherPsp));

        // The 11 January 2007 beta ships an unencrypted BOOT.BIN in place of EBOOT.BIN; requiring
        // EBOOT.BIN alone dropped that build on the floor.
        var bootOnly = MakeInstall("psp-boot", @"PSP_GAME\PARAM.SFO", @"PSP_GAME\SYSDIR\BOOT.BIN",
            @"PSP_GAME\USRDIR\GR.ARC");
        Assert.Equal(BethesdaGame.OblivionPsp, ClassicGameLocator.DetectFromDirectory(bootOnly)?.Game);
        var profile = ClassicGameLocator.DetectFromDirectory(umd);

        Assert.Equal(BethesdaGame.OblivionPsp, profile?.Game);
        Assert.Equal(@"PSP_GAME\USRDIR", profile!.ClassicLooseRoot);
    }

    [Fact]
    public void DetectFromDirectory_UnpackedJ2meJars_ResolveLikeTheirArchives()
    {
        var stormhold = MakeInstall("stormhold", "ESGame.class", "charin.dat", "monsterfilenamesin.dat");
        var dawnstar = MakeInstall("dawnstar", "ESGame.class", "datfiles.lmp", "imgfiles.lmp");
        var oblivion = MakeInstall("oblivion", "eso.ver", "startup.scr", "lang_0.txt");

        Assert.Equal(BethesdaGame.Stormhold, ClassicGameLocator.DetectFromDirectory(stormhold)?.Game);
        Assert.Equal(BethesdaGame.Dawnstar, ClassicGameLocator.DetectFromDirectory(dawnstar)?.Game);
        Assert.Equal(BethesdaGame.OblivionMobile, ClassicGameLocator.DetectFromDirectory(oblivion)?.Game);
    }

    [Fact]
    public void DetectRootForFile_UnrelatedFile_ReturnsNull()
    {
        var stray = MakeInstall("stray", "readme.txt");
        Assert.Null(ClassicGameLocator.DetectRootForFile(Path.Combine(stray, "readme.txt")));
    }
}
