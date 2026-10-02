using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Browsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Exercises physical archive navigation without extracting nested archives or guessing source paths.</summary>
public sealed class AssetArchiveOpenRequestTests : IDisposable
{
    private static readonly byte[] ExpectedPayload = [4, 9, 16];
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "archive-open-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates one isolated synthetic loose root.</summary>
    public AssetArchiveOpenRequestTests() => Directory.CreateDirectory(_directory);

    /// <summary>A real synthetic BSA resolves with its original spelling and opens through the ordinary planner/source path.</summary>
    [Fact]
    public async Task PhysicalBsaRequestOpensItsOriginalArchive()
    {
        var path = WriteBsa(System.IO.Path.Combine("Nested", "Selected.BSA"), "textures/probe.dds");
        await File.WriteAllTextAsync(System.IO.Path.Combine(_directory, "readme.txt"), "not an archive", TestContext.Current.CancellationToken);
        await using var browser = new BrowserSession();
        var selection = await PublishAsync(browser, new LooseFileSystem(_directory));
        var folder = selection.Session.Root.Children.Single(node => node.Kind == AssetNodeKind.Folder);
        var archive = Assert.Single(folder.Children);
        var request = Assert.IsType<AssetArchiveOpenRequest>(AssetArchiveOpenRequest.TryCreate(selection, browser.Current!, archive));
        Assert.Equal(path, request.Path);
        Assert.Same(archive, request.Node);
        Assert.Same(browser.Current, request.Snapshot);
        Assert.Null(AssetArchiveOpenRequest.TryCreate(selection, browser.Current!, selection.Session.Root.Children.Single(node => node.Kind == AssetNodeKind.Text)));

        var plan = ExploreSourcePlanner.Create(request.Path, TestContext.Current.CancellationToken);
        Assert.Equal(ExploreAssetSourceKind.Archive, plan.AssetKind);
        Assert.Null(plan.AnalysisPath);
        await using var opened = BethesdaBrowseSource.Open(plan);
        Assert.IsType<ArchiveFileSystem>(opened.Session.FileSystem);
        Assert.Equal(ExpectedPayload, opened.Session.FileSystem.TryReadAllBytes("textures/probe.dds"));
    }

    /// <summary>Opposite source ordering chooses opposite actual loose files with the same virtual identity.</summary>
    /// <param name="reverse">Whether the second physical root has precedence.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedLooseLayersPreserveTheSelectedWinner(bool reverse)
    {
        var first = WriteLoose(System.IO.Path.Combine("first", "selected.bsa"));
        var second = WriteLoose(System.IO.Path.Combine("second", "selected.bsa"));
        IGameFileSystem[] layers = [new LooseFileSystem(System.IO.Path.GetDirectoryName(first)!), new LooseFileSystem(System.IO.Path.GetDirectoryName(second)!)];
        if (reverse) Array.Reverse(layers);
        await using var browser = new BrowserSession();
        var selection = await PublishAsync(browser, new LayeredGameFileSystem(layers));
        var node = Assert.Single(selection.Session.Root.Children);
        Assert.Equal(reverse ? second : first, AssetArchiveOpenRequest.TryCreate(selection, browser.Current!, node)!.Path);
    }

    /// <summary>An archive-backed winner cannot be replaced by an available lower loose copy merely to obtain a path.</summary>
    /// <param name="entry">The root-level or nested virtual archive path.</param>
    [Theory]
    [InlineData("selected.bsa")]
    [InlineData("archives/selected.bsa")]
    public async Task NestedArchiveWinnerDoesNotFallThroughToLooseFile(string entry)
    {
        var outer = WriteBsa("outer.bsa", entry);
        var lower = WriteLoose(System.IO.Path.Combine("lower", entry.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        await using var browser = new BrowserSession();
        var upper = new ArchiveFileSystem(outer);
        var selection = await PublishAsync(browser, new LayeredGameFileSystem([
            upper, new LooseFileSystem(System.IO.Path.Combine(_directory, "lower"))]));
        Assert.Equal(outer, upper.TryStat(entry)!.Source);
        Assert.Equal(ExpectedPayload, upper.TryReadAllBytes(entry));
        var rootNode = Assert.Single(selection.Session.Root.Children);
        var node = rootNode.Kind == AssetNodeKind.Folder ? Assert.Single(rootNode.Children) : rootNode;
        Assert.Equal(AssetNodeKind.Archive, node.Kind);
        Assert.Null(AssetArchiveOpenRequest.TryCreate(selection, browser.Current!, node));
        Assert.True(File.Exists(lower));
    }

    /// <summary>Same-path objects from another opening and an already retired snapshot cannot initiate source replacement.</summary>
    [Fact]
    public async Task ForeignAndRetiredSelectionsCannotOpen()
    {
        WriteLoose("selected.bsa");
        await using var browser = new BrowserSession();
        var first = await PublishAsync(browser, new LooseFileSystem(_directory));
        var snapshot = browser.Current!;
        var firstNode = Assert.Single(first.Session.Root.Children);
        await using var otherBrowser = new BrowserSession();
        var other = await PublishAsync(otherBrowser, new LooseFileSystem(_directory));
        var otherNode = Assert.Single(other.Session.Root.Children);
        Assert.Null(AssetArchiveOpenRequest.TryCreate(first, snapshot, otherNode));
        Assert.Null(AssetArchiveOpenRequest.TryCreate(first, otherBrowser.Current!, firstNode));
        await browser.ReplaceAsync(new BethesdaBrowseSource(AssetBrowseSession.OpenFolder(_directory)), TestContext.Current.CancellationToken);
        Assert.True(snapshot.CancellationToken.IsCancellationRequested);
        Assert.Null(AssetArchiveOpenRequest.TryCreate(first, snapshot, firstNode));
    }

    /// <summary>Filesystem changes are checked at invocation instead of using a cached path from tree construction.</summary>
    [Fact]
    public async Task DeletedPhysicalArchiveIsNotOffered()
    {
        var path = WriteLoose("selected.bsa");
        await using var browser = new BrowserSession();
        var selection = await PublishAsync(browser, new LooseFileSystem(_directory));
        var node = Assert.Single(selection.Session.Root.Children);
        Assert.NotNull(AssetArchiveOpenRequest.TryCreate(selection, browser.Current!, node));
        File.Delete(path);
        Assert.Null(AssetArchiveOpenRequest.TryCreate(selection, browser.Current!, node));
    }

    /// <summary>A diagnostic session path does not authorize joining virtual entries from an unknown filesystem to disk.</summary>
    [Fact]
    public async Task UnknownFilesystemCannotBorrowPhysicalSessionPath()
    {
        WriteLoose("selected.bsa");
        await using var browser = new BrowserSession();
        var selection = await PublishAsync(browser, new FakeGameFileSystem(("selected.bsa", 3)));
        Assert.Null(AssetArchiveOpenRequest.TryCreate(selection, browser.Current!, Assert.Single(selection.Session.Root.Children)));
    }

    /// <summary>Publishes one owned synthetic filesystem through the real snapshot and tree-selection contracts.</summary>
    /// <param name="browser">The test's async source owner.</param>
    /// <param name="filesystem">The source whose ownership is transferred.</param>
    /// <returns>The fixed-tree selection for this exact published source.</returns>
    private async Task<AssetTreeSelection> PublishAsync(BrowserSession browser, IGameFileSystem filesystem)
    {
        var session = new AssetBrowseSession(filesystem, "fixture", _directory, AssetTreeBuilder.Build(filesystem, "fixture"));
        await browser.ReplaceAsync(new BethesdaBrowseSource(session), TestContext.Current.CancellationToken);
        return new AssetTreeSelection(browser.Current!);
    }

    /// <summary>Creates a small physical archive-shaped leaf without parsing or decoding its bytes.</summary>
    /// <param name="relative">The root-relative physical spelling.</param>
    /// <returns>The created file's absolute path.</returns>
    private string WriteLoose(string relative)
    {
        var path = System.IO.Path.Combine(_directory, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    /// <summary>Writes an actual minimal BSA with a fixed independent three-byte payload.</summary>
    /// <param name="relative">The physical archive path inside this fixture root.</param>
    /// <param name="entry">The archive's sole virtual entry.</param>
    /// <returns>The created archive's absolute path.</returns>
    private string WriteBsa(string relative, string entry)
    {
        var path = System.IO.Path.Combine(_directory, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var writer = new BsaWriter(false);
        writer.AddFile(entry, [4, 9, 16]);
        writer.Write(path);
        return path;
    }

    /// <summary>Removes only this isolated fixture root after every browser has retired.</summary>
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
