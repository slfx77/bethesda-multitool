using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class NativeD3D12ResourceOwnershipSourceContractTests
{
    private static string D3D12Source(params string[] path) => SourceContract.ReadSource(
        ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", .. path]);

    [Fact]
    public void PipelineFactoryOwnsConstructionFailuresAndDisposesUniqueMirrorTwins()
    {
        var source = D3D12Source("D3D12", "ReferencePipelineFactory12.cs");
        var constructor = SourceContract.Extract(
            source,
            "public ReferencePipelineFactory12(",
            "// Original opaque PSO -> winding-flipped twin");
        var dispose = SourceContract.Extract(
            source,
            "public void Dispose()",
            "private sealed class PipelineConstructionTransaction");

        Assert.Contains("TrackConstructionPipeline(_gpu.Device.CreateGraphicsPipelineState", source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            constructor,
            "try",
            "_constructionTransaction!.Commit();",
            "catch",
            "_constructionTransaction?.Dispose();");
        Assert.Contains("_ownedMirrorPsos.Add(mirrorBack);", constructor, StringComparison.Ordinal);
        Assert.Contains("_ownedMirrorPsos.Add(mirrorA2C);", constructor, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            dispose,
            "foreach (var mirror in _ownedMirrorPsos)",
            "mirror.Dispose();",
            "OpaqueDoublePso.Dispose();");
    }

    [Fact]
    public void TextureCacheRetiresEveryPublishedPersistentSlotExactlyOnceAtTeardown()
    {
        var cache = D3D12Source("Gpu", "D3D12", "GpuTextureCache12.cs");
        var dispose = SourceContract.Extract(cache, "public void Dispose()", "public string ResourceName");
        var retire = SourceContract.Extract(
            cache,
            "private void RetirePersistentSlot(",
            "/// <summary>\n    ///     Render-thread step");
        var solids = D3D12Source("Gpu", "D3D12", "GpuSolidTextureFactory12.cs");
        var createEntry = SourceContract.Extract(
            solids,
            "internal GpuTextureCache12.Entry CreateEntry(",
            "/// <summary>\n    ///     Records + submits");

        Assert.Contains("var retiredPersistentSlots = new HashSet<uint>();", dispose,
            StringComparison.Ordinal);
        Assert.Contains("foreach (var node in _cache.Values)", dispose, StringComparison.Ordinal);
        Assert.Contains("RetirePersistentSlot(wp.BindlessIndex, retiredPersistentSlots);", dispose,
            StringComparison.Ordinal);
        Assert.Contains("RetirePersistentSlot(fn.BindlessIndex, retiredPersistentSlots);", dispose,
            StringComparison.Ordinal);
        Assert.Contains("RetirePersistentSlot(ws.BindlessIndex, retiredPersistentSlots);", dispose,
            StringComparison.Ordinal);
        Assert.Contains("foreach (var synthetic in _syntheticEntries.Values)", dispose,
            StringComparison.Ordinal);
        Assert.Contains("!retiredSlots.Add(slot)", retire, StringComparison.Ordinal);
        Assert.Contains("_deletionQueue.EnqueueDispose(new PersistentSlotReturn(_heap, slot));", retire,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            createEntry,
            "var alloc = _heap.AllocatePersistent();",
            "catch",
            "_heap.FreePersistent(alloc.BindlessIndex);");
    }

    [Fact]
    public void WaterConstructorTracksPsosTexturesFootprintAndSharedSlotsUntilCommit()
    {
        var source = D3D12Source("D3D12", "WaterRenderer12.cs");
        var constructor = SourceContract.Extract(
            source,
            "public WaterRenderer12(",
            "public global::BethesdaMultitool.Core.WorldData.WorldRenderStats LastStats");

        Assert.Contains("TrackConstructionResource(new GpuPersistentDescriptorAllocator12", constructor,
            StringComparison.Ordinal);
        Assert.Contains("TrackConstructionResource(gpu.Device.CreateComputePipelineState", constructor,
            StringComparison.Ordinal);
        Assert.Contains("TrackConstructionResource(gpu.Device.CreateCommittedResource<ID3D12Resource>",
            constructor, StringComparison.Ordinal);
        Assert.Contains("TrackConstructionPersistentSlot(blendSrv.BindlessIndex);", constructor,
            StringComparison.Ordinal);
        Assert.Contains("TrackConstructionPersistentSlot(normalSrv.BindlessIndex);", constructor,
            StringComparison.Ordinal);
        Assert.Contains("TrackConstructionPersistentSlot(mipSrv.BindlessIndex);", constructor,
            StringComparison.Ordinal);
        Assert.Contains("TrackConstructionResource(Gpu.D3D12.GpuFixedFootprintTracker12", constructor,
            StringComparison.Ordinal);
        Assert.Contains("TrackConstructionResource(gpu.Device.CreateGraphicsPipelineState", constructor,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            constructor,
            "_constructionTransaction!.Commit();",
            "catch",
            "_constructionTransaction?.Dispose();");
    }

    [Fact]
    public void SwapChainBuildsCompletelyBeforeBindingAndReleasesPartialBackBuffers()
    {
        var source = D3D12Source("Gpu", "D3D12", "GpuSwapChainSurface12.cs");
        var create = SourceContract.Extract(
            source,
            "public static GpuSwapChainSurface12? Create(",
            "public void Resize(");
        var acquire = SourceContract.Extract(
            source,
            "private static ID3D12Resource[] AcquireBackBuffers(",
            "private static void BindPanel(");
        var dispose = SourceContract.Extract(
            source,
            "public void Dispose()",
            "public static GpuSwapChainSurface12? Create(");

        SourceContract.AssertOrder(
            create,
            "AcquireBackBuffers(",
            "CreateDepthBuffer(",
            "CreateSceneColor(",
            "new GpuSwapChainSurface12(",
            "BindPanel(panel, swapChain3);");
        Assert.Contains("if (panelBindAttempted)", create, StringComparison.Ordinal);
        Assert.Contains("if (ex is OutOfMemoryException)", create, StringComparison.Ordinal);
        Assert.Contains("foreach (var buffer in buffers)", acquire, StringComparison.Ordinal);
        Assert.Contains("buffer?.Dispose();", acquire, StringComparison.Ordinal);
        SourceContract.AssertOrder(dispose, "TryDetachPanel(panel);", "_swapChain.Dispose();");
        Assert.Contains("native.SetSwapChain(null).CheckError();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TonemapAndSurfaceInternalConstructionAreTransactional()
    {
        var tonemap = D3D12Source("Gpu", "D3D12", "GpuTonemapPass12.cs");
        var tonemapConstructor = SourceContract.Extract(
            tonemap,
            "public GpuTonemapPass12(",
            "/// <summary>Whether the most recently recorded pass");
        var surface = D3D12Source("Gpu", "D3D12", "GpuSwapChainSurface12.cs");
        var surfaceConstructor = SourceContract.Extract(
            surface,
            "private GpuSwapChainSurface12(",
            "/// <summary>\n    ///     Re-derives the fixed-footprint");

        Assert.Contains("TrackConstructionResource(device.CreateRootSignature", tonemapConstructor,
            StringComparison.Ordinal);
        Assert.Contains("TrackConstructionResource(device.CreateGraphicsPipelineState", tonemapConstructor,
            StringComparison.Ordinal);
        Assert.Contains("device.CreateDescriptorHeap<ID3D12DescriptorHeap>", tonemapConstructor,
            StringComparison.Ordinal);
        Assert.Contains("TrackConstructionResource(device.CreateCommittedResource", tonemapConstructor,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            tonemapConstructor,
            "_constructionTransaction!.Commit();",
            "catch",
            "_constructionTransaction?.Dispose();");
        SourceContract.AssertOrder(
            surfaceConstructor,
            "tonemap = new GpuTonemapPass12(gpu);",
            "RefreshFootprint();",
            "catch",
            "tonemap?.Dispose();");
    }

    [Fact]
    public void SkyGeometryConstructorRollsBackItsPartialPsoFamilyBeforePublication()
    {
        var source = D3D12Source("D3D12", "SkyGeometryRenderer12.cs");
        var constructor = SourceContract.Extract(
            source,
            "public SkyGeometryRenderer12(",
            "// A low-res UV-sphere");

        SourceContract.AssertOrder(
            constructor,
            "ID3D12PipelineState? gradient = null;",
            "gradient = CreatePso(gpu, rootSignature, vs, ps, SkyBlend.Opaque);",
            "stars = CreatePso(gpu, rootSignature, vs, ps, SkyBlend.Additive);",
            "clouds = CreatePso(gpu, rootSignature, vs, ps, SkyBlend.Alpha);",
            "var fallback = GenerateGradientDome();",
            "_psoGradient = gradient;",
            "_psoStars = stars;",
            "_psoClouds = clouds;");
        SourceContract.AssertOrder(
            constructor,
            "catch",
            "clouds?.Dispose();",
            "stars?.Dispose();",
            "gradient?.Dispose();",
            "throw;");
    }
}
