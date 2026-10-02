using BethesdaMultitool.Core.AssetBrowse;
using Slfx77.Multitool.Core.Browsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Pins how asset kinds become Shared filter options and back, and the retained-decision behavior BMT relies on.</summary>
public sealed class AssetKindLabelsTests
{
    /// <summary>Options follow the census order and carry the census counts, Shared plural keys and the save fallback.</summary>
    [Fact]
    public void FilterOptions_FollowTheCensusWithSharedKeysAndTheSaveFallback()
    {
        var options = AssetKindLabels.CreateFilterOptions(
            [new AssetKindCount(AssetNodeKind.Texture, 3), new AssetKindCount(AssetNodeKind.Save, 1), new AssetKindCount(AssetNodeKind.Raw, 2)]);

        Assert.Equal(["Texture", "Save", "Raw"], options.Select(option => option.Id));
        Assert.Equal(["Browser.AssetType.Texture", "AssetTypeFilter_Saves", "Browser.AssetType.Other"], options.Select(option => option.LabelKey));
        Assert.Equal([3L, 1L, 2L], options.Select(option => option.Count));
    }

    /// <summary>Every filterable kind has an identity and label, the identities round-trip, and folders are refused.</summary>
    [Fact]
    public void EveryFilterableKind_RoundTripsAndFoldersAreRefused()
    {
        var ids = AssetKindFilter.FilterableKinds.Select(AssetKindLabels.FilterId).ToArray();

        Assert.Equal(AssetKindFilter.FilterableKinds, AssetKindLabels.SelectedKinds(ids));
        Assert.All(AssetKindFilter.FilterableKinds, kind => Assert.False(string.IsNullOrWhiteSpace(AssetKindLabels.FilterLabelKey(kind))));
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetKindLabels.FilterId(AssetNodeKind.Folder));
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetKindLabels.FilterLabelKey(AssetNodeKind.Folder));
        Assert.Equal("AssetKind_Folder", AssetKindLabels.CaptionKey(AssetNodeKind.Folder));
        Assert.Equal("AssetKind_Sprite", AssetKindLabels.CaptionKey(AssetNodeKind.Sprite));
    }

    /// <summary>Unknown identities name nothing and the result follows the display order, not the input order.</summary>
    [Fact]
    public void SelectedKinds_IgnoresUnknownIdsAndUsesDisplayOrder()
    {
        Assert.Equal([AssetNodeKind.Texture, AssetNodeKind.Video], AssetKindLabels.SelectedKinds(["Video", "bogus", "Texture", "Folder"]));
        Assert.Empty(AssetKindLabels.SelectedKinds([]));
    }

    /// <summary>
    ///     Through the real Shared selection: a kind unchecked in one source stays unchecked when the next source
    ///     also contains it, and a kind new to the next source arrives checked.
    /// </summary>
    [Fact]
    public void SharedSelection_RetainsSurvivingDecisionsAndChecksNewKinds()
    {
        var selection = new BrowserTypeFilterSelection();
        selection.ReplaceOptions(AssetKindLabels.CreateFilterOptions(
            [new AssetKindCount(AssetNodeKind.Texture, 2), new AssetKindCount(AssetNodeKind.Video, 1)]));
        selection.SetSelected(AssetKindLabels.FilterId(AssetNodeKind.Video), false);

        Assert.Equal([AssetNodeKind.Texture], AssetKindLabels.SelectedKinds(selection.SelectedIds));

        selection.ReplaceOptions(AssetKindLabels.CreateFilterOptions(
            [new AssetKindCount(AssetNodeKind.Model, 4), new AssetKindCount(AssetNodeKind.Video, 7)]));

        Assert.Equal([AssetNodeKind.Model], AssetKindLabels.SelectedKinds(selection.SelectedIds));
        var present = selection.Options.Select(option => Enum.Parse<AssetNodeKind>(option.Id)).ToArray();
        var filter = AssetKindFilter.All.WithIncluded(present, AssetKindLabels.SelectedKinds(selection.SelectedIds));
        Assert.True(filter.Admits(AssetNodeKind.Model));
        Assert.False(filter.Admits(AssetNodeKind.Video));
    }
}
