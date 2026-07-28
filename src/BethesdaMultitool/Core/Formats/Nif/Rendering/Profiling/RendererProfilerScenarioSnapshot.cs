using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;

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
    IReadOnlyList<bool> WaterMapsResolved,
    uint? WaterRecordFormId,
    string? WaterRecordEditorId,
    string WaterRecordSource,
    uint? WaterRecordCellFormId,
    ulong TonemapHistoryKey,
    bool TonemapHistoryReset,
    string? TonemapHistoryResetReason,
    RendererProfilerClimateTimingSnapshot ClimateTiming,
    RendererProfilerAtmosphericColorBandSnapshot AtmosphericColorBand,
    IReadOnlyList<RendererProfilerWeatherImageSpaceContributionSnapshot> WeatherImageSpaceContributions,
    RendererProfilerTonemapSnapshot Tonemap,
    string? WaterTechnique = null,
    string? WaterFallbackReason = null,
    int SceneSampleCount = 1,
    int FnvSls1009Draws = 0,
    int FnvSls1009Instances = 0,
    int FnvSls1013Draws = 0,
    int FnvSls1013Instances = 0,
    int PlacedLightCount = 0,
    bool FnvClassicBasicLightingEnabled = false,
    int FnvClassicBasicFallbackDraws = 0,
    int FnvClassicBasicFallbackInstances = 0,
    string? FnvClassicBasicFallbackReason = null,
    int FnvActiveAdtBaseDraws = 0,
    int FnvActiveAdtBaseInstances = 0,
    int FnvActiveAdtBaseVertexColorDraws = 0,
    int FnvActiveAdtBaseVertexColorInstances = 0,
    bool FnvActiveAdtBaseEnabled = false,
    int FnvActiveAdtBaseFallbackDraws = 0,
    int FnvActiveAdtBaseFallbackInstances = 0,
    string? FnvActiveAdtBaseFallbackReason = null);

internal sealed record RendererProfilerCloudLayerSnapshot(
    int SourceIndex,
    Vector2 ScrollVelocity,
    Vector2 ScrollOffset);

internal sealed record RendererProfilerClimateTimingSnapshot(
    float SunriseBeginHour,
    float SunriseEndHour,
    float SunsetBeginHour,
    float SunsetEndHour);

internal sealed record RendererProfilerAtmosphericColorBandSnapshot(
    string FromBand,
    string ToBand,
    float ToWeight);

internal sealed record RendererProfilerWeatherImageSpaceContributionSnapshot(
    string Band,
    uint ModifierFormId,
    string? ModifierEditorId,
    float Weight,
    float? TimelineTime);

internal sealed record RendererProfilerTonemapSnapshot(
    float TargetLum,
    float TintR,
    float TintG,
    float TintB,
    float TintAmount);
