using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     Pins the D3D12/App wiring that the platform-neutral tests cannot execute. The behavioral
///     culler tests prove the masks are conservative; these contracts prove both lit shaders and
///     every host path actually consume/bind those masks without shifting established root slots.
/// </summary>
public sealed class PlacedLightTileBindingSourceContractTests
{
    [Fact]
    public void Tile_mask_root_srv_is_appended_without_reindexing_existing_slots()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuRootSignature12.cs");

        Assert.Contains("new RootDescriptor1(10, 0)", source, StringComparison.Ordinal);
        Assert.Contains("terrainCellGrid, pointLightTiles", source, StringComparison.Ordinal);
        Assert.Contains("public const int TerrainCellGridConstants = 11;", source, StringComparison.Ordinal);
        Assert.Contains("public const int PointLightTilesSrv = 12;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Shader_enumerates_mask_bits_in_original_light_index_order()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "Shaders",
            "Include", "scene_lighting.hlsli");

        Assert.Contains("StructuredBuffer<uint2> uPointLightTileMasks : register(t10, space0);",
            source, StringComparison.Ordinal);
        SourceContract.AssertOrder(source,
            "while (mask.x != 0u)",
            "AccumulatePlacedLight(bit, N, worldPos, contribution);",
            "while (mask.y != 0u)",
            "AccumulatePlacedLight(32u + bit, N, worldPos, contribution);");
        Assert.Contains("mask.x &= mask.x - 1u;", source, StringComparison.Ordinal);
        Assert.Contains("mask.y &= mask.y - 1u;", source, StringComparison.Ordinal);

        var reference = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "Shaders",
            "Reference", "reference.frag.hlsl");
        var terrain = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "Shaders",
            "Terrain", "terrain_textured.frag.hlsl");
        Assert.Contains("input.Position.xy, sunShadow", reference, StringComparison.Ordinal);
        Assert.Contains("input.Position.xy, sunShadow", terrain, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_mirror_capture_and_headless_paths_bind_or_restore_ten()
    {
        var pointLights = SourceContract.ReadAppSource("WorldView3DControl.PointLights.cs");
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var headless = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "NifHeadlessRenderer.cs");

        Assert.Contains("PlacedLightTileCuller.Build(", pointLights, StringComparison.Ordinal);
        Assert.Contains("GpuRootSignature12.Slots.PointLightTilesSrv", pointLights, StringComparison.Ordinal);
        Assert.Contains("var mainPointLightTiles = _lastPointLightTilesGpuAddress;", frame,
            StringComparison.Ordinal);
        Assert.Contains("mainPointLightTiles);", frame, StringComparison.Ordinal);
        Assert.Contains("var captureMainPointLightTiles = _lastPointLightTilesGpuAddress;", capture,
            StringComparison.Ordinal);
        Assert.Contains("captureMainPointLightTiles);", capture, StringComparison.Ordinal);
        Assert.Contains("cameraOriginOverride: captureCameraRelative ? captureRenderOrigin : null,",
            capture, StringComparison.Ordinal);
        Assert.Contains("GpuRootSignature12.Slots.PointLightTilesSrv", headless, StringComparison.Ordinal);
    }

    [Fact]
    public void Profiler_startup_trace_records_the_frame_time_ab_state()
    {
        var profiler = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "Program.cs");

        Assert.Contains(
            "[\"placedLightTiles\"] = EnvironmentVariables.Get(EnvironmentVariables.Viewer.PlacedLightTiles)",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"tolerantCull\"] = EnvironmentVariables.Get(EnvironmentVariables.Viewer.TolerantCull)",
            profiler,
            StringComparison.Ordinal);
    }
}
