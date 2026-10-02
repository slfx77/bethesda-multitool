using BethesdaMultitool.Core.AssetBrowse;
using Slfx77.Multitool.Core.Browsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Exercises the actual app projection against original source trees and their shared export-check owner.</summary>
public sealed class AssetGalleryProjectionTests
{
    private static readonly string[] ExpectedDirectChildNames = ["a.frm", "archive.bsa", "b.nif", "z.dds"];

    /// <summary>Folder scope admits every direct leaf in builder order, folders excluded, without recursive flattening.</summary>
    [Fact]
    public async Task DirectChildren_KeepOriginalOrderAndAdmitEveryLeafKind()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("nested/hidden.dds", 1), ("z.dds", 2), ("a.frm", 3), ("b.nif", 4), ("archive.bsa", 5));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var selection = new AssetTreeSelection(browser.Current!);
        using var gallery = new AssetGalleryProjection(selection, "", AssetKindFilter.All);

        Assert.True(gallery.TryShowFolder(source.Session.Root));
        Assert.Equal(ExpectedDirectChildNames, gallery.Items.Select(node => node.Name));
        Assert.Equal(0, gallery.OtherCount);
        Assert.Equal(gallery.Items, gallery.VisibleItems);
        Assert.All(gallery.Items, node => Assert.Contains(node, source.Session.Root.Children));
        Assert.DoesNotContain(gallery.Items, node => node.Kind == AssetNodeKind.Folder);
        Assert.Same(AssetKindFilter.All, gallery.Kinds);
        Assert.Empty(selection.CaptureSelected(browser.Current!));
    }

    /// <summary>Name and relative-path search retain exact nodes and case-insensitive literal matching.</summary>
    [Fact]
    public async Task Search_MatchesNamesAndRelativePathsWithoutChangingOrder()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("Textures/Alpha.dds", 1), ("Textures/Beta.dds", 2));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        using var gallery = new AssetGalleryProjection(new AssetTreeSelection(browser.Current!), "", AssetKindFilter.All);
        Assert.True(gallery.TryShowFolder(Assert.Single(source.Session.Root.Children)));
        var first = gallery.Items[0];
        Assert.True(gallery.TrySetQuery("ALPHA"));
        Assert.Same(first, Assert.Single(gallery.VisibleItems));
        Assert.True(gallery.TrySetQuery(@"TEXTURES\"));
        Assert.Equal(gallery.Items, gallery.VisibleItems);
        Assert.True(gallery.TrySetQuery(" Alpha"));
        Assert.Empty(gallery.VisibleItems);
        Assert.True(gallery.TrySetQuery("  "));
        Assert.Same(first, gallery.VisibleItems[0]);
    }

    /// <summary>A hidden gallery row and checks outside its folder remain in the same operation-owned export capture.</summary>
    [Fact]
    public async Task Filtering_PreservesHiddenChecksAndSourceWideExport()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("shown/a.dds", 1), ("shown/b.dds", 2), ("elsewhere/c.bin", 3));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        using var gallery = new AssetGalleryProjection(selection, "", AssetKindFilter.All);
        var outside = source.Session.Root.Children[0].Children[0];
        Assert.True(gallery.TryShowFolder(source.Session.Root.Children[1]));
        var hidden = gallery.Items[0];
        Assert.True(gallery.TrySetChecked(hidden, true));
        selection.SetChecked(snapshot, outside, true);
        Assert.True(gallery.TrySetQuery("b.dds"));
        Assert.DoesNotContain(hidden, gallery.VisibleItems);
        Assert.Equal(new[] { outside, hidden }, selection.CaptureSelected(snapshot));
        Assert.True(hidden.IsChecked);
        Assert.False(gallery.TrySetChecked(outside, false));
        Assert.Equal(2, selection.Checks.SelectedCount);
    }

    /// <summary>Native checkbox feedback observes committed checks without reentering selection notifications.</summary>
    [Fact]
    public async Task CheckFeedback_PreservesTreeGalleryAndExportSynchronization()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("a.dds", 1), ("b.dds", 2));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        using var gallery = new AssetGalleryProjection(selection, "a.dds", AssetKindFilter.All);
        Assert.True(gallery.TryShowFolder(source.Session.Root));
        var first = gallery.Items[0];
        var second = gallery.Items[1];
        Assert.DoesNotContain(second, gallery.VisibleItems);
        var feedbackCount = 0;
        var batches = 0;
        selection.Checks.Changed += (_, _) => batches++;
        foreach (var node in gallery.Items)
            node.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(AssetNode.IsChecked)) return;
                feedbackCount++;
                Assert.True(gallery.TrySetChecked(node, node.IsChecked == true));
            };

        selection.SetChecked(snapshot, source.Session.Root, true);
        Assert.Equal(new[] { first, second }, selection.CaptureSelected(snapshot));
        Assert.True(source.Session.Root.IsChecked);
        Assert.True(gallery.TrySetChecked(first, false));
        Assert.Same(second, Assert.Single(selection.CaptureSelected(snapshot)));
        Assert.Null(source.Session.Root.IsChecked);
        selection.SetChecked(snapshot, source.Session.Root, false);
        Assert.Empty(selection.CaptureSelected(snapshot));
        Assert.False(source.Session.Root.IsChecked);
        Assert.True(gallery.TrySetChecked(first, true));
        Assert.Same(first, Assert.Single(selection.CaptureSelected(snapshot)));
        Assert.Null(source.Session.Root.IsChecked);
        Assert.Equal(1, selection.Checks.SelectedCount);
        Assert.Equal(4, batches);
        Assert.Equal(5, feedbackCount);
    }

    /// <summary>Feedback can arrive before a later node's mirror catches up with the atomic committed batch.</summary>
    [Fact]
    public async Task CheckFeedback_UsesCommittedStateBeforeAllMirrorsUpdate()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("a.dds", 1), ("b.dds", 2));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        using var gallery = new AssetGalleryProjection(selection, "", AssetKindFilter.All);
        Assert.True(gallery.TryShowFolder(source.Session.Root));
        var later = gallery.Items[1];
        var observed = false;
        source.Session.Root.PropertyChanged += (_, _) =>
        {
            observed = true;
            Assert.False(later.IsChecked);
            Assert.True(selection.Checks.GetState(later));
            Assert.True(gallery.TrySetChecked(later, true));
        };

        selection.SetChecked(snapshot, source.Session.Root, true);

        Assert.True(observed);
        Assert.True(later.IsChecked);
        Assert.Equal(gallery.Items, selection.CaptureSelected(snapshot));
        Assert.Equal(2, selection.Checks.SelectedCount);
    }

    /// <summary>Only equal feedback is ignored; a real nested mutation still meets the owner's reentrancy guard.</summary>
    [Fact]
    public async Task CheckFeedback_DoesNotSuppressConflictingReentrantCommands()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("a.dds", 1));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        using var gallery = new AssetGalleryProjection(selection, "", AssetKindFilter.All);
        Assert.True(gallery.TryShowFolder(source.Session.Root));
        var node = Assert.Single(gallery.Items);
        var rejected = 0;
        node.PropertyChanged += (_, _) =>
        {
            if (node.IsChecked != true) return;
            Assert.Throws<InvalidOperationException>(() => gallery.TrySetChecked(node, false));
            rejected++;
        };

        selection.SetChecked(snapshot, node, true);

        Assert.Equal(1, rejected);
        Assert.Same(node, Assert.Single(selection.CaptureSelected(snapshot)));
        Assert.True(gallery.TrySetChecked(node, false));
        Assert.Empty(selection.CaptureSelected(snapshot));
        Assert.False(node.IsChecked);
    }

    /// <summary>Search hides native row selection without losing logical focus or selecting a replacement row.</summary>
    [Fact]
    public async Task HiddenFocus_ReturnsWhenSearchClearsWithoutTouchingChecks()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("a.dds", 1), ("b.dds", 2));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var selection = new AssetTreeSelection(browser.Current!);
        using var gallery = new AssetGalleryProjection(selection, "", AssetKindFilter.All);
        Assert.True(gallery.TryShowFolder(source.Session.Root));
        var focused = gallery.Items[0];
        Assert.True(gallery.TryFocus(focused));
        Assert.True(gallery.TrySetQuery("b.dds"));
        Assert.Null(gallery.VisibleFocus);
        Assert.Same(focused, gallery.Focus);
        Assert.False(gallery.TryFocus(focused));
        Assert.True(gallery.TrySetQuery(""));
        Assert.Same(focused, gallery.VisibleFocus);
        Assert.Empty(selection.CaptureSelected(browser.Current!));
    }

    /// <summary>Repeated sibling selection preserves folder/tile references; equal labels in another folder are different occurrences.</summary>
    [Fact]
    public async Task FolderNavigation_RetainsSameFolderAndDistinguishesDuplicateLabels()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("first/same.dds", 1), ("second/same.dds", 2));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        using var gallery = new AssetGalleryProjection(new AssetTreeSelection(browser.Current!), "same", AssetKindFilter.All);
        var first = source.Session.Root.Children[0];
        Assert.True(gallery.TryShowFolder(first));
        var items = gallery.Items;
        var focused = Assert.Single(items);
        Assert.True(gallery.TryFocus(focused));
        Assert.False(gallery.TryShowFolder(first));
        Assert.Same(items, gallery.Items);
        Assert.Same(focused, gallery.Focus);
        Assert.True(gallery.TryShowFolder(source.Session.Root.Children[1]));
        Assert.Null(gallery.Focus);
        var replacement = Assert.Single(gallery.VisibleItems);
        Assert.Equal(focused.Name, replacement.Name);
        Assert.NotSame(focused, replacement);
        Assert.False(gallery.TrySetChecked(focused, true));
        Assert.False(replacement.IsChecked);
    }

    /// <summary>A same-path source replacement rejects obsolete commands even while the original filesystem remains leased.</summary>
    [Fact]
    public async Task SourceReplacement_RejectsRetiredRowsAndPreservesExactNewIdentity()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("same.dds", 1));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var firstSnapshot = browser.Current!;
        await using var oldLease = firstSnapshot.AcquireLease();
        using var oldGallery = new AssetGalleryProjection(new AssetTreeSelection(firstSnapshot), "same", AssetKindFilter.All);
        Assert.True(oldGallery.TryShowFolder(source.Session.Root));
        var oldNode = Assert.Single(oldGallery.Items);
        Assert.True(oldGallery.TryFocus(oldNode));
        var replacement = CreateSource(("same.dds", 2));
        await browser.ReplaceAsync(replacement, TestContext.Current.CancellationToken);
        using var currentGallery = new AssetGalleryProjection(new AssetTreeSelection(browser.Current!), "same", AssetKindFilter.All);
        Assert.True(currentGallery.TryShowFolder(replacement.Session.Root));
        Assert.False(oldGallery.TrySetChecked(oldNode, true));
        Assert.False(oldGallery.TrySetChecked(oldNode, false));
        Assert.False(oldGallery.TryFocus(oldNode));
        Assert.False(oldGallery.TrySetQuery(""));
        Assert.False(oldGallery.TrySetKinds(AssetKindFilter.All));
        Assert.False(currentGallery.TrySetChecked(oldNode, true));
        Assert.False(currentGallery.TrySetChecked(oldNode, false));
        Assert.NotSame(oldNode, Assert.Single(currentGallery.VisibleItems));
        Assert.False(currentGallery.Items[0].IsChecked);
        Assert.Null(currentGallery.Focus);
    }

    /// <summary>Matching generation numbers and labels from another browser do not admit a foreign folder or row.</summary>
    [Fact]
    public async Task ForeignSource_DoesNotChangeTheCurrentProjection()
    {
        await using var browser = new BrowserSession();
        await using var foreignBrowser = new BrowserSession();
        var source = CreateSource(("same.dds", 1));
        var foreign = CreateSource(("same.dds", 1));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        await foreignBrowser.ReplaceAsync(foreign, TestContext.Current.CancellationToken);
        Assert.Equal(browser.Current!.Generation, foreignBrowser.Current!.Generation);
        using var gallery = new AssetGalleryProjection(new AssetTreeSelection(browser.Current), "", AssetKindFilter.All);
        Assert.True(gallery.TryShowFolder(source.Session.Root));
        Assert.False(gallery.TryShowFolder(foreign.Session.Root));
        Assert.False(gallery.TryFocus(foreign.Session.Root.Children[0]));
        Assert.False(gallery.TrySetChecked(foreign.Session.Root.Children[0], true));
        Assert.False(gallery.TrySetChecked(foreign.Session.Root.Children[0], false));
        Assert.Same(source.Session.Root, gallery.Folder);
        Assert.Same(source.Session.Root.Children[0], Assert.Single(gallery.VisibleItems));
    }

    /// <summary>Disposing presentation prevents delayed commands without clearing source checks or retiring its filesystem.</summary>
    [Fact]
    public async Task Disposal_ClosesPresentationAdmissionAndLeavesOperationChecksIntact()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("same.dds", 1));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        using var gallery = new AssetGalleryProjection(selection, "", AssetKindFilter.All);
        Assert.True(gallery.TryShowFolder(source.Session.Root));
        var node = Assert.Single(gallery.Items);
        Assert.True(gallery.TrySetChecked(node, true));
        gallery.Dispose();
        Assert.Empty(gallery.Items);
        Assert.Empty(gallery.VisibleItems);
        Assert.Null(gallery.Folder);
        Assert.Equal(0, gallery.OtherCount);
        Assert.False(gallery.TrySetChecked(node, false));
        Assert.False(gallery.TrySetChecked(node, true));
        Assert.False(gallery.TryShowFolder(source.Session.Root));
        Assert.False(gallery.TrySetKinds(AssetKindFilter.All.With(AssetNodeKind.Texture, false)));
        Assert.Same(node, Assert.Single(selection.CaptureSelected(snapshot)));
        Assert.False(snapshot.CancellationToken.IsCancellationRequested);
    }

    /// <summary>The type filter hides rows like search does: members, checks and the retained focus are untouched.</summary>
    [Fact]
    public async Task TypeFilter_HidesRowsWithoutTouchingChecksOrFocus()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("a.frm", 1), ("z.dds", 2));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        using var gallery = new AssetGalleryProjection(selection, "", AssetKindFilter.All);
        Assert.True(gallery.TryShowFolder(source.Session.Root));
        var sprite = gallery.Items[0];
        var texture = gallery.Items[1];
        Assert.Equal(AssetNodeKind.Sprite, sprite.Kind);
        Assert.True(gallery.TryFocus(sprite));
        Assert.True(gallery.TrySetChecked(sprite, true));
        var noSprites = AssetKindFilter.All.With(AssetNodeKind.Sprite, false);

        Assert.True(gallery.TrySetKinds(noSprites));

        Assert.Same(noSprites, gallery.Kinds);
        Assert.Same(texture, Assert.Single(gallery.VisibleItems));
        Assert.Equal(2, gallery.Items.Count);
        Assert.Equal(1, gallery.OtherCount);
        Assert.Same(sprite, gallery.Focus);
        Assert.Null(gallery.VisibleFocus);
        Assert.False(gallery.TryFocus(sprite));
        Assert.Same(sprite, Assert.Single(selection.CaptureSelected(snapshot)));
        Assert.True(sprite.IsChecked);
        Assert.True(gallery.TrySetChecked(sprite, false));
        Assert.Empty(selection.CaptureSelected(snapshot));

        Assert.True(gallery.TrySetKinds(AssetKindFilter.All));
        Assert.Equal(gallery.Items, gallery.VisibleItems);
        Assert.Same(sprite, gallery.VisibleFocus);
        Assert.Equal(0, gallery.OtherCount);
    }

    /// <summary>A type filter set on one folder still applies after navigating to another.</summary>
    [Fact]
    public async Task TypeFilter_PersistsAcrossFolders()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("first/a.frm", 1), ("first/b.dds", 2), ("second/c.frm", 3), ("second/d.dds", 4));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        using var gallery = new AssetGalleryProjection(new AssetTreeSelection(browser.Current!), "", AssetKindFilter.All);
        var noSprites = AssetKindFilter.All.With(AssetNodeKind.Sprite, false);
        Assert.True(gallery.TryShowFolder(source.Session.Root.Children[0]));
        Assert.True(gallery.TrySetKinds(noSprites));
        Assert.Equal(new[] { "b.dds" }, gallery.VisibleItems.Select(node => node.Name));

        Assert.True(gallery.TryShowFolder(source.Session.Root.Children[1]));

        Assert.Same(noSprites, gallery.Kinds);
        Assert.Equal(new[] { "c.frm", "d.dds" }, gallery.Items.Select(node => node.Name));
        Assert.Equal(new[] { "d.dds" }, gallery.VisibleItems.Select(node => node.Name));
        Assert.Equal(1, gallery.OtherCount);
    }

    /// <summary>The constructor's filter applies to the first folder, combines with the query as AND, and dies with the projection.</summary>
    [Fact]
    public async Task TypeFilter_CombinesWithTheQueryAndIsAppliedFromConstruction()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("Alpha.frm", 1), ("Alpha.dds", 2), ("Beta.dds", 3));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var noSprites = AssetKindFilter.All.With(AssetNodeKind.Sprite, false);
        using var gallery = new AssetGalleryProjection(new AssetTreeSelection(browser.Current!), "alpha", noSprites);

        Assert.True(gallery.TryShowFolder(source.Session.Root));

        Assert.Equal(new[] { "Alpha.dds", "Alpha.frm", "Beta.dds" }, gallery.Items.Select(node => node.Name));
        Assert.Equal(new[] { "Alpha.dds" }, gallery.VisibleItems.Select(node => node.Name));
        Assert.Equal(1, gallery.OtherCount);
        Assert.True(gallery.TrySetQuery(""));
        Assert.Equal(new[] { "Alpha.dds", "Beta.dds" }, gallery.VisibleItems.Select(node => node.Name));
        Assert.True(gallery.TrySetKinds(AssetKindFilter.All));
        Assert.Equal(gallery.Items, gallery.VisibleItems);
        Assert.True(gallery.TrySetQuery("beta"));
        Assert.True(gallery.TrySetKinds(noSprites));
        Assert.Equal(new[] { "Beta.dds" }, gallery.VisibleItems.Select(node => node.Name));

        gallery.Dispose();
        Assert.False(gallery.TrySetKinds(AssetKindFilter.All));
        Assert.False(gallery.TrySetQuery(""));
    }

    /// <summary>Every leaf kind is a tile, and the "other" count is only what the type filter hides, never what the query hides.</summary>
    [Fact]
    public async Task EveryLeafKindIsAnItem_AndOtherCountIsOnlyTheTypeFilter()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(("nested/hidden.dds", 1), ("z.dds", 2), ("a.frm", 3), ("b.nif", 4), ("archive.bsa", 5), ("notes.txt", 6));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        using var gallery = new AssetGalleryProjection(new AssetTreeSelection(browser.Current!), "", AssetKindFilter.All);

        Assert.True(gallery.TryShowFolder(source.Session.Root));

        Assert.Equal(new[] { "a.frm", "archive.bsa", "b.nif", "notes.txt", "z.dds" }, gallery.Items.Select(node => node.Name));
        Assert.Contains(gallery.Items, node => node.Kind == AssetNodeKind.Model && !AssetThumbnailSource.CanRender(node));
        Assert.Contains(gallery.Items, node => node.Kind == AssetNodeKind.Archive && !AssetThumbnailSource.CanRender(node));
        Assert.Equal(0, gallery.OtherCount);

        Assert.True(gallery.TrySetKinds(AssetKindFilter.All.With(AssetNodeKind.Model, false).With(AssetNodeKind.Archive, false)));
        Assert.Equal(2, gallery.OtherCount);
        Assert.Equal(new[] { "a.frm", "notes.txt", "z.dds" }, gallery.VisibleItems.Select(node => node.Name));

        Assert.True(gallery.TrySetQuery("z"));
        Assert.Equal(new[] { "z.dds" }, gallery.VisibleItems.Select(node => node.Name));
        Assert.Equal(2, gallery.OtherCount);
    }

    /// <summary>Transfers the existing in-memory VFS through the real builder and source ownership seam.</summary>
    /// <param name="entries">Bounded synthetic paths and lengths; no private payloads.</param>
    /// <returns>A fresh owned Bethesda source and exact completed tree.</returns>
    private static BethesdaBrowseSource CreateSource(params (string Path, long Size)[] entries)
    {
        var filesystem = new FakeGameFileSystem(entries);
        return new BethesdaBrowseSource(new AssetBrowseSession(filesystem, "gallery", "gallery", AssetTreeBuilder.Build(filesystem, "gallery")));
    }
}
