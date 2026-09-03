using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Pins the command-list transaction around device-backed tonemap history. The pass cannot be
///     instantiated on non-Windows test runners, so these contracts protect the recorder wiring and
///     the exception-safe lazy-allocation boundary in production source.
/// </summary>
public sealed class GpuTonemapSubmissionTransactionSourceContractTests
{
    private static string TonemapSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
        "GpuTonemapPass12.cs");

    [Fact]
    public void Logical_history_enlists_before_record_mutates_it_and_abort_restores_it()
    {
        var source = TonemapSource();
        var record = SourceContract.Extract(
            source,
            "public unsafe void Record(",
            "private void BeginLogicalHistoryTransaction(");
        var submitted = SourceContract.Extract(
            source,
            "void IGpuCommandSubmissionParticipant12.OnCommandListSubmitted()",
            "void IGpuCommandSubmissionParticipant12.OnCommandListAborted()");
        var aborted = SourceContract.Extract(
            source,
            "void IGpuCommandSubmissionParticipant12.OnCommandListAborted()",
            "private TonemapLogicalHistoryState CaptureLogicalHistory()");
        var capture = SourceContract.Extract(
            source,
            "private TonemapLogicalHistoryState CaptureLogicalHistory()",
            "private void RestoreLogicalHistory(");
        var restore = SourceContract.Extract(
            source,
            "private void RestoreLogicalHistory(",
            "private void ClearLogicalHistoryTransaction()");

        Assert.Contains("IDisposable, IGpuCommandSubmissionParticipant12", source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            record,
            "BeginLogicalHistoryTransaction(recorder);",
            "var slot = _srvCursor;",
            "var writeIdx = _avgWriteIndex;",
            "_avgWriteIndex = readIdx;");
        Assert.Contains("recorder.EnlistCurrentFrame(this);", source, StringComparison.Ordinal);
        Assert.Contains("ClearLogicalHistoryTransaction();", submitted, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreLogicalHistory", submitted, StringComparison.Ordinal);
        Assert.Contains("RestoreLogicalHistory(snapshot);", aborted, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose", aborted, StringComparison.Ordinal);

        foreach (var field in new[]
                 {
                     "_adaptPrimed", "_avgWriteIndex", "_lastAdaptiveMode", "_lastHistoryFormat",
                     "_lastHistoryHeight", "_lastHistoryKey", "_lastHistoryTarget",
                     "_lastHistoryWidth", "LastHistoryReset", "LastHistoryResetReason"
                 })
        {
            Assert.Contains(field, capture, StringComparison.Ordinal);
            Assert.Contains(field, restore, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("_reductionTextures", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("_reductionLevelCount", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("_srvCursor", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_surface_paths_pass_the_owning_recorder_to_tonemap()
    {
        var swapChain = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuSwapChainSurface12.cs");
        var offscreen = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuOffscreenSceneTarget12.cs");

        Assert.Contains("public void ResolveTo(GpuCommandRecorder12 recorder,", swapChain,
            StringComparison.Ordinal);
        Assert.Contains("_tonemap.Record(recorder,", swapChain, StringComparison.Ordinal);
        Assert.Contains("public void RecordReadback(GpuCommandRecorder12 recorder)", offscreen,
            StringComparison.Ordinal);
        Assert.Contains("_tonemap.Record(recorder,", offscreen, StringComparison.Ordinal);
        Assert.DoesNotContain("_tonemap.Record(cmd,", swapChain, StringComparison.Ordinal);
        Assert.DoesNotContain("_tonemap.Record(cmd,", offscreen, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_reduction_level_is_installed_only_after_its_descriptor_is_valid()
    {
        var allocation = SourceContract.Extract(
            TonemapSource(),
            "for (var levelIndex = _reductionLevelCount;",
            "_reductionLevelCount = Math.Max(_reductionLevelCount, plan.DownsampleDrawCount);");

        SourceContract.AssertOrder(
            allocation,
            "var texture = _gpu.Device.CreateCommittedResource<ID3D12Resource>(",
            "try",
            "_gpu.Device.CreateRenderTargetView(texture, null, rtv);",
            "catch",
            "texture.Dispose();",
            "_reductionTextures[levelIndex] = texture;",
            "_reductionLevelCount = levelIndex + 1;");
    }
}
