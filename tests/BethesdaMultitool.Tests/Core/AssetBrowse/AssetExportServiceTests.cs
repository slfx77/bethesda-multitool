using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Checks selection export's preservation and error behavior using synthetic loose assets.</summary>
public sealed class AssetExportServiceTests
{
    /// <summary>Identity-sensitive exports reject a readable payload from a different source layer.</summary>
    [Fact]
    public async Task DoesNotRelabelPayloadFromDifferentProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), "asset-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "voice.ogg"), "payload", TestContext.Current.CancellationToken);
            using var source = new LooseFileSystem(root);
            var output = Path.Combine(root, "output");
            var results = await AssetExportService.ExportAsync(source, ["voice.ogg"], output, AssetExportMode.Original,
                expectedProvenance: new Dictionary<string, string> { ["voice.ogg"] = "another-archive.bsa" },
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(Assert.Single(results).Success);
            Assert.False(File.Exists(Path.Combine(output, "voice.ogg")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Duplicate selections are collapsed, relative paths survive, and missing items do not discard success.</summary>
    [Fact]
    public async Task PreservesPathsDeduplicatesAndContinuesAfterItemFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "asset-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = Path.Combine(root, "input");
            Directory.CreateDirectory(Path.Combine(input, "textures"));
            await File.WriteAllTextAsync(Path.Combine(input, "textures", "one.bin"), "payload", TestContext.Current.CancellationToken);
            using var source = new LooseFileSystem(input);
            var output = Path.Combine(root, "output");
            string[] selection = ["textures/one.bin", "textures\\one.bin", "missing.bin"];
            var results = await AssetExportService.ExportAsync(source, selection, output, AssetExportMode.Original,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, results.Count);
            Assert.True(results[0].Success);
            Assert.False(results[1].Success);
            Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(output, "textures", "one.bin"), TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Input files and existing outputs remain intact unless a distinct output replacement is requested.</summary>
    [Fact]
    public async Task RefusesTraversalInputReplacementAndUnrequestedOverwrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "asset-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "one.bin"), "source", TestContext.Current.CancellationToken);
            using var source = new LooseFileSystem(root);
            string[] selected = ["one.bin", "../escape.bin"];
            var results = await AssetExportService.ExportAsync(source, selected, root, AssetExportMode.Original,
                overwrite: true, cancellationToken: TestContext.Current.CancellationToken);
            Assert.All(results, result => Assert.False(result.Success));
            Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(root, "one.bin"), TestContext.Current.CancellationToken));
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "one.bin"), "existing", TestContext.Current.CancellationToken);
            var refused = await AssetExportService.ExportAsync(source, ["one.bin"], output, AssetExportMode.Original,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(Assert.Single(refused).Success);
            Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(output, "one.bin"), TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A corrupt DDX produces a per-item failure without publishing a broken DDS.</summary>
    [Fact]
    public async Task CorruptTextureDoesNotPublishOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "asset-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "bad.ddx"), "invalid", TestContext.Current.CancellationToken);
            using var source = new LooseFileSystem(root);
            var output = Path.Combine(root, "output");
            var results = await AssetExportService.ExportAsync(source, ["bad.ddx"], output, AssetExportMode.DdxToDds,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(Assert.Single(results).Success);
            Assert.False(File.Exists(Path.Combine(output, "bad.dds")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A failed override cannot hide the actual loose input or containing archive from replacement protection.</summary>
    /// <param name="archiveSource">Whether the successful read names an archive file rather than a loose root.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualReadProvenanceCannotBeReplacedAndLaterItemsStillExport(bool archiveSource)
    {
        var root = Path.Combine(Path.GetTempPath(), "asset-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = Path.Combine(root, "actual");
            var higher = Path.Combine(root, "unreadable-override");
            var other = Path.Combine(root, "other");
            var payloads = archiveSource ? Path.Combine(root, "archive-payloads") : output;
            foreach (var directory in new[] { output, higher, other, payloads })
            {
                Directory.CreateDirectory(directory);
            }
            var original = Path.Combine(output, "one.bin");
            await File.WriteAllTextAsync(Path.Combine(higher, "one.bin"), "unreadable override", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(payloads, "one.bin"), "readable payload", TestContext.Current.CancellationToken);
            if (archiveSource)
                await File.WriteAllTextAsync(original, "original archive container", TestContext.Current.CancellationToken);
            var before = await File.ReadAllBytesAsync(original, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(other, "later.bin"), "later payload", TestContext.Current.CancellationToken);
            using var source = new LayeredGameFileSystem(
            [
                new ObservedFileSystem(new LooseFileSystem(higher)) { Readable = false },
                new ObservedFileSystem(new LooseFileSystem(payloads)) { ReadSource = archiveSource ? original : null },
                new LooseFileSystem(other)
            ]);
            var reported = new List<AssetExportResult>();

            var results = await AssetExportService.ExportAsync(source, ["one.bin", "later.bin"], output,
                AssetExportMode.Original, overwrite: true, progress: new InlineProgress(reported.Add),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, results.Count);
            Assert.False(results[0].Success);
            Assert.Equal("An export cannot replace its original input.", results[0].Error);
            Assert.Equal(original, results[0].OutputPath);
            Assert.True(results[1].Success);
            Assert.Equal(results, reported);
            Assert.Equal(before, await File.ReadAllBytesAsync(original, TestContext.Current.CancellationToken));
            Assert.Equal("later payload", await File.ReadAllTextAsync(Path.Combine(output, "later.bin"), TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(output, "*.tmp", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Cancellation observed after a bounded read neither replaces an existing output nor reports completion.</summary>
    [Fact]
    public async Task CancellationDuringReadPreservesOutputAndPublishesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "asset-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = Path.Combine(root, "input");
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(input);
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(input, "one.bin"), "new payload", TestContext.Current.CancellationToken);
            var destination = Path.Combine(output, "one.bin");
            await File.WriteAllTextAsync(destination, "existing output", TestContext.Current.CancellationToken);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var source = new ObservedFileSystem(new LooseFileSystem(input))
                { AfterRead = _ => cancellation.Cancel() };
            var reported = new List<AssetExportResult>();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AssetExportService.ExportAsync(source,
                ["one.bin"], output, AssetExportMode.Original, overwrite: true,
                progress: new InlineProgress(reported.Add), cancellationToken: cancellation.Token));

            Assert.Equal(["one.bin"], source.ReadPaths);
            Assert.Empty(reported);
            Assert.Equal("existing output", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
            Assert.Equal([destination], Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A failed final rename removes the staged file and preserves later successful outputs.</summary>
    [Fact]
    public async Task PublicationFailureCleansTemporaryFileAndContinues()
    {
        var root = Path.Combine(Path.GetTempPath(), "asset-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = Path.Combine(root, "input");
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(input);
            Directory.CreateDirectory(Path.Combine(output, "one.bin"));
            await File.WriteAllTextAsync(Path.Combine(input, "one.bin"), "first", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(input, "later.bin"), "later", TestContext.Current.CancellationToken);
            using var source = new LooseFileSystem(input);
            var reported = new List<AssetExportResult>();

            var results = await AssetExportService.ExportAsync(source, ["one.bin", "later.bin"], output,
                AssetExportMode.Original, overwrite: true, progress: new InlineProgress(reported.Add),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, results.Count);
            Assert.False(results[0].Success);
            Assert.False(string.IsNullOrWhiteSpace(results[0].Error));
            Assert.True(results[1].Success);
            Assert.Equal(results, reported);
            Assert.True(Directory.Exists(Path.Combine(output, "one.bin")));
            Assert.Equal([Path.Combine(output, "later.bin")], Directory.GetFiles(output, "*", SearchOption.AllDirectories));
            Assert.Equal("later", await File.ReadAllTextAsync(Path.Combine(output, "later.bin"), TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Cancellation between items retains completed bytes and never begins the next source read.</summary>
    [Fact]
    public async Task CancellationAfterSuccessRetainsCompletedOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "asset-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = Path.Combine(root, "input");
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(input);
            await File.WriteAllTextAsync(Path.Combine(input, "one.bin"), "first", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(input, "later.bin"), "later", TestContext.Current.CancellationToken);
            using var source = new ObservedFileSystem(new LooseFileSystem(input));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var reported = new List<AssetExportResult>();
            var progress = new InlineProgress(result => { reported.Add(result); cancellation.Cancel(); });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AssetExportService.ExportAsync(source,
                ["one.bin", "later.bin"], output, AssetExportMode.Original, progress: progress,
                cancellationToken: cancellation.Token));

            Assert.True(Assert.Single(reported).Success);
            Assert.Equal(["one.bin"], source.ReadPaths);
            Assert.Equal([Path.Combine(output, "one.bin")], Directory.GetFiles(output, "*", SearchOption.AllDirectories));
            Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(output, "one.bin"), TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Observes real bounded loose reads while simulating an unreadable override or an archive provenance label.</summary>
    private sealed class ObservedFileSystem(IGameFileSystem inner) : IGameFileSystem
    {
        internal bool Readable { get; init; } = true;
        internal string? ReadSource { get; init; }
        internal Action<string>? AfterRead { get; init; }
        internal List<string> ReadPaths { get; } = [];
        public string Label => inner.Label;
        public bool Exists(string path) => inner.Exists(path);
        public GameFileEntry? TryStat(string path) => inner.TryStat(path);
        public byte[]? TryReadAllBytes(string path) => Readable ? inner.TryReadAllBytes(path) : null;
        public GameFileReadResult? TryReadAllBytesBounded(string path, long maximumBytes)
        {
            ReadPaths.Add(path);
            var read = Readable ? inner.TryReadAllBytesBounded(path, maximumBytes) : null;
            if (read is not null && ReadSource is { } source)
                read = read with { Entry = read.Entry with { Source = source } };
            AfterRead?.Invoke(path);
            return read;
        }
        public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null) => inner.EnumerateFiles(prefix);
        public GameFileEnumerationPage EnumerateFilesBounded(string? prefix, int maximumEntries) =>
            inner.EnumerateFilesBounded(prefix, maximumEntries);
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Runs progress callbacks synchronously at the service's completed-item boundary.</summary>
    private sealed class InlineProgress(Action<AssetExportResult> report) : IProgress<AssetExportResult>
    {
        public void Report(AssetExportResult value) => report(value);
    }
}
