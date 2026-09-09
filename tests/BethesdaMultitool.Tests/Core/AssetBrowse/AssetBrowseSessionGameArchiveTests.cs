using System.IO.Compression;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Opening a classic game whose install IS a single archive.
///     <para>
///         The three TES Travels J2ME titles ship as one <c>.jar</c> with no install directory
///         anywhere, so <c>TryOpenGameRoot</c> — which probes DIRECTORIES — can never claim them.
///         Before this factory existed the browser could still mount such a jar, but only as an
///         anonymous archive, so nothing downstream could tell Stormhold from any other zip.
///     </para>
/// </summary>
public sealed class AssetBrowseSessionGameArchiveTests
{
    /// <summary>Stormhold is identified by these three entries (<c>GameProfiles</c>).</summary>
    private static readonly string[] StormholdMarkers =
        ["ESGame.class", "charin.dat", "monsterfilenamesin.dat"];

    /// <summary>Oblivion mobile is a different game in the same container shape.</summary>
    private static readonly string[] OblivionMobileMarkers = ["eso.ver", "startup.scr", "lang_0.txt"];

    private static string WriteJar(params string[] entryNames)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "assetbrowse-jar-" + Guid.NewGuid().ToString("N") + ".jar");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var name in entryNames)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("payload");
            }
        }

        return path;
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leaked temp file is harmless.
        }
    }

    /// <summary>
    ///     The claim this factory exists to make: a jar carrying a game's markers is recognised AS
    ///     that game, and the identity survives onto the session.
    /// </summary>
    [Fact]
    public void AJarCarryingTheMarkersIsClaimedAsThatGame()
    {
        var path = WriteJar([.. StormholdMarkers, "sprites/hero.cus"]);
        try
        {
            using var session = AssetBrowseSession.TryOpenGameArchive(path);

            Assert.NotNull(session);
            Assert.NotNull(session.Profile);
            Assert.Equal(BethesdaGame.Stormhold, session.Profile.Game);
            Assert.Equal(Path.GetFullPath(path), session.SourcePath);
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>Detection is per game, not "any jar wins" — a different marker set is a different game.</summary>
    [Fact]
    public void ADifferentMarkerSetIsADifferentGame()
    {
        var path = WriteJar(OblivionMobileMarkers);
        try
        {
            using var session = AssetBrowseSession.TryOpenGameArchive(path);

            Assert.NotNull(session);
            Assert.Equal(BethesdaGame.OblivionMobile, session.Profile?.Game);
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    ///     ⚠ The discriminating case. A factory that simply mounted every archive and reported
    ///     success would pass both tests above; only refusing an unrecognised archive shows that
    ///     detection actually ran. The caller relies on the null to fall back to a plain mount.
    /// </summary>
    [Fact]
    public void AnUnrecognisedArchiveIsRefused()
    {
        var path = WriteJar("readme.txt", "data/thing.bin");
        try
        {
            Assert.Null(AssetBrowseSession.TryOpenGameArchive(path));
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    ///     A partial marker set is refused: every marker has to be present, so a jar that merely
    ///     shares one file name with a game is not that game.
    /// </summary>
    [Fact]
    public void APartialMarkerSetIsRefused()
    {
        var path = WriteJar("ESGame.class", "charin.dat");
        try
        {
            Assert.Null(AssetBrowseSession.TryOpenGameArchive(path));
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    ///     The same jar still opens through the plain archive factory, and THAT session carries no
    ///     identity — which is precisely what the new factory adds. Pinning both halves keeps the
    ///     fallback in <c>OpenArchive_Click</c> meaningful.
    /// </summary>
    [Fact]
    public void ThePlainArchiveFactoryStillOpensItButWithoutAnIdentity()
    {
        var path = WriteJar(StormholdMarkers);
        try
        {
            using var session = AssetBrowseSession.OpenArchive(path);

            Assert.Null(session.Profile);
            Assert.NotEmpty(session.Root.Children);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void TryOpenGameArchive_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => AssetBrowseSession.TryOpenGameArchive(null!));
    }

    /// <summary>A path that is not an archive at all answers null rather than throwing.</summary>
    [Fact]
    public void AMissingFileIsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), "assetbrowse-absent-" + Guid.NewGuid().ToString("N") + ".jar");

        Assert.Null(AssetBrowseSession.TryOpenGameArchive(path));
    }
}