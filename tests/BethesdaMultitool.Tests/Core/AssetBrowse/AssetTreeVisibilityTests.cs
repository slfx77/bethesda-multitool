using BethesdaMultitool.Core.AssetBrowse;
using Slfx77.Multitool.Core.Browsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Pins which tree nodes the type filter shows: leaves by kind, folders by their descendants, the root always.</summary>
public sealed class AssetTreeVisibilityTests
{
    /// <summary>With nothing excluded every node is visible and the counts agree.</summary>
    [Fact]
    public void All_ShowsEveryNode()
    {
        using var fs = new FakeGameFileSystem(("tex\\a.dds", 1), ("tex\\b.dds", 2), ("snd\\c.wav", 3));
        var root = AssetTreeBuilder.Build(fs, "root");

        var visibility = AssetTreeVisibility.Compute(root, AssetKindFilter.All);

        Assert.Equal(3, visibility.VisibleFileCount);
        Assert.Equal(3, visibility.TotalFileCount);
        Assert.True(visibility.IsVisible(root));
        Assert.Equal(root.Children, visibility.VisibleChildren(root));
        foreach (var folder in root.Children)
        {
            Assert.True(visibility.IsVisible(folder));
            Assert.Equal(folder.Children, visibility.VisibleChildren(folder));
            Assert.All(folder.Children, leaf => Assert.True(visibility.IsVisible(leaf)));
        }
    }

    /// <summary>A folder whose every leaf is excluded disappears; its sibling and the root stay.</summary>
    [Fact]
    public void ExcludingAKind_HidesTheFolderThatOnlyHoldsIt()
    {
        using var fs = new FakeGameFileSystem(("tex\\a.dds", 1), ("tex\\b.dds", 2), ("snd\\c.wav", 3));
        var root = AssetTreeBuilder.Build(fs, "root");
        var snd = Assert.Single(root.Children, node => node.Name == "snd");
        var tex = Assert.Single(root.Children, node => node.Name == "tex");

        var visibility = AssetTreeVisibility.Compute(root, AssetKindFilter.All.With(AssetNodeKind.Texture, false));

        Assert.True(visibility.IsVisible(root));
        Assert.False(visibility.IsVisible(tex));
        Assert.All(tex.Children, leaf => Assert.False(visibility.IsVisible(leaf)));
        Assert.True(visibility.IsVisible(snd));
        Assert.True(visibility.IsVisible(Assert.Single(snd.Children)));
        Assert.Same(snd, Assert.Single(visibility.VisibleChildren(root)));
        Assert.Empty(visibility.VisibleChildren(tex));
        Assert.Equal(1, visibility.VisibleFileCount);
        Assert.Equal(3, visibility.TotalFileCount);
    }

    /// <summary>Excluding every present kind leaves only the root, with nothing under it.</summary>
    [Fact]
    public void ExcludingEveryPresentKind_LeavesOnlyTheRoot()
    {
        using var fs = new FakeGameFileSystem(("tex\\a.dds", 1), ("snd\\c.wav", 3));
        var root = AssetTreeBuilder.Build(fs, "root");
        var filter = AssetKindFilter.All.With(AssetNodeKind.Texture, false).With(AssetNodeKind.Audio, false);

        var visibility = AssetTreeVisibility.Compute(root, filter);

        Assert.True(visibility.IsVisible(root));
        Assert.Empty(visibility.VisibleChildren(root));
        Assert.All(root.Children, folder => Assert.False(visibility.IsVisible(folder)));
        Assert.Equal(0, visibility.VisibleFileCount);
        Assert.Equal(2, visibility.TotalFileCount);
    }

    /// <summary>A folder stays visible through a visible subfolder even when its own leaf is hidden, and siblings keep builder order.</summary>
    [Fact]
    public void FolderWithHiddenLeafButVisibleSubfolder_StaysVisibleInBuilderOrder()
    {
        using var fs = new FakeGameFileSystem(("top\\hidden.dds", 1), ("top\\sub\\kept.wav", 2), ("top\\zed.wav", 3),
            ("top\\alpha.wav", 4), ("top\\beta.dds", 5));
        var root = AssetTreeBuilder.Build(fs, "root");
        var top = Assert.Single(root.Children);

        var visibility = AssetTreeVisibility.Compute(root, AssetKindFilter.All.With(AssetNodeKind.Texture, false));

        Assert.True(visibility.IsVisible(top));
        Assert.Equal(new[] { "sub", "alpha.wav", "zed.wav" }, visibility.VisibleChildren(top).Select(node => node.Name));
        Assert.Equal(new[] { "sub", "alpha.wav", "beta.dds", "hidden.dds", "zed.wav" }, top.Children.Select(node => node.Name));
        Assert.Equal(top.Children.Where(visibility.IsVisible), visibility.VisibleChildren(top));
        Assert.Equal(3, visibility.VisibleFileCount);
        Assert.Equal(5, visibility.TotalFileCount);
    }

    /// <summary>A 2048-folder chain hides and reappears whole, computed without recursion.</summary>
    [Fact]
    public void DeepChain_HidesAndShowsEveryFolderIteratively()
    {
        var path = string.Join('\\', Enumerable.Repeat("d", 2048)) + "\\leaf.nif";
        using var fs = new FakeGameFileSystem((path, 1));
        var root = AssetTreeBuilder.Build(fs, "root");
        var chain = new List<AssetNode>();
        var node = root;
        while (node.Children.Count > 0)
        {
            node = Assert.Single(node.Children);
            chain.Add(node);
        }

        Assert.Equal(2049, chain.Count);
        Assert.Equal(AssetNodeKind.Model, chain[^1].Kind);

        var hidden = AssetTreeVisibility.Compute(root, AssetKindFilter.All.With(AssetNodeKind.Model, false));
        Assert.True(hidden.IsVisible(root));
        Assert.All(chain, link => Assert.False(hidden.IsVisible(link)));
        Assert.Empty(hidden.VisibleChildren(root));
        Assert.Equal(0, hidden.VisibleFileCount);
        Assert.Equal(1, hidden.TotalFileCount);

        var shown = AssetTreeVisibility.Compute(root, AssetKindFilter.All.With(AssetNodeKind.Model, false).With(AssetNodeKind.Model, true));
        Assert.All(chain, link => Assert.True(shown.IsVisible(link)));
        Assert.Same(chain[0], Assert.Single(shown.VisibleChildren(root)));
        Assert.Equal(1, shown.VisibleFileCount);
    }

    /// <summary>Hiding a checked leaf does not uncheck it: the check owner is independent of visibility.</summary>
    [Fact]
    public async Task HidingACheckedLeaf_KeepsItsCheck()
    {
        var fs = new FakeGameFileSystem(("tex\\a.dds", 1), ("snd\\c.wav", 3));
        var session = new AssetBrowseSession(fs, "checks", "checks", AssetTreeBuilder.Build(fs, "checks"));
        await using var browser = new BrowserSession();
        await browser.ReplaceAsync(new BethesdaBrowseSource(session), TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        var leaf = Assert.Single(Assert.Single(session.Root.Children, node => node.Name == "tex").Children);
        selection.SetChecked(snapshot, leaf, true);
        Assert.True(leaf.IsChecked);

        var visibility = AssetTreeVisibility.Compute(session.Root, AssetKindFilter.All.With(AssetNodeKind.Texture, false));

        Assert.False(visibility.IsVisible(leaf));
        Assert.False(visibility.IsVisible(leaf.Parent!));
        Assert.Same(leaf, Assert.Single(selection.CaptureSelected(snapshot)));
        Assert.True(leaf.IsChecked);
        Assert.Equal(1, selection.Checks.SelectedCount);
    }

    /// <summary>A node from another tree is simply not visible, and null arguments are rejected.</summary>
    [Fact]
    public void ForeignNodesAndNulls()
    {
        using var fs = new FakeGameFileSystem(("a.dds", 1));
        using var otherFs = new FakeGameFileSystem(("a.dds", 1));
        var root = AssetTreeBuilder.Build(fs, "root");
        var other = AssetTreeBuilder.Build(otherFs, "other");

        var visibility = AssetTreeVisibility.Compute(root, AssetKindFilter.All);

        Assert.False(visibility.IsVisible(other));
        Assert.False(visibility.IsVisible(Assert.Single(other.Children)));
        Assert.Throws<ArgumentNullException>(() => AssetTreeVisibility.Compute(null!, AssetKindFilter.All));
        Assert.Throws<ArgumentNullException>(() => AssetTreeVisibility.Compute(root, null!));
        Assert.Throws<ArgumentNullException>(() => visibility.IsVisible(null!));
        Assert.Throws<ArgumentNullException>(() => visibility.VisibleChildren(null!));
    }
}
