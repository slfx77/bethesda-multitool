using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Rendering.Level2D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Rendering.Level2D;

/// <summary>
///     Which files the 2D map pane claims, and the agreement between the two places that used to
///     answer it.
///     <para>
///         ⚠⚠ This exists because those two places DRIFTED. <see cref="Level2DViewPolicy" /> said a
///         Shadowkey <c>.ZMP</c> had a 2D picture, while <c>AssetLevel2DSource</c> kept a separate
///         hardcoded list that had never heard of one — so the policy advertised a view that
///         nothing could build. A wire connected at one end is worse than a missing one: the UI
///         offers the file, opens the pane, and shows nothing, which reads as a broken asset.
///     </para>
/// </summary>
public class Level2DViewPolicyTests
{
    private static AssetNode Leaf(string name)
    {
        return new AssetNode(name, name, AssetNodeKind.Map, 1);
    }

    /// <summary>Every name the pane should claim, and the ones it must not.</summary>
    public static TheoryData<string, bool> Names()
    {
        return new TheoryData<string, bool>
        {
            // Arena voxel grids.
            { "CITY.MIF", true },
            { "WILD.RMD", true },
            // Daggerfall's world heightmap and its two overlays, all named by the game.
            { "WOODS.WLD", true },
            { "CLIMATE.PAK", true },
            { "POLITIC.PAK", true },
            // A Shadowkey zone grid and an Oblivion mobile tile map.
            { "azra.zmp", true },
            { "l01_1.jtm", true },
            // A Van Buren map inside a .grp, named by the archive backend as NNNNN.EMAP.
            { "00001.EMAP", true },
            // Geometry opens in 3D; it has no authored picture.
            { "ARMOR.3D", false },
            { "L8.BS6", false },
            // ⚠ Neither extension is exclusive, so both are matched by NAME. Redguard ships .WLD files
            // in an unrelated format, and .PAK is far too common to claim outright — it used to be, and
            // that claimed files nothing could draw.
            { "ISLAND.WLD", false },
            { "SOMETHING.PAK", false }
        };
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void SupportsClaimsExactlyTheGridAuthoredFiles(string fileName, bool expected)
    {
        Assert.Equal(expected, Level2DViewPolicy.Supports(fileName));
    }

    /// <summary>
    ///     ⚠ The assertion that stops the drift recurring: the browser's gate is pinned to the same
    ///     LITERAL expectations as the policy. If someone re-introduces a private extension list in
    ///     <c>AssetLevel2DSource</c>, these values catch it.
    ///     <para>
    ///         ⛔ Deliberately NOT written as <c>Assert.Equal(Supports(name), CanOpen(node))</c>.
    ///         <c>CanOpen</c> delegates to <c>Supports</c>, so that comparison is a tautology that
    ///         passes no matter how wrong either becomes — a test that cannot fail, which is worse
    ///         than none because it reads as coverage.
    ///     </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void TheBrowserGateClaimsExactlyTheSameFiles(string fileName, bool expected)
    {
        Assert.Equal(expected, AssetLevel2DSource.CanOpen(Leaf(fileName)));
    }

    /// <summary>A grid-authored file opens in 2D because 2D is its original view.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void AGridAuthoredFileDefaultsToTwoDimensional(string fileName, bool expected)
    {
        Assert.Equal(expected, Level2DViewPolicy.DefaultsToTwoDimensional(fileName));
    }

    /// <summary>
    ///     Matching is on the file name, not the whole path — a node carries a virtual path and the
    ///     policy is handed the leaf name either way.
    /// </summary>
    [Theory]
    [InlineData(@"DF\DAGGER\ARENA2\woods.wld")]
    [InlineData("zones/azra.zmp")]
    public void ANestedPathIsMatchedByItsLeafName(string path)
    {
        Assert.True(Level2DViewPolicy.Supports(path));
    }
}