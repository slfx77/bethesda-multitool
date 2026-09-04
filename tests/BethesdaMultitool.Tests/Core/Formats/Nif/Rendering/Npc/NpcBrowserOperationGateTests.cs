using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcBrowserOperationGateTests
{
    [Fact]
    public void Enter_IsReentrantForNestedServiceOperations()
    {
        var gate = new NpcBrowserOperationGate();

        using var outerOperation = gate.Enter();
        using var nestedOperation = gate.Enter();
    }

    [Fact]
    public async Task Enter_SerializesConcurrentOperations()
    {
        var gate = new NpcBrowserOperationGate();
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();

        var first = Task.Run(() =>
        {
            using var operation = gate.Enter();
            firstEntered.Set();
            Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10)));
        });
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));

        var second = Task.Run(() =>
        {
            secondStarted.Set();
            using var operation = gate.Enter();
            secondEntered.Set();
        });
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            releaseFirst.Set();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(secondEntered.IsSet);
    }

    [Fact]
    public async Task DisposeResources_WaitsForActiveOperationAndRejectsNewWork()
    {
        var gate = new NpcBrowserOperationGate();
        using var operationEntered = new ManualResetEventSlim();
        using var releaseOperation = new ManualResetEventSlim();
        using var disposalStarted = new ManualResetEventSlim();
        using var resourcesDisposed = new ManualResetEventSlim();

        var activeOperation = Task.Run(() =>
        {
            using var operation = gate.Enter();
            operationEntered.Set();
            Assert.True(releaseOperation.Wait(TimeSpan.FromSeconds(10)));
        });
        Assert.True(operationEntered.Wait(TimeSpan.FromSeconds(10)));

        var disposal = Task.Run(() =>
        {
            disposalStarted.Set();
            gate.DisposeResources(resourcesDisposed.Set);
        });
        Assert.True(disposalStarted.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            Assert.False(resourcesDisposed.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            releaseOperation.Set();
        }

        await Task.WhenAll(activeOperation, disposal).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(resourcesDisposed.IsSet);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var operation = gate.Enter();
        });
    }

    [Fact]
    public void ServiceSerializesEveryMutableCompositionAndTextureConsumer()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc",
            "NpcBrowserService.cs");
        var dispose = SourceContract.Extract(
            source,
            "public void Dispose()",
            "public static NpcBrowserService? TryCreate(");
        var npcScene = SourceContract.Extract(
            source,
            "public BethesdaViewerScene? BuildViewerScene(",
            "public BethesdaViewerScene? BuildCreatureViewerScene(");
        var creatureScene = SourceContract.Extract(
            source,
            "public BethesdaViewerScene? BuildCreatureViewerScene(",
            "public byte[] ExportViewerSceneToGlb(");
        var glbExport = SourceContract.Extract(
            source,
            "public byte[] ExportViewerSceneToGlb(",
            "public byte[]? BuildGlb(");
        var pngRender = SourceContract.Extract(
            source,
            "public byte[]? RenderPng(",
            "public async Task BatchExportGlbAsync(");
        var batchExport = SourceContract.Extract(
            source,
            "public async Task BatchExportGlbAsync(",
            "public async Task BatchRenderPngAsync(");
        var batchRender = SourceContract.Extract(
            source,
            "public async Task BatchRenderPngAsync(",
            "private NpcAppearance? ResolveAppearance(");

        Assert.Contains("_operationGate.DisposeResources", dispose, StringComparison.Ordinal);
        Assert.Contains("_operationGate.Enter()", npcScene, StringComparison.Ordinal);
        Assert.Contains("_operationGate.Enter()", creatureScene, StringComparison.Ordinal);
        Assert.Contains("_operationGate.Enter()", glbExport, StringComparison.Ordinal);
        Assert.Contains("_operationGate.Enter()", pngRender, StringComparison.Ordinal);
        Assert.Equal(2, SourceContract.CountOccurrences(batchExport, "_operationGate.Enter()"));
        Assert.Equal(2, SourceContract.CountOccurrences(batchRender, "_operationGate.Enter()"));
    }
}
