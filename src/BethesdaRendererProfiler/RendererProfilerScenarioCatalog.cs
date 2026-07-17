using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaRendererProfiler;

internal sealed record RendererProfilerScenarioPostProcessSettings(
    bool HdrEnabled,
    bool BloomEnabled,
    bool ImagespaceEnabled,
    bool FogEnabled);

internal sealed record RendererProfilerScenarioFixture(
    uint ReferenceFormId,
    uint BaseFormId,
    string BaseEditorId,
    string ModelPath,
    Vector3 PlacementPosition,
    Vector3 PlacementRotationRadians);

internal sealed record RendererProfilerScenarioStep(
    string Id,
    string WeatherEditorId,
    float GameHour,
    float GameDay,
    float AnimationTimeSeconds,
    Vector3 CameraPosition,
    float CameraPitchDegrees,
    float CameraYawDegrees,
    RendererProfilerScenarioPostProcessSettings? PostProcessSettings = null);

internal sealed record RendererProfilerScenarioPlan(
    string Name,
    BethesdaGame ExpectedGame,
    string WorldspaceEditorId,
    IReadOnlyList<RendererProfilerScenarioStep> Steps,
    RendererProfilerScenarioFixture? Fixture = null);

internal static class RendererProfilerScenarioCatalog
{
    internal const string FnvWaterNightMatrix = "fnv-water-night-matrix";
    internal const string FnvCloudMotion = "fnv-cloud-motion";
    internal const string FnvCelestial = "fnv-celestial";
    internal const string FnvProspectorNeonBloom = "fnv-prospector-neon-bloom";

    internal static IReadOnlyList<string> Names { get; } =
    [
        FnvWaterNightMatrix,
        FnvCloudMotion,
        FnvCelestial,
        FnvProspectorNeonBloom,
    ];

    internal static bool TryNormalizeName(string? value, out string? normalized)
    {
        normalized = Names.FirstOrDefault(name =>
            string.Equals(name, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return normalized is not null;
    }

    internal static bool TryCreate(string? name, out RendererProfilerScenarioPlan? plan)
    {
        if (!TryNormalizeName(name, out var normalized))
        {
            plan = null;
            return false;
        }

        plan = normalized switch
        {
            FnvWaterNightMatrix => CreateWaterNightMatrix(),
            FnvCloudMotion => CreateCloudMotion(),
            FnvCelestial => CreateCelestial(),
            FnvProspectorNeonBloom => CreateProspectorNeonBloom(),
            _ => null,
        };
        return plan is not null;
    }

    private static RendererProfilerScenarioPlan CreateWaterNightMatrix()
    {
        var camera = new Vector3(78320f, 51000f, 2600f);
        return FnvPlan(FnvWaterNightMatrix,
        [
            new("noon", "NVWastelandClear", 12f, 0f, 0f, camera, -8f, 0f),
            new("night", "NVWastelandClear", 2f, 0f, 0f, camera, -8f, 0f),
        ]);
    }

    private static RendererProfilerScenarioPlan CreateCloudMotion()
    {
        var camera = new Vector3(78320f, 51000f, 3000f);
        return FnvPlan(FnvCloudMotion,
        [
            new("t-000", "NVWastelandClearWindy", 13f, 0f, 0f, camera, 5f, 0f),
            new("t-010", "NVWastelandClearWindy", 13f, 0f, 10f, camera, 5f, 0f),
        ]);
    }

    private static RendererProfilerScenarioPlan CreateCelestial()
    {
        var camera = new Vector3(78320f, 51000f, 3000f);
        // NVDefaultClimate's recovered Fallout rotated-arm path puts Masser at this direction at
        // 02:00. Aim the night-phase captures at the moon instead of the zenith: the old 80°/0°
        // pose missed it entirely, so different phase textures produced byte-identical sky frames
        // while the structural phase assertions still passed.
        const float moonPitchDegrees = 45.183f;
        const float moonYawDegrees = 125.538f;
        return FnvPlan(FnvCelestial,
        [
            new("night-full-d000", "NVWastelandClear", 2f, 0f, 0f, camera,
                moonPitchDegrees, moonYawDegrees),
            new("night-phase-d003", "NVWastelandClear", 2f, 3f, 0f, camera,
                moonPitchDegrees, moonYawDegrees),
            new("night-new-d012", "NVWastelandClear", 2f, 12f, 0f, camera,
                moonPitchDegrees, moonYawDegrees),
            new("night-cycle-d024", "NVWastelandClear", 2f, 24f, 0f, camera,
                moonPitchDegrees, moonYawDegrees),
            new("sunrise", "NVWastelandClear", 7f, 0f, 0f, camera, 80f, 0f),
            new("morning", "NVWastelandClear", 9f, 0f, 0f, camera, 80f, 0f),
            new("noon", "NVWastelandClear", 12f, 0f, 0f, camera, 80f, 0f),
            new("afternoon", "NVWastelandClear", 13f, 0f, 0f, camera, 80f, 0f),
            new("sunset", "NVWastelandClear", 19f, 0f, 0f, camera, 80f, 0f),
        ]);
    }

    private static RendererProfilerScenarioPlan CreateProspectorNeonBloom()
    {
        // FalloutNV.esm retail authority:
        //   ACTI 0x0016B5E1 NVProspectorSaloonNeonLights
        //   REFR 0x0016B5E4 in CELL 0x000DAEB9 / WastelandNV
        //   DATA position=(-67970.18,3904.494,8368.053), rotationZ=4.683187 rad.
        // The NIF's measured local AABB center is (-316,-333,411). The world renderer applies
        // -RotZ to placed geometry (PlacedReferenceTransform), so transform that local focus and
        // back the camera away along the facade normal. This keeps the fixture centered without a
        // hand-aimed bookmark that could silently drift away from its authored placement.
        var fixture = new RendererProfilerScenarioFixture(
            0x0016B5E4,
            0x0016B5E1,
            "NVProspectorSaloonNeonLights",
            @"architecture\Goodsprings\NV_ProspectorSaloon-Neon_Lights.NIF",
            new Vector3(-67970.18f, 3904.494f, 8368.053f),
            new Vector3(0f, 0f, 4.683187f));
        var focus = fixture.PlacementPosition + RotateZ(
            new Vector3(-316f, -333f, 411f),
            -fixture.PlacementRotationRadians.Z);
        var facadeDirection = Vector2.Normalize(new Vector2(
            focus.X - fixture.PlacementPosition.X,
            focus.Y - fixture.PlacementPosition.Y));
        var camera = focus + new Vector3(facadeDirection * 650f, 50f);
        var toFocus = focus - camera;
        var yaw = MathF.Atan2(toFocus.X, toFocus.Y) * (180f / MathF.PI);
        var pitch = MathF.Atan2(
            toFocus.Z,
            MathF.Sqrt((toFocus.X * toFocus.X) + (toFocus.Y * toFocus.Y))) * (180f / MathF.PI);
        var bloomOff = new RendererProfilerScenarioPostProcessSettings(
            HdrEnabled: true,
            BloomEnabled: false,
            ImagespaceEnabled: true,
            FogEnabled: true);
        var bloomOn = bloomOff with { BloomEnabled = true };

        return FnvPlan(FnvProspectorNeonBloom,
        [
            new("bloom-off", "NVWastelandClear", 2f, 0f, 0f, camera, pitch, yaw, bloomOff),
            new("bloom-on", "NVWastelandClear", 2f, 0f, 0f, camera, pitch, yaw, bloomOn),
        ], fixture);
    }

    private static Vector3 RotateZ(Vector3 value, float radians)
    {
        var sin = MathF.Sin(radians);
        var cos = MathF.Cos(radians);
        return new Vector3(
            (value.X * cos) - (value.Y * sin),
            (value.X * sin) + (value.Y * cos),
            value.Z);
    }

    private static RendererProfilerScenarioPlan FnvPlan(
        string name,
        IReadOnlyList<RendererProfilerScenarioStep> steps,
        RendererProfilerScenarioFixture? fixture = null) =>
        new(name, BethesdaGame.FalloutNewVegas, "WastelandNV", steps, fixture);
}
