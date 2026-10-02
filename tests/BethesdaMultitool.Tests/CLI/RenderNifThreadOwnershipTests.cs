using System.Reflection;
using System.Text.Json;
using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;
using BethesdaMultitool.Tests.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>Exercises real NIF caller routes while input reads suspend, fail or cancel around a native renderer owner.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class RenderNifThreadOwnershipTests : IDisposable
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(60);
    private readonly byte[] _nif;
    private readonly SpriteResult _expected;
    private readonly string _root;

    /// <summary>Creates one native reference image from the existing small triangle fixture before allocating temporary files.</summary>
    public RenderNifThreadOwnershipTests()
    {
        GpuTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "The native sprite backend requires Windows.");
        _nif = BigEndianNifBuilder.Build(alphaFlags: 0);
        _expected = RenderNativeReference(_nif);
        Assert.Contains(Enumerable.Range(0, _expected.Width * _expected.Height),
            index => _expected.Pixels[index * 4 + 3] != 0);
        _root = Directory.CreateTempSubdirectory("BmtNifThreadOwnership_").FullName;
    }

    /// <summary>Runs all public caller routes, preserving a successful directory output when another image cannot be written.</summary>
    [Fact]
    public async Task FileDirectoryAndArchiveRoutesProduceNativePixelsAndIndexes()
    {
        var token = TestContext.Current.CancellationToken;
        var input = Directory.CreateDirectory(Path.Combine(_root, "input")).FullName;
        var triangle = Path.Combine(input, "triangle.nif");
        await File.WriteAllBytesAsync(triangle, _nif, token);
        await File.WriteAllBytesAsync(Path.Combine(input, "blocked.nif"), _nif, token);
        await File.WriteAllBytesAsync(Path.Combine(input, "marker-ignore.nif"), _nif, token);

        var single = Settings(triangle, "single");
        await RenderNifProcessor.RunLocalFileAsync(single, token);
        AssertImage(single.OutputDir, "triangle.png");

        var directory = Settings(input, "directory");
        Directory.CreateDirectory(Path.Combine(directory.OutputDir, "blocked.png"));
        await RenderNifProcessor.RunLocalDirectoryAsync(directory, token);
        AssertIndex(directory.OutputDir, "triangle.png");
        AssertImage(directory.OutputDir, "triangle.png");

        var archivePath = Path.Combine(_root, "meshes.bsa");
        const string entry = "meshes\\triangle.nif";
        using (var archive = BsaWriter.CreateWithAutoFlags([entry]))
        {
            archive.AddFile(entry, _nif);
            archive.Write(archivePath);
        }
        var archiveSettings = Settings("", "archive", archivePath);
        await RenderNifProcessor.RunBsaBatchAsync(archiveSettings, token);
        AssertIndex(archiveSettings.OutputDir, "meshes_triangle.png");
        AssertImage(archiveSettings.OutputDir, "meshes_triangle.png");
    }

    /// <summary>Completes a genuinely pending read on a different thread and preserves successes around a failed read.</summary>
    [Fact]
    public async Task SuspendedReadResumesNativeRenderingAndPreservesPartialSuccess()
    {
        var token = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = Settings("", "suspended");
        Directory.CreateDirectory(settings.OutputDir);
        var readerThreads = new List<int>();
        var batch = InvokeBatch(settings, ["first", "failed", "last"], (item, cancellation) =>
        {
            readerThreads.Add(Environment.CurrentManagedThreadId);
            if (item == "first")
            {
                entered.TrySetResult(Environment.CurrentManagedThreadId);
                return release.Task.WaitAsync(cancellation);
            }
            return item == "failed"
                ? Task.FromException<byte[]>(new IOException("Injected NIF input failure."))
                : Task.FromResult(_nif);
        }, token);
        try
        {
            var ownerThread = await entered.Task.WaitAsync(CompletionTimeout, token);
            Assert.False(batch.IsCompleted);
            Assert.Empty(Directory.GetFiles(settings.OutputDir, "*.png"));
            var completionThread = await Task.Factory.StartNew(() =>
            {
                release.SetResult(_nif);
                return Environment.CurrentManagedThreadId;
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.NotEqual(ownerThread, completionThread);
            await batch.WaitAsync(CompletionTimeout, token);
            Assert.Equal(3, readerThreads.Count);
            Assert.All(readerThreads, thread => Assert.Equal(ownerThread, thread));
        }
        finally
        {
            // Failed assertions must not strand a native owner waiting for this test's input gate.
            release.TrySetResult(_nif);
            await batch.WaitAsync(CompletionTimeout, CancellationToken.None);
        }
        AssertIndex(settings.OutputDir, "first.png", "last.png");
        AssertImage(settings.OutputDir, "first.png");
        AssertImage(settings.OutputDir, "last.png");
    }

    /// <summary>Cancels an unfinished input read, observes native cleanup, then completes a new file workflow.</summary>
    [Fact]
    public async Task CancelledReadReleasesTheOwnerBeforeAnotherWorkflow()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = Settings("", "cancelled");
        Directory.CreateDirectory(settings.OutputDir);
        var batch = InvokeBatch(settings, ["waiting", "unreached"], (_, token) =>
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(token);
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken);
            Assert.False(batch.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                batch.WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken));
        }
        finally
        {
            await cancellation.CancelAsync();
            release.TrySetCanceled(cancellation.Token);
            try { await batch.WaitAsync(CompletionTimeout, CancellationToken.None); }
            catch (OperationCanceledException) { /* Expected after the owner finishes its finally cleanup. */ }
        }
        Assert.Empty(Directory.GetFiles(settings.OutputDir));

        var input = Path.Combine(_root, "recovered.nif");
        await File.WriteAllBytesAsync(input, _nif, TestContext.Current.CancellationToken);
        var next = Settings(input, "after-cancellation");
        await RenderNifProcessor.RunLocalFileAsync(next, TestContext.Current.CancellationToken);
        AssertImage(next.OutputDir, "recovered.png");
    }

    /// <summary>Creates settings matching the native reference while requesting the GPU caller path.</summary>
    /// <param name="path">Input filename, directory or archive filter.</param>
    /// <param name="output">Test-local output directory name.</param>
    /// <param name="archive">Optional synthetic archive path.</param>
    /// <returns>Small fixed-size render settings with an explicit front-facing camera.</returns>
    private NifRenderSettings Settings(string path, string output, string? archive = null) => new()
    {
        Path = path,
        OutputDir = Path.Combine(_root, output),
        BsaPath = archive,
        Render = new RenderParams(1f, 16, 64),
        FixedSize = 64,
        ForceGpu = true,
        Camera = new CameraConfig { ElevationDeg = 90f, ElevationOverridden = true }
    };

    /// <summary>Injects controllable I/O into the actual private batch orchestration without adding a production test API.</summary>
    /// <param name="settings">Real output and native-renderer settings.</param>
    /// <param name="items">Ordered test input identities.</param>
    /// <param name="read">Input operation that can suspend, fail or cancel.</param>
    /// <param name="token">Batch cancellation token.</param>
    /// <returns>The production batch task, including native cleanup and index writing.</returns>
    private static Task InvokeBatch(NifRenderSettings settings, string[] items,
        Func<string, CancellationToken, Task<byte[]>> read, CancellationToken token)
    {
        var method = typeof(RenderNifProcessor).GetMethod("RunBatchAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Func<string, (string Path, string BaseName)> describe = static item => (item + ".nif", item);
        return Assert.IsAssignableFrom<Task>(method.MakeGenericMethod(typeof(string)).Invoke(null,
            [settings, items, null, null, describe, read, token]));
    }

    /// <summary>Renders the fixture directly through the native backend on one thread for pixel comparison.</summary>
    /// <param name="payload">Synthetic big-endian triangle fixture.</param>
    /// <returns>The visible native output that the asynchronous callers must reproduce exactly.</returns>
    private static SpriteResult RenderNativeReference(byte[] payload)
    {
        var converted = NifConverter.Convert(payload);
        Assert.True(converted.Success);
        Assert.NotNull(converted.OutputData);
        var nif = NifParser.Parse(converted.OutputData);
        Assert.NotNull(nif);
        var model = NifGeometryExtractor.Extract(converted.OutputData, nif);
        Assert.NotNull(model);
        using var gpu = GpuDevice12.Create();
        Assert.NotNull(gpu);
        using var renderer = new GpuSpriteRenderer12(gpu);
        return Assert.IsType<SpriteResult>(renderer.Render(model, null, 1f, 16, 64, 0f, 90f, 64));
    }

    /// <summary>Checks decoded output pixels against the independently executed native reference.</summary>
    /// <param name="directory">Workflow output directory.</param>
    /// <param name="name">Expected PNG filename.</param>
    private void AssertImage(string directory, string name)
    {
        using var image = Image.Load<Rgba32>(Path.Combine(directory, name));
        Assert.Equal(_expected.Width, image.Width);
        Assert.Equal(_expected.Height, image.Height);
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        Assert.Equal(_expected.Pixels, pixels);
    }

    /// <summary>Checks the established serialized field names and excludes unsuccessful or filtered inputs.</summary>
    /// <param name="directory">Workflow output directory.</param>
    /// <param name="files">Exactly the successful PNG filenames.</param>
    private void AssertIndex(string directory, params string[] files)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "sprite-index.json")));
        Assert.Equal(files.Order(StringComparer.Ordinal), json.RootElement.EnumerateObject().Select(item => item.Name));
        foreach (var name in files)
        {
            var item = json.RootElement.GetProperty(name);
            Assert.Equal(name, item.GetProperty("File").GetString());
            Assert.Equal(_expected.Width, item.GetProperty("Width").GetInt32());
            Assert.Equal(_expected.Height, item.GetProperty("Height").GetInt32());
        }
    }

    /// <summary>Deletes this test's temporary fixture and outputs after all native workflows have completed.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
