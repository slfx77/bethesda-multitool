using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class GpuReferencePipelineStatisticsTests
{
    [Fact]
    public void Query_slots_and_readback_offsets_follow_the_command_recorder_ring()
    {
        Assert.Equal(1, GpuReferencePipelineStatistics12.QueryCountPerFrame);
        Assert.Equal(88, Marshal.SizeOf<QueryDataPipelineStatistics>());
        Assert.Equal(88, GpuReferencePipelineStatistics12.ResultSizeInBytes);

        Assert.Equal(0u, GpuReferencePipelineStatistics12.QueryIndex(0));
        Assert.Equal(1u, GpuReferencePipelineStatistics12.QueryIndex(1));
        Assert.Equal(0ul, GpuReferencePipelineStatistics12.ReadbackOffset(0));
        Assert.Equal(88ul, GpuReferencePipelineStatistics12.ReadbackOffset(1));
    }

    [Fact]
    public void Query_uses_pipeline_statistics_and_never_resolves_an_incomplete_interval()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuReferencePipelineStatistics12.cs");
        var resolve = SourceContract.Extract(
            source,
            "public void ResolveActiveFrame",
            "public void MarkActiveFrameSubmitted");

        Assert.Contains("QueryHeapType.PipelineStatistics", source, StringComparison.Ordinal);
        Assert.Contains("QueryType.PipelineStatistics", source, StringComparison.Ordinal);
        Assert.Contains("QueryDataPipelineStatistics", source, StringComparison.Ordinal);
        Assert.Contains("_queryOpen || !_queryCompleted", resolve, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            resolve,
            "if (_activeFrameIndex < 0 || _queryOpen || !_queryCompleted)",
            "commandList.ResolveQueryData(");
        Assert.Contains("_gpu.FrameFence.CompletedValue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitFor", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_retires_the_query_heap_when_readback_creation_fails()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuReferencePipelineStatistics12.cs");
        var constructor = SourceContract.Extract(
            source,
            "public GpuReferencePipelineStatistics12",
            "public bool IsEnabled");

        SourceContract.AssertOrder(
            constructor,
            "var queryHeap = gpu.Device.CreateQueryHeap",
            "try",
            "_readback = gpu.Device.CreateCommittedResource",
            "catch",
            "queryHeap.Dispose();",
            "throw;",
            "_queryHeap = queryHeap;");
    }

    [Fact]
    public void Frame_brackets_only_the_primary_reference_pass_and_preserves_submission_identity()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var primaryPass = SourceContract.Extract(
            frame,
            "var visibleReferences = 0;",
            "_gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.ReferencesEnd);");

        SourceContract.AssertOrder(
            primaryPass,
            "pipelineStatistics.BeginReferencePass(cmd);",
            "visibleReferences = _references?.Render(",
            "pipelineStatistics.EndReferencePass(cmd);");
        Assert.Contains("finally", primaryPass, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderReflection", primaryPass, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderBlendedDeferred", primaryPass, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordSunShadowPass", primaryPass, StringComparison.Ordinal);

        SourceContract.AssertOrder(
            frame,
            "recorder.BeginFrame();",
            "EmitCompletedReferencePipelineStatistics();",
            "_gpuReferencePipelineStatistics12?.BeginFrame(recorder.FrameIndex);");
        SourceContract.AssertOrder(
            frame,
            "_gpuReferencePipelineStatistics12?.ResolveActiveFrame(cmd);",
            "recorder.EndFrame();",
            "_gpuReferencePipelineStatistics12?.MarkActiveFrameSubmitted(");
    }

    [Fact]
    public void Feature_is_default_off_and_trace_is_frame_keyed_and_fail_closed()
    {
        var environment = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "EnvironmentVariables.cs");
        var control = SourceContract.ReadAppSource("WorldView3DControl.xaml.cs");
        var device = SourceContract.ReadAppSource("WorldView3DControl.Device.cs");
        var profiling = SourceContract.ReadAppSource("WorldView3DControl.Profiling.cs");
        var profilerProgram = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "Program.cs");

        Assert.Contains("FALLOUT_VIEWER_REFERENCE_PIPELINE_STATISTICS", environment,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ReferencePipelineStatistics)",
            control,
            StringComparison.Ordinal);
        Assert.Contains("if (_referencePipelineStatisticsRequested)", device,
            StringComparison.Ordinal);
        Assert.Contains("[\"enabled\"] = false", device, StringComparison.Ordinal);
        Assert.Contains("gpu-reference-pipeline-statistics-status", device,
            StringComparison.Ordinal);
        Assert.Contains("!IsProfileWindowFrame(statistics.FrameNumber)", profiling,
            StringComparison.Ordinal);
        Assert.Contains("[\"frame\"] = statistics.FrameNumber", profiling,
            StringComparison.Ordinal);
        Assert.Contains("[\"iaPrimitives\"] = statistics.IAPrimitives", profiling,
            StringComparison.Ordinal);
        Assert.Contains("[\"vsInvocations\"] = statistics.VSInvocations", profiling,
            StringComparison.Ordinal);
        Assert.Contains("[\"psInvocations\"] = statistics.PSInvocations", profiling,
            StringComparison.Ordinal);
        Assert.Contains("[\"referencePipelineStatistics\"]", profilerProgram,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_harness_requires_explicit_state_and_one_sample_per_scored_frame()
    {
        var harness = SourceContract.ReadSource("scratchpad", "live_profiles", "run_live.ps1");

        Assert.Contains("[string]$ExpectedReferencePipelineStatistics = ''", harness,
            StringComparison.Ordinal);
        Assert.Contains("FALLOUT_VIEWER_REFERENCE_PIPELINE_STATISTICS", harness,
            StringComparison.Ordinal);
        Assert.Contains("$pipelineStatisticsFrames.Count -ne [long]$frameCount", harness,
            StringComparison.Ordinal);
        Assert.Contains("$expectedPipelineFrame = $firstScoredFrame", harness,
            StringComparison.Ordinal);
        Assert.Contains("[uint64]$iaPrimitives", harness, StringComparison.Ordinal);
        Assert.Contains("[uint64]$vsInvocations", harness, StringComparison.Ordinal);
        Assert.Contains("[uint64]$psInvocations", harness, StringComparison.Ordinal);
        Assert.Contains("referencePipelineStatistics = $referencePipelineStatisticsMetrics", harness,
            StringComparison.Ordinal);
        Assert.Contains("$pipelineStatisticsStatus.Count -ne 0", harness,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Shutdown_waits_for_gpu_and_drains_both_query_streams_before_trace_and_resource_disposal()
    {
        var device = SourceContract.ReadAppSource("WorldView3DControl.Device.cs");
        var host = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "MainWindow.cs");
        var timedExit = SourceContract.Extract(
            host,
            "private void ExitProfiler",
            "private void CloseProfilerTrace");
        var windowClose = SourceContract.Extract(
            host,
            "private void OnClosed",
            "private async void OnWorldViewLoaded");

        SourceContract.AssertOrder(
            device,
            "private void DisposeD3D12Backend()",
            "_commandRecorder12?.WaitForGpuIdle();",
            "EmitCompletedGpuFrames();",
            "EmitCompletedReferencePipelineStatistics();",
            "_gpuReferencePipelineStatistics12?.Dispose();",
            "_gpuTimestampProfiler12?.Dispose();");
        SourceContract.AssertOrder(timedExit, "Dispose();", "CloseProfilerTrace(message);");
        SourceContract.AssertOrder(windowClose, "Dispose();", "CloseProfilerTrace(");
    }
}
