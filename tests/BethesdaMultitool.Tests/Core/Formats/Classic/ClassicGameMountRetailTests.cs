using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) that a real classic install mounts as one filesystem
///     through its profile: the loose tree at the profile's loose root plus every archive its globs
///     name. Structural assertions only — presence and lower bounds, never content counts.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ClassicGameMountRetailTests
{
    /// <summary>
    ///     Turns a <c>RealAssetPaths.Classics</c> DATA root into the INSTALL root the locator and
    ///     the mount both expect: the same directory when the profile declares no loose root
    ///     (Arena), otherwise its parent (Daggerfall's ARENA2, Battlespire's GAMEDATA).
    /// </summary>
    private static string InstallRootOf(string dataRoot, GameProfile profile)
    {
        return profile.ClassicLooseRoot.Length == 0
            ? dataRoot
            : Directory.GetParent(dataRoot)!.FullName;
    }

    public static TheoryData<int, string> Games()
    {
        return new TheoryData<int, string>
        {
            { (int)BethesdaGame.Arena, "TEMPLATE.DAT" },
            { (int)BethesdaGame.Daggerfall, "TEXTURE.000" },
            { (int)BethesdaGame.Battlespire, "ARMOR.3D" }
        };
    }

    private static string? DataRootFor(BethesdaGame game)
    {
        return game switch
        {
            BethesdaGame.Arena => RealAssetPaths.Classics.Arena(),
            BethesdaGame.Daggerfall => RealAssetPaths.Classics.Daggerfall(),
            BethesdaGame.Battlespire => RealAssetPaths.Classics.Battlespire(),
            _ => null
        };
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void OpenGameRoot_MountsARealInstallAndServesItsLooseFiles(int gameValue, string knownLooseFile)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var game = (BethesdaGame)gameValue;
        var dataRoot = DataRootFor(game);
        Assert.SkipWhen(dataRoot is null, RealAssetPaths.SkipMessage(game.ToString()));

        var profile = GameProfiles.For(game);
        var installRoot = InstallRootOf(dataRoot!, profile);

        // The derived install root is the one the app's single detection rule recognises.
        var detected = ClassicGameLocator.DetectFromDirectory(installRoot);
        Assert.NotNull(detected);
        Assert.Equal(game, detected.Game);

        using var fs = GameFileSystem.OpenGameRoot(profile, installRoot);

        // The loose layer is rooted at the DATA directory, so a data file resolves by bare name.
        Assert.True(fs.Exists(knownLooseFile), $"{game}: '{knownLooseFile}' did not resolve in the mount.");
        Assert.NotNull(fs.TryReadAllBytes(knownLooseFile));

        // The mount enumerates the loose tree and every archive layer together.
        var entries = fs.EnumerateFiles().Take(5_000).ToList();
        Assert.True(entries.Count > 100, $"{game}: only {entries.Count} entries enumerated.");
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void TryOpenGameRoot_BuildsABrowsableTreeForARealInstall(int gameValue, string knownLooseFile)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var game = (BethesdaGame)gameValue;
        var dataRoot = DataRootFor(game);
        Assert.SkipWhen(dataRoot is null, RealAssetPaths.SkipMessage(game.ToString()));

        var installRoot = InstallRootOf(dataRoot!, GameProfiles.For(game));

        using var session = AssetBrowseSession.TryOpenGameRoot(installRoot);

        Assert.NotNull(session);
        Assert.Equal(installRoot, session.SourcePath);
        Assert.True(session.FileSystem.Exists(knownLooseFile));

        // The tree is built eagerly at open and has real content under its root.
        var nodes = Flatten(session.Root).ToList();
        Assert.True(nodes.Count > 100, $"{game}: tree held only {nodes.Count} nodes.");
        Assert.Contains(nodes, n => string.Equals(n.Name, knownLooseFile, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<AssetNode> Flatten(AssetNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}
