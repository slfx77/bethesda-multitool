using BethesdaMultitool.Core.AssetBrowse;
using Slfx77.Multitool.Core.Browsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Exercises the actual Bethesda tree/source adapter rather than duplicating shared propagation algorithms.</summary>
public sealed class AssetTreeSelectionTests
{
    /// <summary>The immutable expected preorder for the first-hit path identity fixture.</summary>
    private static readonly string[] FirstHitPaths = [@"other\First.DDS", @"Textures\First.DDS", "nested.bsa", "unknown.raw"];

    /// <summary>The completed builder tree preserves first-hit path spelling, distinct folders and opaque archive leaves.</summary>
    [Fact]
    public async Task BuilderIdentity_PreservesFirstCaseInsensitivePathAndOpaqueArchives()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(new FakeGameFileSystem(("Textures/First.DDS", 7),
            (@"textures\first.dds", 19), (@"other\First.DDS", 3), ("nested.bsa", 11), ("unknown.raw", 2)));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        selection.SetChecked(snapshot, source.Session.Root, true);

        var files = selection.CaptureSelected(snapshot);
        Assert.Equal(4, selection.FileCount);
        Assert.Equal(FirstHitPaths, files.Select(node => node.VirtualPath));
        Assert.Equal(7, files[1].Size);
        Assert.Empty(files[2].Children);
        Assert.Equal(AssetNodeKind.Archive, files[2].Kind);
        Assert.False(files[3].IsPreviewable);
        Assert.All(selection.Checks.CaptureSelected(snapshot), identity => Assert.Equal(source.Id, identity.SourceId));
    }

    /// <summary>Overlapping folder/leaf commands produce one coherent final mirror batch and one export per leaf.</summary>
    [Fact]
    public async Task FolderExclusion_DeduplicatesOverlapsAndMirrorsOnlyFinalChanges()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(new FakeGameFileSystem((@"sub\a.dds", 1), (@"sub\b.nif", 2), ("c.txt", 3)));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        var root = source.Session.Root;
        var folder = root.Children[0];
        var first = folder.Children[0];
        var second = folder.Children[1];
        var notifications = new Dictionary<AssetNode, int>();
        foreach (var node in new[] { root, folder, first, second, root.Children[1] })
            node.PropertyChanged += (_, args) =>
            {
                Assert.Equal(nameof(AssetNode.IsChecked), args.PropertyName);
                notifications[node] = notifications.GetValueOrDefault(node) + 1;
            };
        var batches = 0;
        selection.Checks.Changed += (_, args) =>
        {
            batches++;
            Assert.Equal(2, args.SelectedCount);
            Assert.Null(root.IsChecked);
            Assert.Null(folder.IsChecked);
            Assert.True(first.IsChecked);
            Assert.False(second.IsChecked);
            Assert.True(root.Children[1].IsChecked);
        };
        selection.Checks.Apply(snapshot,
        [new(root, true), new(folder, true), new(first, true), new(first, true), new(second, false)]);

        Assert.Equal(1, batches);
        Assert.Equal(new[] { first, root.Children[1] }, selection.CaptureSelected(snapshot));
        Assert.Equal(4, notifications.Count);
        Assert.All(notifications.Values, count => Assert.Equal(1, count));
        selection.SetChecked(snapshot, first, true);
        Assert.Equal(1, batches);
    }

    /// <summary>Checks outside a currently presented folder survive other commands, while Clear includes every hidden leaf.</summary>
    [Fact]
    public async Task ClearAndNullableCommand_PreserveTheBethesdaBooleanPolicy()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(new FakeGameFileSystem((@"shown\a.dds", 1), (@"hidden\b.dds", 2)));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        var hidden = source.Session.Root.Children[0];
        var shown = source.Session.Root.Children[1];
        selection.SetChecked(snapshot, hidden, true);
        selection.SetChecked(snapshot, shown, true);
        selection.SetChecked(snapshot, shown.Children[0], null);
        Assert.False(shown.Children[0].IsChecked);
        Assert.Same(hidden.Children[0], Assert.Single(selection.CaptureSelected(snapshot)));
        selection.Checks.Clear(snapshot);
        Assert.Equal(0, selection.Checks.SelectedCount);
        Assert.False(hidden.IsChecked);
        Assert.False(hidden.Children[0].IsChecked);
        Assert.False(source.Session.Root.IsChecked);
    }

    /// <summary>Same-path replacement creates new exact nodes; both stale checks and stale preview admission are rejected.</summary>
    [Fact]
    public async Task Replacement_ResetsChecksAndRejectsRetiredAndForeignRows()
    {
        await using var browser = new BrowserSession();
        await using var otherBrowser = new BrowserSession();
        var first = CreateSource(new FakeGameFileSystem(("same.dds", 3)));
        await browser.ReplaceAsync(first, TestContext.Current.CancellationToken);
        var oldSnapshot = browser.Current!;
        var oldSelection = new AssetTreeSelection(oldSnapshot);
        var oldNode = first.Session.Root.Children[0];
        oldSelection.SetChecked(oldSnapshot, oldNode, true);
        await otherBrowser.ReplaceAsync(CreateSource(new FakeGameFileSystem(("same.dds", 3))), TestContext.Current.CancellationToken);
        Assert.Equal(oldSnapshot.Generation, otherBrowser.Current!.Generation);
        Assert.False(oldSelection.ContainsCurrentNode(otherBrowser.Current, oldNode));

        var replacement = CreateSource(new FakeGameFileSystem(("same.dds", 9)));
        await browser.ReplaceAsync(replacement, TestContext.Current.CancellationToken);
        var current = browser.Current!;
        var selection = new AssetTreeSelection(current);
        var newNode = replacement.Session.Root.Children[0];
        Assert.False(selection.ContainsCurrentNode(current, oldNode));
        Assert.False(oldSelection.ContainsCurrentNode(oldSnapshot, oldNode));
        Assert.True(selection.ContainsCurrentNode(current, newNode));
        Assert.False(newNode.IsChecked);
        Assert.Empty(selection.CaptureSelected(current));
        Assert.Throws<ArgumentException>(() => selection.SetChecked(current, oldNode, true));
        Assert.Throws<InvalidOperationException>(() => oldSelection.SetChecked(oldSnapshot, oldNode, false));
        Assert.Throws<InvalidOperationException>(() => oldSelection.CaptureSelected(current));
    }

    /// <summary>A captured export keeps the original filesystem leased across replacement and uses original bytes and paths.</summary>
    [Fact]
    public async Task CapturedExport_RetainsOldSourceAndIgnoresLaterChecks()
    {
        var output = Path.Combine(Path.GetTempPath(), "bmt-selection-" + Guid.NewGuid().ToString("N"));
        await using var browser = new BrowserSession();
        var filesystem = new FakeGameFileSystem((@"sub\one.bin", 7), ("skip.bin", 1));
        var source = CreateSource(filesystem);
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        await using var lease = snapshot.AcquireLease();
        selection.SetChecked(snapshot, source.Session.Root.Children[0], true);
        var captured = selection.CaptureSelected(snapshot);
        selection.Checks.Clear(snapshot);
        await browser.ReplaceAsync(CreateSource(new FakeGameFileSystem((@"sub\one.bin", 19))), TestContext.Current.CancellationToken);
        Assert.Equal(0, filesystem.DisposeCount);
        try
        {
            var results = await AssetExportService.ExportAsync(source.Session.FileSystem,
                captured.Select(node => node.VirtualPath), output, AssetExportMode.Original,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(Assert.Single(results).Success);
            Assert.Equal(new byte[7], await File.ReadAllBytesAsync(Path.Combine(output, "sub", "one.bin"), TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
            await lease.DisposeAsync();
            Assert.Equal(1, filesystem.DisposeCount);
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
    }

    /// <summary>The adapter rejects a conflicting catalog instead of silently changing the builder's first-hit policy.</summary>
    [Fact]
    public async Task ConflictingIdentity_IsRejectedWhenTopologyBypassesBuilderDeduplication()
    {
        var root = new AssetNode("root", "", AssetNodeKind.Folder, 0);
        root.AddChild(new AssetNode("one.dds", "one.dds", AssetNodeKind.Texture, 1));
        root.AddChild(new AssetNode("ONE.DDS", "ONE.DDS", AssetNodeKind.Texture, 2));
        await using var browser = new BrowserSession();
        await browser.ReplaceAsync(new BethesdaBrowseSource(new AssetBrowseSession(new FakeGameFileSystem(), "test", "test", root)),
            TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => new AssetTreeSelection(browser.Current!));
    }

    /// <summary>An unsupported late-discovered node cannot enter the captured fixed topology through a native event.</summary>
    [Fact]
    public async Task LateDiscovery_IsNotSilentlyAddedToChecksOrExport()
    {
        await using var browser = new BrowserSession();
        var source = CreateSource(new FakeGameFileSystem(("one.bin", 1)));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        var late = new AssetNode("late.bin", "late.bin", AssetNodeKind.Raw, 2);
        source.Session.Root.AddChild(late);
        Assert.False(selection.ContainsCurrentNode(snapshot, late));
        Assert.Throws<ArgumentException>(() => selection.SetChecked(snapshot, late, true));
        selection.SetChecked(snapshot, source.Session.Root, true);
        Assert.Equal("one.bin", Assert.Single(selection.CaptureSelected(snapshot)).VirtualPath);
    }

    /// <summary>A deep completed tree can be checked and captured through the shared iterative controller.</summary>
    [Fact]
    public async Task DeepTree_ChecksAndCapturesWithoutRecursiveNodePropagation()
    {
        var path = string.Join('\\', Enumerable.Repeat("d", 2048)) + @"\leaf.nif";
        await using var browser = new BrowserSession();
        var source = CreateSource(new FakeGameFileSystem((path, 1)));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var selection = new AssetTreeSelection(snapshot);
        selection.SetChecked(snapshot, source.Session.Root, true);
        Assert.Equal(path, Assert.Single(selection.CaptureSelected(snapshot)).VirtualPath);
        Assert.Equal(1, selection.Checks.SelectedCount);
        Assert.True(source.Session.Root.IsChecked);
    }

    /// <summary>Wraps the actual eager builder and owned Bethesda source around the maintained in-memory filesystem.</summary>
    /// <param name="filesystem">The filesystem whose ownership transfers to the returned source.</param>
    /// <returns>A fresh source identity with a complete original tree.</returns>
    private static BethesdaBrowseSource CreateSource(FakeGameFileSystem filesystem) =>
        new(new AssetBrowseSession(filesystem, "test", "test", AssetTreeBuilder.Build(filesystem, "test")));
}
