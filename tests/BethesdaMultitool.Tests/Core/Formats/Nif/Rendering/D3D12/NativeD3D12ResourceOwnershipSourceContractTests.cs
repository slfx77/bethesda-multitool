using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class NativeD3D12ResourceOwnershipSourceContractTests
{
    private static string D3D12Source(params string[] path)
    {
        return SourceContract.ReadSource(
            ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", .. path]);
    }

    /// <summary>Retains Shared families before creation and retries releases without disposing borrowed pipeline aliases.</summary>
    [Fact]
    public void PipelineFactoryOwnsConstructionFailuresAndDisposesUniqueMirrorTwins()
    {
        var source = D3D12Source("D3D12", "ReferencePipelineFactory12.cs");
        var constructor = SourceContract.Extract(
            source,
            "public ReferencePipelineFactory12(",
            "// Original opaque PSO -> winding-flipped twin");
        var dispose = SourceContract.Extract(
            source, "public void Dispose()", "private RetiredResourceDisposal PrepareRetiredResources()");
        var retirement = SourceContract.Extract(
            source, "private RetiredResourceDisposal PrepareRetiredResources()", "private void VerifyAccess()");
        var transaction = D3D12Source("D3D12", "ReferencePipelineConstructionTransaction12.cs");

        Assert.DoesNotContain("_gpu.Device.CreateGraphicsPipelineState", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pipeline.Dispose();", source, StringComparison.Ordinal);
        SourceContract.AssertOrder(constructor,
            "_grassRoute = TrackConstructionResource(new ReferenceShaderRoute12(rootSignature));",
            "_instancedGrassBlendRoute = TrackConstructionResource(new ReferenceShaderRoute12(rootSignature));",
            "var fixedPipelineResources = RetainPipelineFamily(26);",
            "_sharedRoute = TrackConstructionResource(",
            "new ReferenceShaderRoute12(rootSignature, blendedVsBytecode, psBytecode)",
            "DirectOpaqueBackPso = CreatePipelineState(fixedPipelineResources, 0,");
        SourceContract.AssertOrder(constructor,
            "try", "_constructionTransaction!.Commit();", "catch (Exception creationError)",
            "_constructionTransaction?.Dispose();", "catch (Exception cleanupError)",
            "creationError, cleanupError);");
        SourceContract.AssertOrder(transaction,
            "for (var index = _creationOrder.Count - 1; index >= 0; index--)",
            "resource.Dispose();", "_owned.Remove(resource);", "_creationOrder.RemoveAt(index);");
        Assert.Contains("throw new AggregateException", transaction, StringComparison.Ordinal);

        var retain = SourceContract.Extract(source,
            "private ShaderPipelineResources RetainPipelineFamily(", "private void ReleasePipelineFamily(");
        SourceContract.AssertOrder(retain,
            "RetryPendingPipelineRelease();", "_pipelineFamilies.EnsureCapacity(",
            "_constructionTransaction?.Reserve();", "_rootSignature.CreatePipelineResources(capacity);",
            "_pipelineFamilies.Add(pipelineResources);", "_constructionTransaction?.Track(pipelineResources);");
        var release = SourceContract.Extract(source,
            "private void ReleasePipelineFamily(", "private void ReleaseUnpublishedPipelineFamily(");
        SourceContract.AssertOrder(release,
            "pipelineResources.Dispose();", "_constructionTransaction?.Forget(pipelineResources);",
            "_pipelineFamilies.Remove(pipelineResources);");
        var retry = SourceContract.Extract(source,
            "private void RetryPendingPipelineRelease()", "private static ReadOnlyMemory<byte> CompileEmbeddedShader(");
        SourceContract.AssertOrder(retry,
            "ReleasePipelineFamily(_pendingPipelineRelease);", "_pendingPipelineRelease = null;");
        SourceContract.AssertOrder(dispose,
            "VerifyAccess();", "_retiredResources = PrepareRetiredResources();", "_disposed = true;",
            "_retiredResources!.Dispose();", "_pipelineFamilies.Clear();");
        Assert.DoesNotContain("if (_disposed) return;", dispose, StringComparison.Ordinal);
        Assert.Contains("retired.Add(_sharedRoute,", retirement, StringComparison.Ordinal);
        Assert.Contains("retired.Add(_grassRoute,", retirement, StringComparison.Ordinal);
        Assert.Contains("retired.Add(_instancedGrassBlendRoute,", retirement, StringComparison.Ordinal);
        Assert.Contains("retired.Add(_independentSkinPipelines,", retirement, StringComparison.Ordinal);
        Assert.Contains("retired.Add(_pipelineFamilies[index],", retirement, StringComparison.Ordinal);
        Assert.DoesNotContain(".Dispose();", retirement, StringComparison.Ordinal);
        Assert.DoesNotContain("_ownedMirrorPsos", source, StringComparison.Ordinal);

        var renderer = D3D12Source("D3D12", "ReferenceRenderer12.cs");
        var rendererDispose = SourceContract.Extract(
            renderer, "public void Dispose()", "private void VerifyDisposalAccess()");
        SourceContract.AssertOrder(rendererDispose,
            "VerifyDisposalAccess();", "retired.Add(RetireOpaqueSubmissionPacket,",
            "retired.Add(_opaqueIndirectSignature,", "retired.Add(_pipelines,",
            "_retiredResources = retired;", "_disposed = true;", "_retiredResources!.Dispose();");
        Assert.Equal(2, SourceContract.CountOccurrences(rendererDispose, "stage: 1"));
        Assert.DoesNotContain("if (_disposed) return;", rendererDispose, StringComparison.Ordinal);
        var packetTransfer = SourceContract.Extract(
            renderer, "private void RetireOpaqueSubmissionPacket()", "private void DrawOpaqueBatches(");
        SourceContract.AssertOrder(packetTransfer,
            "if (_opaqueSubmissionPacket is { } packet)", "_deletionQueue.EnqueueDispose(packet);",
            "_opaqueSubmissionPacket = null;", "_opaquePacketCandidateKey = null;");
    }

    [Fact]
    public void TextureCacheRetiresEveryPublishedPersistentSlotExactlyOnceAtTeardown()
    {
        var cache = D3D12Source("Gpu", "D3D12", "GpuTextureCache12.cs");
        // Teardown moved onto the shared RetiredResourceDisposal staging: Dispose now runs ordered
        // prerequisites and CollectRetiredResources freezes the post-drain graph, where a HashSet
        // makes "exactly once" structural rather than a guard inside each retirement call.
        var dispose = SourceContract.Extract(
            cache, "private void CollectRetiredResources()", "private static void ReportRetirementFailure");
        var retire = SourceContract.Extract(
            cache,
            "private void RetirePersistentSlot(",
            "/// <summary>\n    ///     Render-thread step");
        var solids = D3D12Source("Gpu", "D3D12", "GpuSolidTextureFactory12.cs");
        var createEntry = SourceContract.Extract(
            solids,
            "internal GpuTextureCache12.Entry CreateEntry(",
            "/// <summary>\n    ///     Records + submits");

        Assert.Contains("var slots = new HashSet<uint>();", dispose, StringComparison.Ordinal);
        Assert.Contains("foreach (var node in _cache.Values)", dispose, StringComparison.Ordinal);
        Assert.Contains("slots.Add(wp.BindlessIndex);", dispose, StringComparison.Ordinal);
        Assert.Contains("slots.Add(fn.BindlessIndex);", dispose, StringComparison.Ordinal);
        Assert.Contains("slots.Add(ws.BindlessIndex);", dispose, StringComparison.Ordinal);
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

    /// <summary>Owns the shared pipeline family before allocation while retaining texture, footprint and slot rollback.</summary>
    [Fact]
    public void WaterConstructorTracksPsosTexturesFootprintAndSharedSlotsUntilCommit()
    {
        var source = D3D12Source("D3D12", "WaterRenderer12.cs");
        var constructor = SourceContract.Extract(
            source,
            "public WaterRenderer12(",
            "public global::BethesdaMultitool.Core.WorldData.WorldRenderStats LastStats");

        Assert.DoesNotContain("_persistentSrvs", source, StringComparison.Ordinal);
        SourceContract.AssertOrder(constructor,
            "_pipelineResources = TrackConstructionResource(rootSignature.CreatePipelineResources(18));",
            "WaterPipelineFactory12.CreateBasePipelines(gpu, _pipelineResources)",
            "_fnvNoiseScrollBlendPso = pipelines.NoiseScrollBlend;",
            "_psoStarfieldDepthSample = pipelines.StarfieldDepthSample;");
        Assert.DoesNotContain("gpu.Device.CreateComputePipelineState", constructor, StringComparison.Ordinal);
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
        Assert.DoesNotContain("gpu.Device.CreateGraphicsPipelineState", constructor, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            constructor,
            "_constructionTransaction!.Commit();",
            "catch",
            "_constructionTransaction?.Dispose();");
    }

    /// <summary>Keeps optional initialization failures and staged runtime release reachable for an owner-thread retry.</summary>
    [Fact]
    public void WaterRetainsOptionalFamilyAndRuntimeCleanupBeforeStopping()
    {
        var source = D3D12Source("D3D12", "WaterRenderer12.cs");
        var initialize = SourceContract.Extract(source,
            "private bool TryEnsureModernWater(", "private unsafe bool RecordModernWaterPrepasses(");
        SourceContract.AssertOrder(initialize,
            "_pendingModernWater = new ModernWaterResources12(",
            "_pendingModernWater.Initialize(",
            "resources = _pendingModernWater;",
            "_modernWater = resources;");
        SourceContract.AssertOrder(initialize, "_pendingModernWater?.Dispose();", "_pendingModernWater = null;");
        var dispose = SourceContract.Extract(source, "public void Dispose()", "private RetiredResourceDisposal PrepareRetiredResources()");
        SourceContract.AssertOrder(dispose, "VerifyAccess();", "PrepareRetiredResources();", "_disposed = true;", "_retiredResources!.Dispose();");
        var releases = SourceContract.Extract(source,
            "private RetiredResourceDisposal PrepareRetiredResources()", "private void RetireInstanceBuffer()");
        SourceContract.AssertOrder(releases, "retired.Add(_modernWater,", "retired.Add(_pendingModernWater,", "retired.Add(_pipelineResources,");
        Assert.Contains("stage: 1", releases, StringComparison.Ordinal);
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

    /// <summary>Requires constructor rollback to own Shared before native allocation and retain the remaining HDR resources.</summary>
    [Fact]
    public void TonemapAndSurfaceInternalConstructionAreTransactional()
    {
        var tonemap = D3D12Source("Gpu", "D3D12", "GpuTonemapPass12.cs");
        var tonemapConstructor = SourceContract.Extract(
            tonemap,
            "public GpuTonemapPass12(",
            "///     Whether the latest recorded pass invalidated eye-adaptation history.");
        var surface = D3D12Source("Gpu", "D3D12", "GpuSwapChainSurface12.cs");
        var surfaceConstructor = SourceContract.Extract(
            surface,
            "private GpuSwapChainSurface12(",
            "/// <summary>\n    ///     Re-derives the fixed-footprint");

        SourceContract.AssertOrder(
            tonemapConstructor,
            "_pipelineResources = TrackConstructionResource(new ShaderPipelineResources(device, 10));",
            "_pipelineResources.Initialize(new VersionedRootSignatureDescription(desc));",
            "_rootSignature = _pipelineResources.RootSignature;",
            "_pso = _pipelineResources.CreateGraphics(0, psoDesc);",
            "_tes4BlurPso = _pipelineResources.CreateGraphics(9, tes4BlurPsoDesc);");
        Assert.Equal(10, SourceContract.CountOccurrences(tonemapConstructor, "_pipelineResources.CreateGraphics("));
        Assert.DoesNotContain("device.CreateRootSignature", tonemapConstructor, StringComparison.Ordinal);
        Assert.DoesNotContain("device.CreateGraphicsPipelineState", tonemapConstructor, StringComparison.Ordinal);
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

    /// <summary>Requires retryable HDR cleanup and one shared owner for the borrowed root and all pipeline handles.</summary>
    [Fact]
    public void TonemapRetainsFailedCleanupWithoutReleasingBorrowedPipelinesTwice()
    {
        var tonemap = D3D12Source("Gpu", "D3D12", "GpuTonemapPass12.cs");
        var dispose = SourceContract.Extract(
            tonemap,
            "public void Dispose()",
            "void IGpuCommandSubmissionParticipant12.OnCommandListSubmitted()");

        Assert.DoesNotContain("if (_disposed) return;", dispose, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            dispose,
            "if (_retiredResources is null)",
            "_disposed = true;",
            "var retired = new RetiredResourceDisposal();",
            "retired.Add(_srvHeap,",
            "retired.Add(_avgTextures[0],",
            "foreach (var texture in _reductionTextures)",
            "retired.Add(_bloomTexture,",
            "foreach (var texture in _alternateReductionTextures)",
            "retired.Add(_alternateBloomTexture,",
            "retired.Add(_pipelineResources, \"tonemap pipeline family\", 1);",
            "_retiredResources = retired;",
            "}\n        _retiredResources.Dispose();");
        foreach (var borrowed in new[]
                 {
                     "_rootSignature", "_pso", "_avgPso", "_adaptPso", "_downsamplePso",
                     "_skyrimLuminancePso", "_skyrimDownsamplePso", "_bloomPso", "_blurPso",
                     "_tes4BrightPassPso", "_tes4BlurPso"
                 })
        {
            Assert.DoesNotContain($"{borrowed}.Dispose()", tonemap, StringComparison.Ordinal);
        }
    }

    /// <summary>Requires the retained Shared family to cover every sky allocation until the renderer can be published.</summary>
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
            "_pipelineResources = rootSignature.CreatePipelineResources(3);",
            "SkyPipelineFactory12.CreateGeometryPipelines(gpu, _pipelineResources)",
            "var fallback = GenerateGradientDome();",
            "_psoGradient = gradient;",
            "_psoStars = stars;",
            "_psoClouds = clouds;");
        SourceContract.AssertOrder(
            constructor,
            "catch",
            "_pipelineResources.Dispose();",
            "throw;");
        Assert.DoesNotContain("CreateGraphicsPipelineState", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_psoGradient.Dispose()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_psoStars.Dispose()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_psoClouds.Dispose()", source, StringComparison.Ordinal);
    }
}
