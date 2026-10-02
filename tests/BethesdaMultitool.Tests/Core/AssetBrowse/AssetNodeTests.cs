using BethesdaMultitool.Core.AssetBrowse;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Pins immutable asset topology and kind-driven preview/extraction capabilities.</summary>
public sealed class AssetNodeTests
{
    /// <summary>Creates an unattached immutable folder for topology assertions.</summary>
    private static AssetNode NewFolder(string name)
    {
        return new AssetNode(name, name, AssetNodeKind.Folder, 0);
    }

    /// <summary>Creates an unattached leaf with a chosen classification.</summary>
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

    /// <summary>Builder parent/child links and initial display values do not depend on a native control.</summary>
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

    /// <summary>Every nonfolder remains extractable even when no preview decoder exists.</summary>
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

}
