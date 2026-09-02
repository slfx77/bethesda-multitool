using BethesdaMultitool.Core.AssetBrowse;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Pins the <see cref="AssetNode" /> contract: tristate check flooding down and mixed-state
///     recomputation up (PropertyChanged raised only where the value changed), kind-driven
///     capability predicates, and <see cref="AssetNode.CheckedFiles" />.
/// </summary>
public sealed class AssetNodeTests
{
    private static AssetNode NewFolder(string name)
    {
        return new AssetNode(name, name, AssetNodeKind.Folder, 0);
    }

    private static AssetNode NewLeaf(string name, AssetNodeKind kind, long size = 1)
    {
        return new AssetNode(name, name, kind, size);
    }

    /// <summary>root → { sub → { a.dds, b.nif }, c.txt } — all initially unchecked.</summary>
    private static (AssetNode Root, AssetNode Sub, AssetNode A, AssetNode B, AssetNode C) NewTree()
    {
        var root = NewFolder("root");
        var sub = NewFolder("sub");
        var a = NewLeaf("a.dds", AssetNodeKind.Texture);
        var b = NewLeaf("b.nif", AssetNodeKind.Model);
        var c = NewLeaf("c.txt", AssetNodeKind.Text);
        root.AddChild(sub);
        root.AddChild(c);
        sub.AddChild(a);
        sub.AddChild(b);
        return (root, sub, a, b, c);
    }

    /// <summary>Counts IsChecked change notifications per node (and pins the property name).</summary>
    private static Dictionary<AssetNode, int> TrackChanges(params AssetNode[] nodes)
    {
        var counts = nodes.ToDictionary(n => n, _ => 0);
        foreach (var node in nodes)
        {
            node.PropertyChanged += (sender, args) =>
            {
                Assert.Equal(nameof(AssetNode.IsChecked), args.PropertyName);
                counts[(AssetNode)sender!]++;
            };
        }

        return counts;
    }

    [Fact]
    public void NewTree_IsWiredAndUnchecked()
    {
        var (root, sub, a, b, c) = NewTree();

        Assert.Null(root.Parent);
        Assert.Same(root, sub.Parent);
        Assert.Same(sub, a.Parent);
        Assert.Same(sub, b.Parent);
        Assert.Same(root, c.Parent);
        Assert.Equal(new[] { sub, c }, root.Children);
        Assert.Equal(new[] { a, b }, sub.Children);
        Assert.All(new[] { root, sub, a, b, c }, n => Assert.False(n.IsChecked));
    }

    [Fact]
    public void CheckFolder_FloodsSubtree_RaisingOncePerNode()
    {
        var (root, sub, a, b, c) = NewTree();
        var counts = TrackChanges(root, sub, a, b, c);

        root.IsChecked = true;

        Assert.All(new[] { root, sub, a, b, c }, n => Assert.True(n.IsChecked));
        Assert.All(counts.Values, v => Assert.Equal(1, v));

        // Re-assigning the same value changes nothing and must raise nothing.
        root.IsChecked = true;
        Assert.All(counts.Values, v => Assert.Equal(1, v));
    }

    [Fact]
    public void CheckLeaf_RecomputesAncestorsToMixed()
    {
        var (root, sub, a, b, c) = NewTree();
        var counts = TrackChanges(root, sub, a, b, c);

        a.IsChecked = true;

        Assert.True(a.IsChecked);
        Assert.False(b.IsChecked);
        Assert.Null(sub.IsChecked);
        Assert.Null(root.IsChecked);
        Assert.False(c.IsChecked);
        Assert.Equal(1, counts[a]);
        Assert.Equal(1, counts[sub]);
        Assert.Equal(1, counts[root]);
        Assert.Equal(0, counts[b]);
        Assert.Equal(0, counts[c]);
    }

    [Fact]
    public void CheckingEveryLeaf_TurnsAncestorsTrue_WithoutRedundantEvents()
    {
        var (root, sub, a, b, c) = NewTree();
        var counts = TrackChanges(root, sub, a, b, c);

        a.IsChecked = true; // sub and root go mixed
        b.IsChecked = true; // sub goes true; root stays mixed (c unchecked) so no root event
        Assert.True(sub.IsChecked);
        Assert.Null(root.IsChecked);
        Assert.Equal(2, counts[sub]);  // false → null → true
        Assert.Equal(1, counts[root]); // false → null only

        c.IsChecked = true;
        Assert.True(root.IsChecked);
        Assert.Equal(2, counts[root]); // null → true
    }

    [Fact]
    public void UncheckingFolder_ClearsItsSubtree_AndRemixesAncestors()
    {
        var (root, sub, a, b, c) = NewTree();
        root.IsChecked = true;

        sub.IsChecked = false;

        Assert.False(a.IsChecked);
        Assert.False(b.IsChecked);
        Assert.False(sub.IsChecked);
        Assert.True(c.IsChecked);
        Assert.Null(root.IsChecked); // c is still checked → mixed
    }

    [Fact]
    public void AssigningNull_IsCoercedToFalse()
    {
        var (_, _, a, _, _) = NewTree();
        a.IsChecked = true;

        a.IsChecked = null;

        Assert.False(a.IsChecked);
    }

    [Theory]
    [InlineData(AssetNodeKind.Folder, true, false, false)]
    [InlineData(AssetNodeKind.Archive, true, false, true)]
    [InlineData(AssetNodeKind.Plugin, false, false, true)]
    [InlineData(AssetNodeKind.Texture, false, true, true)]
    [InlineData(AssetNodeKind.Model, false, true, true)]
    [InlineData(AssetNodeKind.Audio, false, true, true)]
    [InlineData(AssetNodeKind.Video, false, true, true)]
    [InlineData(AssetNodeKind.Sprite, false, true, true)]
    [InlineData(AssetNodeKind.Map, false, true, true)]
    [InlineData(AssetNodeKind.Text, false, true, true)]
    [InlineData(AssetNodeKind.Save, false, false, true)]
    [InlineData(AssetNodeKind.Raw, false, false, true)]
    public void CapabilityPredicates_FollowKind(
        AssetNodeKind kind, bool expandable, bool previewable, bool extractable)
    {
        var node = NewLeaf("n", kind);

        Assert.Equal(expandable, node.IsExpandable);
        Assert.Equal(previewable, node.IsPreviewable);
        Assert.Equal(extractable, node.IsExtractable);
    }

    [Fact]
    public void CheckedFiles_YieldsCheckedNonFoldersInTreeOrder()
    {
        var (root, sub, a, b, c) = NewTree();

        sub.IsChecked = true;
        Assert.Equal(new[] { a, b }, root.CheckedFiles());

        c.IsChecked = true;
        Assert.Equal(new[] { a, b, c }, root.CheckedFiles());

        // Folders never appear, even when fully checked themselves.
        root.IsChecked = true;
        Assert.Equal(new[] { a, b, c }, root.CheckedFiles());
    }

    [Fact]
    public void CheckedFiles_OnCheckedLeaf_YieldsSelf()
    {
        var (_, _, a, b, _) = NewTree();
        a.IsChecked = true;

        Assert.Equal(new[] { a }, a.CheckedFiles());
        Assert.Empty(b.CheckedFiles());
    }

    [Fact]
    public void CheckedFiles_IncludesCheckedArchiveLeaves()
    {
        var root = NewFolder("root");
        var bsa = NewLeaf("stuff.bsa", AssetNodeKind.Archive, 100);
        root.AddChild(bsa);

        bsa.IsChecked = true;

        Assert.Equal(new[] { bsa }, root.CheckedFiles());
    }
}
