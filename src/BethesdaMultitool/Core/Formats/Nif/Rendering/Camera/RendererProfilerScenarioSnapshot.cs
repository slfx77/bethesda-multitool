using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Minimal strongly-typed state captured alongside a renderer-profiler acceptance frame. The
///     full parity payload remains in the <c>capture-state</c> JSONL event; this snapshot gives the
///     scenario runner stable structural values without reading its own trace back from disk.
/// </summary>
internal sealed record RendererProfilerScenarioSnapshot(
    BethesdaGame Game,
    string? WorldspaceEditorId,
    string? WeatherEditorId,
    float GameHour,
    float GameDay,
    float AnimationTimeSeconds,
    Vector3 SunLightDirection,
    Vector3 SunBillboardDirection,
    int MoonCount,
    Vector3 PrimaryMoonDirection,
    float PrimaryMoonDrawAlpha,
    int PrimaryMoonPhase,
    int MoonPhaseLengthDays,
    IReadOnlyList<RendererProfilerCloudLayerSnapshot> CloudLayers,
    int WaterDraws,
    string? WaterPipeline,
    bool WaterNoisePrepassUsed,
    IReadOnlyList<bool> WaterMapsResolved);

internal sealed record RendererProfilerCloudLayerSnapshot(
    int SourceIndex,
    Vector2 ScrollVelocity,
    Vector2 ScrollOffset);
