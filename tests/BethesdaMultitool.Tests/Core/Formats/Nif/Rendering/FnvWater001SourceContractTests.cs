using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>Source contracts for the Windows-only PSO/split-draw and one-shot snapshot plumbing.</summary>
public sealed class FnvWater001SourceContractTests
{
    [Fact]
    public void RendererCompilesDedicatedDepthSamplePermutationAndDisposesIt()
    {
        var source = ReadRenderer();

        Assert.Contains("new ShaderMacro(\"FNV_WATER001\", \"1\")", source, StringComparison.Ordinal);
        Assert.Contains("_psoFnvWater001DepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);",
            source, StringComparison.Ordinal);
        Assert.Contains("_psoFnvWater001DepthSample.Dispose();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_psoFnvWater001 =", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EligibleDrawRendersGeneratedCellsFirstAndPlacedNifsWithWater003()
    {
        var source = ReadRenderer();
        var route = Extract(source,
            "// WATER001's reconstructed horizontal plane is valid only for generated cell quads.",
            "LastStats.DrawCallMilliseconds");

        AssertOrder(route,
            "if (useFnvWater001)",
            "cmd.DrawInstanced(6, (uint)cellVisible, 0, 0);",
            "cmd.SetPipelineState(_psoDepthSample);",
            "cmd.DrawInstanced(6, (uint)nifPacketCount, 0, (uint)cellVisible);");
        Assert.Contains("cmd.DrawInstanced(6, (uint)instanceCount, 0, 0);", route,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotSetterRequiresPositivePreflightAndRenderConsumesDescriptorBeforeEarlyReturns()
    {
        var source = ReadRenderer();
        var setter = Extract(source, "public void SetFnvWater001Snapshot(", "public void SetModernCubeMap(");
        Assert.Contains("if (!_fnvWater001PendingPreflight.Candidate)", setter, StringComparison.Ordinal);
        Assert.Contains("bindlessIndex == NoNormalMap", setter, StringComparison.Ordinal);
        Assert.Contains("width == 0", setter, StringComparison.Ordinal);
        Assert.Contains("height == 0", setter, StringComparison.Ordinal);

        var renderStart = source.IndexOf("private int RenderCore(", StringComparison.Ordinal);
        var emptyReturn = source.IndexOf(
            "if (_waterCells.Count == 0 && _nifWaterPlanes.Count == 0) return 0;",
            renderStart,
            StringComparison.Ordinal);
        Assert.True(renderStart >= 0 && emptyReturn > renderStart);
        var prefix = source[renderStart..emptyReturn];
        AssertOrder(prefix,
            "var fnvWater001Snapshot = _fnvWater001Snapshot;",
            "_fnvWater001Snapshot = default;");
    }

    [Fact]
    public void DrawTimeRecheckUsesTheActualProjectionModeAndMixedNifsAreNamed()
    {
        var source = ReadRenderer();
        var render = Extract(source, "private int RenderCore(", "private static string DescribeTechnique(");

        Assert.Contains("isPerspectiveProjection,", render, StringComparison.Ordinal);
        Assert.DoesNotContain("isPerspectiveProjection: true,", render, StringComparison.Ordinal);
        Assert.Contains(
            "$\"+FnvWater003RtFree-scene-depth-{depthRoute}-placed-nif\"",
            render,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveWaterTypeUsesCellXcwtThenWorldspaceNam2AndMustMatchSelection()
    {
        var source = ReadRenderer();
        var inspect = Extract(source,
            "private FnvWater001VisibleCellContract InspectFnvWater001VisibleCells(",
            "private FnvWater001Preflight EvaluateFnvWater001(");

        Assert.Contains("water.Cell.WaterFormId is > 0", inspect, StringComparison.Ordinal);
        Assert.Contains(": _fnvWater001WorldspaceDefaultWaterFormId;", inspect, StringComparison.Ordinal);
        Assert.Contains("effectiveWaterFormId != _fnvWater001SelectedWaterFormId", inspect,
            StringComparison.Ordinal);
        Assert.Contains("if (water.Height != planeHeight)", inspect, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpAndHlslAppendTheSameTwoWater001Registers()
    {
        var renderer = ReadRenderer();
        var shader = ReadShader();

        Assert.Contains("private const uint FnvWater001UniformByteSize = 2 * 16;", renderer,
            StringComparison.Ordinal);
        Assert.Contains("public uint FnvWater001SnapshotIndex;", renderer, StringComparison.Ordinal);
        Assert.Contains("public uint FnvWater001SnapshotWidth;", renderer, StringComparison.Ordinal);
        Assert.Contains("public uint FnvWater001SnapshotHeight;", renderer, StringComparison.Ordinal);
        Assert.Contains("public float FnvWater001PlaneHeight;", renderer, StringComparison.Ordinal);
        Assert.Contains("public Vector4 FnvWater001Surface;", renderer, StringComparison.Ordinal);
        Assert.Contains("uint4 uFnvWater001Snapshot;", shader, StringComparison.Ordinal);
        Assert.Contains("float4 uFnvWater001Surface;", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void TelemetryNamesTheReconstructedApproximationAndEveryFallbackReason()
    {
        var source = ReadRenderer();

        Assert.Contains(
            "$\"FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-{depthRoute}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"selective-content-mask-approximated-by-main-depth\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "LastStats.WaterTelemetryUnavailableReason = LastFnvWater001Decision.ReasonCode;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderKeepsRecoveredDepthProjectionAndLocalFallbackContracts()
    {
        var shader = ReadShader();
        var water001 = Extract(shader, "#if FNV_WATER001", "#else");

        Assert.Contains("sceneDistance / waterDistance", water001, StringComparison.Ordinal);
        Assert.Contains("length(scenePoint - input.vWorldPos) / underwaterFogFar", water001,
            StringComparison.Ordinal);
        Assert.Contains("dot(input.vWorldPos - scenePoint, float3(0.0, 0.0, 1.0)) / underwaterFogFar",
            water001, StringComparison.Ordinal);
        Assert.Contains("saturate(lerp(float2(1.0, 1.0), rawDepth, noiseFade))", water001,
            StringComparison.Ordinal);
        Assert.Contains("rawDepth.y * depthT * distortionScale * N.xy", water001,
            StringComparison.Ordinal);
        Assert.Contains("SampleLevel(gWaterClampSampler, refractionUv, 0)", water001,
            StringComparison.Ordinal);
        Assert.Contains("return FnvWater003LocalFallback(", water001, StringComparison.Ordinal);
        Assert.Contains("!isfinite(column) || !isfinite(depthT)", water001,
            StringComparison.Ordinal);
        Assert.Contains("clip(-1.0);", water001, StringComparison.Ordinal);
    }

    private static string ReadRenderer() => ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
        "WaterRenderer12.cs");

    private static string ReadShader() => ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "Shaders",
        "water.frag.hlsl");

    private static void AssertOrder(string source, params string[] values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            var current = source.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected `{value}` after source offset {previous}.");
            previous = current;
        }
    }

    private static string Extract(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker `{startMarker}`.");
        Assert.True(end > start, $"Missing end marker `{endMarker}` after `{startMarker}`.");
        return source[start..end];
    }

    private static string ReadSource(params string[] relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), Path.Combine(relativePath)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
