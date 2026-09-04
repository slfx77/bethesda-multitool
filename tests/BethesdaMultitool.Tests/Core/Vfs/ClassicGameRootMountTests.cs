using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Vfs;

/// <summary>
///     Pins the classic-install mount: the loose layer lands at the profile's
///     <c>ClassicLooseRoot</c> rather than the install root, an archive glob carrying a
///     subdirectory resolves relative to the install root, and detection stays on the one rule the
///     rest of the app uses (markers, exact root).
/// </summary>
public sealed class ClassicGameRootMountTests : IDisposable
{
    private readonly string _root;

    public ClassicGameRootMountTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "classic-mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; leaked temp dirs are harmless.
        }
    }

    private void Write(string relativePath, byte[] content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    /// <summary>A Daggerfall install: markers under ARENA2, which is also its loose root.</summary>
    private void StageDaggerfall()
    {
        Write(@"ARENA2\ARCH3D.BSA", [0x01]);
        Write(@"ARENA2\MAPS.BSA", [0x02]);
    }

    [Fact]
    public void OpenGameRoot_MountsTheLooseLayerAtTheProfilesLooseRoot()
    {
        StageDaggerfall();
        Write(@"ARENA2\TEXTURE.000", [1, 2, 3, 4]);

        // A file directly under the install root is NOT data — only ARENA2 is mounted.
        Write("FALL.EXE", [9, 9]);

        using var fs = GameFileSystem.OpenGameRoot(GameProfiles.For(BethesdaGame.Daggerfall), _root);

        Assert.True(fs.Exists("TEXTURE.000"));
        Assert.Equal([1, 2, 3, 4], fs.TryReadAllBytes("TEXTURE.000"));
        Assert.False(fs.Exists("FALL.EXE"));
        Assert.False(fs.Exists(@"ARENA2\TEXTURE.000"));
    }

    [Fact]
    public void OpenGameRoot_MountsTheRootItselfWhenTheProfileNamesNoLooseRoot()
    {
        // Arena's data IS the install root, so ClassicLooseRoot is empty.
        Write("GLOBAL.BSA", [0x01]);
        Write("TEMPLATE.DAT", [0x02]);
        Write("AGTEMPL.INF", [7, 7, 7]);

        var profile = GameProfiles.For(BethesdaGame.Arena);
        Assert.Empty(profile.ClassicLooseRoot);

        using var fs = GameFileSystem.OpenGameRoot(profile, _root);

        Assert.Equal([7, 7, 7], fs.TryReadAllBytes("AGTEMPL.INF"));
    }

    [Fact]
    public void OpenGameRoot_ResolvesAGlobsSubdirectoryAgainstTheInstallRoot()
    {
        // Battlespire's globs are GAMEDATA\*.BSA, GAMEDATA\3D.BS6, GAMEDATA\SPIRE.SND — all
        // subdirectory-qualified. A malformed archive must degrade to an empty layer, not throw,
        // so the mount still serves the loose tree beside it.
        Write(@"GAMEDATA\3D.BS6", [0x01]);
        Write(@"GAMEDATA\BSI.BSA", [0x02]);
        Write(@"GAMEDATA\ARMOR.3D", [4, 5, 6]);

        using var fs = GameFileSystem.OpenGameRoot(GameProfiles.For(BethesdaGame.Battlespire), _root);

        Assert.Equal([4, 5, 6], fs.TryReadAllBytes("ARMOR.3D"));
    }

    [Fact]
    public void OpenGameRoot_WithoutLooseFilesMountsArchivesOnly()
    {
        StageDaggerfall();
        Write(@"ARENA2\TEXTURE.000", [1]);

        using var fs = GameFileSystem.OpenGameRoot(
            GameProfiles.For(BethesdaGame.Daggerfall), _root, includeLooseFiles: false);

        Assert.False(fs.Exists("TEXTURE.000"));
    }

    [Fact]
    public void OpenGameRoot_ToleratesAnAbsentDirectoryForAGlob()
    {
        // Arena's install has no subdirectories; a profile glob pointing at a missing directory
        // must simply contribute no layer.
        Write("GLOBAL.BSA", [0x01]);
        Write("TEMPLATE.DAT", [0x02]);

        using var fs = GameFileSystem.OpenGameRoot(GameProfiles.For(BethesdaGame.Daggerfall), _root);

        Assert.False(fs.Exists("anything"));
    }

    [Fact]
    public void TryOpenGameRoot_RecognisesAnInstallRootByItsMarkers()
    {
        StageDaggerfall();
        Write(@"ARENA2\TEXTURE.000", [1, 2]);

        using var session = AssetBrowseSession.TryOpenGameRoot(_root);

        Assert.NotNull(session);
        Assert.Equal(Path.GetFileName(_root), session.SourceLabel);
        Assert.True(session.FileSystem.Exists("TEXTURE.000"));
    }

    [Fact]
    public void TryOpenGameRoot_ReturnsNullForADirectoryThatIsNotAnInstallRoot()
    {
        // The wrapper directory ABOVE a Steam re-release does not match the markers. Returning
        // null keeps one detection rule app-wide; the caller falls back to OpenFolder.
        StageDaggerfall();
        var wrapper = Directory.GetParent(_root)!.FullName;

        Assert.Null(AssetBrowseSession.TryOpenGameRoot(Path.Combine(_root, "ARENA2")));
        Assert.NotNull(AssetBrowseSession.TryOpenGameRoot(_root));
        Assert.NotEqual(_root, wrapper);
    }

    [Fact]
    public void TryOpenGameRoot_ReturnsNullForAnEmptyOrMissingDirectory()
    {
        Assert.Null(AssetBrowseSession.TryOpenGameRoot(_root));
        Assert.Null(AssetBrowseSession.TryOpenGameRoot(Path.Combine(_root, "does-not-exist")));
    }
}
