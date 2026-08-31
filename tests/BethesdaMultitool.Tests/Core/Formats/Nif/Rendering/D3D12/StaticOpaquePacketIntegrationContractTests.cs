using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Source contracts for the D3D12-only persistent opaque-packet seam. The packet resource has
///     device-independent ABI tests of its own; these assertions pin the renderer ordering that
///     makes persistent mains safe alongside transient batches and shadow-only tails.
/// </summary>
public sealed class StaticOpaquePacketIntegrationContractTests
{
    [Fact]
    public void OptInAndExactFrameKeyRequireARepeatedCandidateBeforeAllocation()
    {
        var source = RendererSource();
        var resolve = SourceContract.Extract(
            source,
            "private OpaqueSubmissionPacket12? ResolveOpaqueSubmissionPacket(",
            "private bool TryBuildOpaquePacketMainMatrices(");

        Assert.Contains(
            "EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ReferenceStaticOpaquePacket)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("if (OpaqueIndirectRequested || StaticOpaquePacketRequested)", source,
            StringComparison.Ordinal);
        Assert.Contains("if (!StaticOpaquePacketRequested)", resolve, StringComparison.Ordinal);
        Assert.Contains("if (_opaqueIndirectSignature is null)", resolve, StringComparison.Ordinal);

        foreach (var exactKeyInput in new[]
                 {
                     "_publishedBatchIdentity",
                     "_publishedBatchGeneration",
                     "_meshCache.EvictionGeneration",
                     "_frameRenderOrigin",
                     "_frameRefilterActive",
                     "in _frameFrustum",
                     "_frameCylinderX",
                     "_frameCylinderY",
                     "_frameCylinderRadius",
                     "_frameSmallPropCutoffSq",
                     "_enableDistanceLod",
                     "_shadowCaptureArmed",
                     "useCascadePrefixes"
                 })
        {
            Assert.Contains(exactKeyInput, resolve, StringComparison.Ordinal);
        }

        SourceContract.AssertOrder(
            resolve,
            "var frameKey = StaticOpaquePacketReuseKey.Create(",
            "StaticOpaquePacketPolicy.CanReuse(current.Key, frameKey)",
            "if (_opaquePacketCandidateKey is not { } candidate || candidate != frameKey)",
            "_opaquePacketCandidateKey = frameKey;",
            "fallbackReason = StaticOpaquePacketFallbackReason.KeyMismatch;",
            "_opaquePacketCandidateFrames++;",
            "var inputs = new List<OpaqueSubmissionPacket12.DrawInput>(",
            "OpaqueSubmissionPacket12.TryCreate(");
        Assert.Contains(
            "_gpu, cmd, _deletionQueue, in frameKey, inputs, out var replacement",
            resolve,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShadowTailSidecarIsCompleteBeforePacketMainsLeaveTheLegacyCensus()
    {
        var draw = DrawSource();

        SourceContract.AssertOrder(
            draw,
            "var packet = ResolveOpaqueSubmissionPacket(",
            "_ringBuffer.TryAllocate(",
            "packetDraw.ShadowTailMatrices.Span.CopyTo(",
            "packetTailInstanceAddress = packetTailAllocation.GpuAddress",
            "var packetBatchCount = packet?.DrawCount ?? 0;",
            "var totalInstances = 0;",
            "var packetCensusDrawCursor = 0;",
            "var packetized = packet is not null && packet.TryTakeDraw(",
            "if (packetized)",
            "var usefulShadowSourceCount = includeShadow",
            "var count = sourceInstanceCount + usefulShadowSourceCount;");

        var beforeCensus = draw[..draw.IndexOf("var totalInstances = 0;", StringComparison.Ordinal)];
        Assert.Contains("packet = null;", beforeCensus, StringComparison.Ordinal);
        Assert.Contains("StaticOpaquePacketFallbackReason.ShadowTailAllocationFailed", beforeCensus,
            StringComparison.Ordinal);
        Assert.Contains("if (!tailBindingsValid)", beforeCensus, StringComparison.Ordinal);
        Assert.Contains("StaticOpaquePacketFallbackReason.BuildFailed", beforeCensus,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PacketRunsBindPersistentT8AndArgumentStorageWithoutCollapsingMixedOrderGaps()
    {
        var draw = DrawSource();
        var packetSource = PacketSource();

        Assert.Contains(
            "for (var submissionIndex = 0; submissionIndex < activeBatches.Count; submissionIndex++)",
            draw,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            draw,
            "if (packet is not null && packet.TryTakeDraw(",
            "ref readonly var packetDraw = ref packet.DrawAt(packetDrawIndex);",
            "if (packet.TryTakeRun(",
            "ref readonly var packetRun = ref packet.RunAt(packetRunIndex);",
            "FlushOpaqueIndirectRun(",
            "BindReferenceInstanceBuffer(",
            "cmd.ExecuteIndirect(",
            "packet.ArgumentBuffer",
            "packetRun.ArgumentBufferOffset",
            "continue;",
            "var batch = batchState.Instances;");
        Assert.Contains("commandListInstanceAddress != packet.InstanceSrvAddress", draw,
            StringComparison.Ordinal);
        Assert.Contains("inputs[i].SubmissionIndex != inputs[i - 1].SubmissionIndex + 1",
            packetSource, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(inputs[i - 1].Pso, inputs[i].Pso)", packetSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureReplaysPersistentMainsAndTransientShadowTails()
    {
        var draw = DrawSource();
        var packetBranch = SourceContract.Extract(
            draw,
            "var packetSubmissionDrawCursor = 0;",
            "var batch = batchState.Instances;");

        Assert.Contains("_mirrorDraws.Add(new MirrorDraw(", packetBranch, StringComparison.Ordinal);
        Assert.Contains("packetDraw.PerDrawCbAddress,\n                        packet.InstanceSrvAddress,",
            packetBranch, StringComparison.Ordinal);
        Assert.Contains("_shadowDraws.Add(new ShadowDraw(", packetBranch, StringComparison.Ordinal);
        Assert.Contains("packetDraw.TailPerDrawCbAddress,\n                            packetTailInstanceAddress,",
            packetBranch, StringComparison.Ordinal);
        Assert.Contains("packetDraw.TailCount", packetBranch, StringComparison.Ordinal);
        Assert.Contains("packetDraw.ShadowTailMatrices.Span.CopyTo(", draw, StringComparison.Ordinal);
    }

    [Fact]
    public void PacketHitTraversalIsCopyFreeAndAggregatesRunTelemetryWithoutLegacyFrameWrites()
    {
        var draw = DrawSource();
        var packetSource = PacketSource();
        var copySkip = SourceContract.Extract(
            draw,
            "var packetCopyDrawCursor = 0;",
            "var worlds = batchState.Instances;");
        var packetSubmission = SourceContract.Extract(
            draw,
            "var packetSubmissionDrawCursor = 0;",
            "var batch = batchState.Instances;");

        Assert.Equal(3, SourceContract.CountOccurrences(draw, "packet.TryTakeDraw("));
        Assert.DoesNotContain("TryGetDraw", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetRunStartingAt", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("Dictionary<", packetSource, StringComparison.Ordinal);
        Assert.Contains("ref readonly var packetDraw = ref packet.DrawAt(packetDrawIndex);",
            packetSubmission, StringComparison.Ordinal);
        Assert.Contains("ref readonly var packetRun = ref packet.RunAt(packetRunIndex);",
            packetSubmission, StringComparison.Ordinal);

        Assert.DoesNotContain("FrameDrawCount", copySkip, StringComparison.Ordinal);
        Assert.DoesNotContain("FrameCascadeCount", copySkip, StringComparison.Ordinal);
        Assert.DoesNotContain("FrameShadowOnlyCount", copySkip, StringComparison.Ordinal);
        Assert.DoesNotContain("FrameShadowOnlyCascadeCount", copySkip, StringComparison.Ordinal);
        Assert.DoesNotContain("batchState.Frame", packetSubmission, StringComparison.Ordinal);

        Assert.Contains("LastStats.ReferenceOpaqueSurvivingDraws += packetRun.DrawCount;",
            packetSubmission, StringComparison.Ordinal);
        Assert.Contains("LastStats.ReferenceOpaqueOrdinaryDraws += packetRun.DrawCount;",
            packetSubmission, StringComparison.Ordinal);
        Assert.Contains("_opaqueSubmissionPsos.Add(packetRun.Pso);",
            packetSubmission, StringComparison.Ordinal);
        Assert.Contains("submeshDraws += packetRun.DrawCount;", packetSubmission,
            StringComparison.Ordinal);
        Assert.Contains("LastStats.ReferenceInstancedDraws += packetRun.DrawCount;",
            packetSubmission, StringComparison.Ordinal);
        Assert.Contains("LastStats.ReferenceInstances += packetRun.InstanceCount;",
            packetSubmission, StringComparison.Ordinal);
        Assert.DoesNotContain("_opaqueSubmissionPsos.Add(packetDraw.Pso);",
            packetSubmission, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicationIdentityAndEveryPacketReplacementRetireThroughTheDeletionQueue()
    {
        var source = RendererSource();
        var publish = SourceContract.Extract(
            source,
            "private void PublishBatchBuild(",
            "private void ClearPublishedBatchSnapshotAfterEviction()");
        var clear = SourceContract.Extract(
            source,
            "private void ClearPublishedBatchSnapshotAfterEviction()",
            "private static int BatchBuildProcessed(");
        var retire = SourceContract.Extract(
            source,
            "private void RetireOpaqueSubmissionPacket()",
            "private void DrawOpaqueBatches(");
        var dispose = SourceContract.Extract(source, "public void Dispose()", "private static bool ForwardWithin(");

        SourceContract.AssertOrder(
            publish,
            "RetireOpaqueSubmissionPacket();",
            "_publishedBatchSet = state.Target;",
            "_publishedBatchIdentity++;",
            "_publishedBatchGeneration++;");
        Assert.Contains("_deletionQueue.EnqueueDispose(prior);", source, StringComparison.Ordinal);
        Assert.Contains("RetireOpaqueSubmissionPacket();", clear, StringComparison.Ordinal);
        Assert.Contains("_publishedBatchIdentity++;", clear, StringComparison.Ordinal);
        Assert.Contains("_publishedBatchGeneration++;", clear, StringComparison.Ordinal);
        Assert.Contains("_deletionQueue.EnqueueDispose(packet);", retire, StringComparison.Ordinal);
        Assert.Contains("RetireOpaqueSubmissionPacket();", dispose, StringComparison.Ordinal);
        Assert.DoesNotContain("packet.Dispose();", retire, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadingAnotherWorldInvalidatesThePacketBeforePublishingTheNewGame()
    {
        var source = RendererSource();
        var load = SourceContract.Extract(source, "public void LoadData(", "private Vector4 ResolveTextureState(");

        SourceContract.AssertOrder(
            load,
            "RetireOpaqueSubmissionPacket();",
            "_renderCache = renderCache;",
            "GrassScatterProfile.ForGame(renderCache.Game)");
    }

    [Fact]
    public void ActiveAndHitTelemetryDistinguishAvailabilityFromPersistentReplay()
    {
        var draw = DrawSource();

        Assert.Contains(
            "checked((packet.DrawCount + packet.TailDrawCount) * (int)InstanceDrawByteSize)",
            draw,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            draw,
            "LastStats.ReferenceStaticOpaquePacketActive =",
            "StaticOpaquePacketRequested && _opaqueIndirectSignature is not null;",
            "LastStats.ReferenceStaticOpaquePacketHit = packet is not null;",
            "LastStats.ReferenceStaticOpaquePacketBatches = packetBatchCount;",
            "LastStats.ReferenceStaticOpaquePacketInstances = packetInstanceCount;",
            "LastStats.ReferenceStaticOpaquePacketRuns = packetRunCount;",
            "LastStats.ReferenceStaticOpaquePacketBuildMilliseconds = packetBuildMs;",
            "LastStats.ReferenceStaticOpaquePacketFallbackReason = (int)packetFallback;");
        SourceContract.AssertOrder(
            draw,
            "var reportedIndirectFallback = packet is not null && ordinaryMainDrawCapacity == 0",
            "? OpaqueIndirectFallbackReason.None",
            "LastStats.ReferenceOpaqueIndirectActive = useOpaqueIndirect || packet is not null;",
            "LastStats.ReferenceOpaqueIndirectFallbackReason = (int)reportedIndirectFallback;");
    }

    private static string DrawSource()
    {
        var source = RendererSource();
        return SourceContract.Extract(source, "private void DrawOpaqueBatches(", "private void DrawBlended(");
    }

    private static string RendererSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
        "ReferenceRenderer12.cs");

    private static string PacketSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
        "OpaqueSubmissionPacket12.cs");
}
