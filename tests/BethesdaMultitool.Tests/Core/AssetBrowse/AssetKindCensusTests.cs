using BethesdaMultitool.Core.AssetBrowse;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Pins the per-kind leaf census the type-filter flyout is built from.</summary>
public sealed class AssetKindCensusTests
{
    /// <summary>Every present kind is counted once per leaf, listed in display order, and absent kinds are absent.</summary>
    [Fact]
    public void Present_CountsLeavesPerKindInDisplayOrder()
    {
        using var fs = new FakeGameFileSystem(("a.dds", 1), ("b\\c.frm", 2), ("b\\d.nif", 3), ("b\\e\\f.wav", 4),
            ("g.bik", 5), ("noext", 6));
        var root = AssetTreeBuilder.Build(fs, "root");

        var present = AssetKindCensus.Present(root);

        Assert.Equal(new[]
        {
            new AssetKindCount(AssetNodeKind.Texture, 1),
            new AssetKindCount(AssetNodeKind.Sprite, 1),
            new AssetKindCount(AssetNodeKind.Model, 1),
            new AssetKindCount(AssetNodeKind.Audio, 1),
            new AssetKindCount(AssetNodeKind.Video, 1),
            new AssetKindCount(AssetNodeKind.Raw, 1)
        }, present);
        Assert.DoesNotContain(present, count => count.Kind == AssetNodeKind.Folder);
        Assert.DoesNotContain(present, count => count.Kind == AssetNodeKind.Text);
        Assert.Equal(6, present.Sum(count => count.Count));
    }

    /// <summary>Several leaves of one kind in different folders add up to one row.</summary>
    [Fact]
    public void Present_AddsUpLeavesOfOneKindAcrossFolders()
    {
        using var fs = new FakeGameFileSystem(("x\\a.dds", 1), ("y\\b.dds", 2), ("c.dds", 3), ("y\\z\\d.png", 4));
        var root = AssetTreeBuilder.Build(fs, "root");

        var present = AssetKindCensus.Present(root);

        var texture = Assert.Single(present);
        Assert.Equal(new AssetKindCount(AssetNodeKind.Texture, 4), texture);
    }

    /// <summary>A root with no leaves has nothing to list.</summary>
    [Fact]
    public void Present_EmptyRootIsEmpty()
    {
        using var fs = new FakeGameFileSystem();

        Assert.Empty(AssetKindCensus.Present(AssetTreeBuilder.Build(fs, "root")));
    }

    /// <summary>A 2048-folder chain is walked without recursion, so the census cannot overflow the stack.</summary>
    [Fact]
    public void Present_WalksADeepChainIteratively()
    {
        var path = string.Join('\\', Enumerable.Repeat("d", 2048)) + "\\leaf.nif";
        using var fs = new FakeGameFileSystem((path, 1));
        var root = AssetTreeBuilder.Build(fs, "root");

        var present = AssetKindCensus.Present(root);

        Assert.Equal(new AssetKindCount(AssetNodeKind.Model, 1), Assert.Single(present));
    }
}
