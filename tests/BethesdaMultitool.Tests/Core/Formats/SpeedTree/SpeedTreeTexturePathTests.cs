using BethesdaMultitool.Core.Formats.SpeedTree;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.SpeedTree;

/// <summary>
///     Tests for <see cref="SpeedTreeTexturePath.ToGamePath" /> kind-aware routing. The regression target is
///     the July-prototype leaf card <c>WastelandUndergrowthBranches01.tga</c>: its stem contains none of the
///     bark/foliage/leaf keywords, so <see cref="SpeedTreeTextureKind.Auto" /> mis-files it under
///     <c>textures\trees\</c> (a TEX MISS); <see cref="SpeedTreeTextureKind.Leaf" /> must route it to
///     <c>textures\trees\leaves\</c> and collapse the per-card <c>01</c> suffix to the shipped
///     <c>wastelandundergrowthbranches</c> atlas.
/// </summary>
public sealed class SpeedTreeTexturePathTests
{
    private const string ProtoLeafCard =
        @"C:\Noah\Fallout\Trees\WastelandUndergrowth01\WastelandUndergrowthBranches01.tga";

    [Fact]
    public void ToGamePath_LeafKind_BranchesNamedCard_RoutesToLeavesAndStripsIndex()
    {
        var result = SpeedTreeTexturePath.ToGamePath(ProtoLeafCard, SpeedTreeTextureKind.Leaf);

        Assert.Equal(@"textures\trees\leaves\wastelandundergrowthbranches.dds", result);
    }

    [Fact]
    public void ToGamePath_AutoKind_BranchesNamedCard_RegressesToTreesRoot()
    {
        // Documents the pre-fix behavior the leaf call site used to get (and the reason for the bug):
        // with no keyword match, Auto files the card directly under \trees\ with its index intact.
        var result = SpeedTreeTexturePath.ToGamePath(ProtoLeafCard);

        Assert.Equal(@"textures\trees\wastelandundergrowthbranches01.dds", result);
    }

    [Fact]
    public void ToGamePath_BarkKind_RoutesToBranches()
    {
        var result = SpeedTreeTexturePath.ToGamePath(
            @"C:\Noah\Fallout\Trees\WastelandShrub01\WastelandShrub01Bark.tga", SpeedTreeTextureKind.Bark);

        Assert.Equal(@"textures\trees\branches\wastelandshrub01bark.dds", result);
    }

    [Fact]
    public void ToGamePath_LeafKind_NumberedLeavesAtlas_KeepsIndex()
    {
        // Genuinely numbered "…leavesNN" atlases ship per-number — the collapse must NOT strip these.
        var result = SpeedTreeTexturePath.ToGamePath(
            @"C:\dev\Trees\Pine01\PineLeaves01.tga", SpeedTreeTextureKind.Leaf);

        Assert.Equal(@"textures\trees\leaves\pineleaves01.dds", result);
    }

    [Fact]
    public void ToGamePath_EmbeddedGameRelativePath_HonoredVerbatimForAnyKind()
    {
        // Oblivion's TreeKvatchBurnt bark embeds an already-game-relative path; it must be honored
        // verbatim regardless of kind rather than re-routed by the stem heuristic.
        const string embedded = @"C:\Oblivion\Data\Textures\Trees\Branches\TreeKvatchBurnt.dds";

        Assert.Equal(@"textures\trees\branches\treekvatchburnt.dds",
            SpeedTreeTexturePath.ToGamePath(embedded, SpeedTreeTextureKind.Bark));
        Assert.Equal(@"textures\trees\branches\treekvatchburnt.dds",
            SpeedTreeTexturePath.ToGamePath(embedded, SpeedTreeTextureKind.Leaf));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToGamePath_EmptyInput_ReturnsNull(string? devPath)
    {
        Assert.Null(SpeedTreeTexturePath.ToGamePath(devPath, SpeedTreeTextureKind.Leaf));
    }
}
