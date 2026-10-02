using BethesdaMultitool.Core.AssetBrowse;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Pins the immutable exclusion set behind the asset browser's type filter.</summary>
public sealed class AssetKindFilterTests
{
    /// <summary>The display order the census and the flyout share, written out so a reordering is a visible diff.</summary>
    private static readonly AssetNodeKind[] ExpectedDisplayOrder =
    [
        AssetNodeKind.Texture, AssetNodeKind.Sprite, AssetNodeKind.Model, AssetNodeKind.Audio, AssetNodeKind.Video,
        AssetNodeKind.Map, AssetNodeKind.Text, AssetNodeKind.Plugin, AssetNodeKind.Archive, AssetNodeKind.Save,
        AssetNodeKind.Raw
    ];

    /// <summary>The initial filter admits every kind, Raw included, and reports itself inactive.</summary>
    [Fact]
    public void All_AdmitsEveryKindAndIsInactive()
    {
        var all = AssetKindFilter.All;

        Assert.False(all.IsActive);
        Assert.Empty(all.ExcludedKinds);
        Assert.All(Enum.GetValues<AssetNodeKind>(), kind => Assert.True(all.Admits(kind), kind.ToString()));
        Assert.True(all.Admits(AssetNodeKind.Raw));
    }

    /// <summary>Excluding one kind hides exactly that kind; including it again is value-equal to the initial filter.</summary>
    [Fact]
    public void With_ExcludesExactlyOneKindAndRoundTripsToAll()
    {
        var noVideo = AssetKindFilter.All.With(AssetNodeKind.Video, false);

        Assert.True(noVideo.IsActive);
        Assert.Equal(new[] { AssetNodeKind.Video }, noVideo.ExcludedKinds);
        Assert.False(noVideo.Admits(AssetNodeKind.Video));
        Assert.All(Enum.GetValues<AssetNodeKind>().Where(kind => kind != AssetNodeKind.Video),
            kind => Assert.True(noVideo.Admits(kind), kind.ToString()));
        Assert.NotEqual(AssetKindFilter.All, noVideo);

        var restored = noVideo.With(AssetNodeKind.Video, true);

        Assert.Equal(AssetKindFilter.All, restored);
        Assert.Equal(AssetKindFilter.All.GetHashCode(), restored.GetHashCode());
        Assert.False(restored.IsActive);
    }

    /// <summary>Folders are never filterable, and the filterable list is every other real enum member in display order.</summary>
    [Fact]
    public void FilterableKinds_CoverEveryNonFolderKindInDisplayOrder()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetKindFilter.All.With(AssetNodeKind.Folder, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetKindFilter.All.With(AssetNodeKind.Folder, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetKindFilter.All.With((AssetNodeKind)999, false));

        Assert.Equal(ExpectedDisplayOrder, AssetKindFilter.FilterableKinds);
        Assert.DoesNotContain(AssetNodeKind.Folder, AssetKindFilter.FilterableKinds);
        Assert.Equal(
            Enum.GetValues<AssetNodeKind>().Where(kind => kind != AssetNodeKind.Folder).Order(),
            AssetKindFilter.FilterableKinds.Order());
        Assert.Equal(AssetKindFilter.FilterableKinds.Count, AssetKindFilter.FilterableKinds.Distinct().Count());
    }

    /// <summary>Excluding everything still admits folders, by kind and by node, so the tree keeps its root.</summary>
    [Fact]
    public void WithAll_False_ExcludesEveryLeafKindButStillAdmitsFolders()
    {
        var none = AssetKindFilter.All.WithAll(false);

        Assert.True(none.IsActive);
        Assert.Equal(AssetKindFilter.FilterableKinds, none.ExcludedKinds);
        Assert.All(AssetKindFilter.FilterableKinds, kind => Assert.False(none.Admits(kind), kind.ToString()));
        Assert.True(none.Admits(AssetNodeKind.Folder));

        using var fs = new FakeGameFileSystem(("folder\\a.dds", 1));
        var root = AssetTreeBuilder.Build(fs, "root");
        var folder = Assert.Single(root.Children);
        Assert.Equal(AssetNodeKind.Folder, folder.Kind);
        Assert.True(none.Admits(root));
        Assert.True(none.Admits(folder));
        Assert.False(none.Admits(Assert.Single(folder.Children)));

        Assert.Equal(AssetKindFilter.All, none.WithAll(true));
    }

    /// <summary>A present kind the user left unchecked becomes excluded; a present kind checked again is admitted.</summary>
    [Fact]
    public void WithIncluded_ExcludesPresentKindsThatAreNotIncluded()
    {
        var present = new[] { AssetNodeKind.Texture, AssetNodeKind.Audio, AssetNodeKind.Video };

        var filtered = AssetKindFilter.All.WithIncluded(present, [AssetNodeKind.Texture, AssetNodeKind.Video]);

        Assert.Equal(new[] { AssetNodeKind.Audio }, filtered.ExcludedKinds);
        Assert.Equal(AssetKindFilter.All.With(AssetNodeKind.Audio, false), filtered);

        var readmitted = filtered.WithIncluded(present, present);

        Assert.Equal(AssetKindFilter.All, readmitted);
    }

    /// <summary>A kind excluded in an earlier source stays excluded while a later source lists no leaf of that kind.</summary>
    [Fact]
    public void WithIncluded_KeepsAnExclusionForAKindTheCurrentSourceDoesNotList()
    {
        var earlier = AssetKindFilter.All.With(AssetNodeKind.Model, false);

        var later = earlier.WithIncluded([AssetNodeKind.Texture, AssetNodeKind.Audio], [AssetNodeKind.Texture]);

        Assert.Equal(new[] { AssetNodeKind.Model, AssetNodeKind.Audio }, later.ExcludedKinds);
        Assert.False(later.Admits(AssetNodeKind.Model));
        Assert.False(later.Admits(AssetNodeKind.Audio));
        Assert.True(later.Admits(AssetNodeKind.Texture));
        Assert.Throws<ArgumentOutOfRangeException>(() => later.WithIncluded([AssetNodeKind.Folder], []));
    }
}
