using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaRendererProfiler;

internal sealed record RendererProfilerScenarioPostProcessSettings(
    bool HdrEnabled,
    bool BloomEnabled,
    bool ImagespaceEnabled,
    bool FogEnabled,
    bool ShadowsEnabled = true);

internal sealed record RendererProfilerScenarioFixture(
    uint ReferenceFormId,
    uint BaseFormId,
    string BaseEditorId,
    string ModelPath,
    Vector3 PlacementPosition,
    Vector3 PlacementRotationRadians,
    uint? CellFormId = null);

/// <summary>
///     Profiler-only WATER001 positive fixture. It keeps the selected retail worldspace, terrain,
///     references, weather, and NVCleanWater material, but gives the water renderer exactly one
///     generated CELL packet. This isolates the single-material reconstructed route from the
///     per-WATR batched retail visibility set exercised by <c>fnv-water-night-matrix</c>.
/// </summary>
internal sealed record RendererProfilerScenarioSyntheticWaterFixture(
    uint SourceCellFormId,
    int GridX,
    int GridY,
    uint WaterFormId,
    float PlaneHeight);

internal sealed record RendererProfilerScenarioStep(
    string Id,
    string WeatherEditorId,
    float GameHour,
    float GameDay,
    float AnimationTimeSeconds,
    Vector3 CameraPosition,
    float CameraPitchDegrees,
    float CameraYawDegrees,
    RendererProfilerScenarioPostProcessSettings? PostProcessSettings = null,
    bool ClearAdaptedLightBeforeCapture = false);

internal sealed record RendererProfilerScenarioPlan(
    string Name,
    BethesdaGame ExpectedGame,
    string WorldspaceEditorId,
    IReadOnlyList<RendererProfilerScenarioStep> Steps,
    RendererProfilerScenarioFixture? Fixture = null,
    RendererProfilerScenarioSyntheticWaterFixture? SyntheticWaterFixture = null);

internal static class RendererProfilerScenarioCatalog
{
    internal const string FnvWaterNightMatrix = "fnv-water-night-matrix";
    internal const string FnvWater001Synthetic = "fnv-water001-synthetic";
    internal const string FnvCloudMotion = "fnv-cloud-motion";
    internal const string FnvCelestial = "fnv-celestial";
    internal const string FnvProspectorNeonBloom = "fnv-prospector-neon-bloom";
    internal const string FnvSunlightDimmer = "fnv-sunlight-dimmer";
    internal const string FnvAdaptationHistory = "fnv-adaptation-history";
    internal const string FnvWeatherImageSpaceBands = "fnv-weather-imagespace-bands";
    internal const string FnvActiveAdtBase = "fnv-active-adt-base";
    internal const string FnvSls1009Sls1013Alias = "fnv-sls1009-sls1013";

    internal static IReadOnlyList<string> Names { get; } =
    [
        FnvWaterNightMatrix,
        FnvWater001Synthetic,
        FnvCloudMotion,
        FnvCelestial,
        FnvProspectorNeonBloom,
        FnvSunlightDimmer,
        FnvAdaptationHistory,
        FnvWeatherImageSpaceBands,
        FnvActiveAdtBase
    ];

    internal static bool TryNormalizeName(string? value, out string? normalized)
    {
        var requested = value?.Trim();
        if (string.Equals(requested, FnvSls1009Sls1013Alias, StringComparison.OrdinalIgnoreCase))
        {
            normalized = FnvActiveAdtBase;
            return true;
        }

        normalized = Names.FirstOrDefault(name =>
            string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
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
            FnvWater001Synthetic => CreateWater001Synthetic(),
            FnvCloudMotion => CreateCloudMotion(),
            FnvCelestial => CreateCelestial(),
            FnvProspectorNeonBloom => CreateProspectorNeonBloom(),
            FnvSunlightDimmer => CreateSunlightDimmer(),
            FnvAdaptationHistory => CreateAdaptationHistory(),
            FnvWeatherImageSpaceBands => CreateWeatherImageSpaceBands(),
            FnvActiveAdtBase => CreateActiveAdtBase(),
            _ => null
        };
        return plan is not null;
    }

    private static RendererProfilerScenarioPlan CreateWaterNightMatrix()
    {
        var camera = new Vector3(78320f, 51000f, 2600f);
        return FnvPlan(FnvWaterNightMatrix,
        [
            new RendererProfilerScenarioStep("noon", "NVWastelandClear", 12f, 0f, 0f, camera, -8f, 0f),
            new RendererProfilerScenarioStep("night", "NVWastelandClear", 2f, 0f, 0f, camera, -8f, 0f)
        ]);
    }

    private static RendererProfilerScenarioPlan CreateWater001Synthetic()
    {
        // CELL 0x000DDCF8 is the exact retail Lake Mead authority used by the night matrix:
        // grid (19,12), XCLW 2600, XCWT NVCleanWater 0x001009CA, and an authored LAND heightmap.
        // Aim steeply at the center of that tile from 800 units above the plane. The profiler-only
        // fixture retains its opaque LAND but replaces the water visibility set with this one tile,
        // satisfying WATER001's bounded homogeneous-plane/type contract deterministically.
        var camera = new Vector3(79872f, 51200f, 3400f);
        var postProcess = new RendererProfilerScenarioPostProcessSettings(
            true,
            false,
            true,
            true);
        var fixture = new RendererProfilerScenarioSyntheticWaterFixture(
            0x000DDCF8,
            19,
            12,
            0x001009CA,
            2600f);

        return new RendererProfilerScenarioPlan(
            FnvWater001Synthetic,
            BethesdaGame.FalloutNewVegas,
            "WastelandNV",
            [
                new RendererProfilerScenarioStep(
                    "positive",
                    "NVWastelandClear",
                    12f,
                    0f,
                    0f,
                    camera,
                    -65f,
                    0f,
                    postProcess,
                    true)
            ],
            SyntheticWaterFixture: fixture);
    }

    private static RendererProfilerScenarioPlan CreateCloudMotion()
    {
        var camera = new Vector3(78320f, 51000f, 3000f);
        // Cloud motion needs the retail post stack, but projected sun shadows only add an unrelated
        // cross-process shadow-map edge variable over the distant terrain. Pin every visual toggle
        // explicitly and isolate that pass so the image delta belongs to the animation clock.
        var isolated = new RendererProfilerScenarioPostProcessSettings(
            true,
            true,
            true,
            true,
            false);
        return FnvPlan(FnvCloudMotion,
        [
            new RendererProfilerScenarioStep("t-000", "NVWastelandClearWindy", 13f, 0f, 0f, camera, 5f, 0f,
                isolated, true),
            new RendererProfilerScenarioStep("t-010", "NVWastelandClearWindy", 13f, 0f, 10f, camera, 5f, 0f,
                isolated, true)
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
            new RendererProfilerScenarioStep("night-full-d000", "NVWastelandClear", 2f, 0f, 0f, camera,
                moonPitchDegrees, moonYawDegrees),
            new RendererProfilerScenarioStep("night-phase-d003", "NVWastelandClear", 2f, 3f, 0f, camera,
                moonPitchDegrees, moonYawDegrees),
            new RendererProfilerScenarioStep("night-new-d012", "NVWastelandClear", 2f, 12f, 0f, camera,
                moonPitchDegrees, moonYawDegrees),
            new RendererProfilerScenarioStep("night-cycle-d024", "NVWastelandClear", 2f, 24f, 0f, camera,
                moonPitchDegrees, moonYawDegrees),
            new RendererProfilerScenarioStep("sunrise", "NVWastelandClear", 7f, 0f, 0f, camera, 80f, 0f),
            new RendererProfilerScenarioStep("morning", "NVWastelandClear", 9f, 0f, 0f, camera, 80f, 0f),
            new RendererProfilerScenarioStep("noon", "NVWastelandClear", 12f, 0f, 0f, camera, 80f, 0f),
            new RendererProfilerScenarioStep("afternoon", "NVWastelandClear", 13f, 0f, 0f, camera, 80f, 0f),
            new RendererProfilerScenarioStep("sunset", "NVWastelandClear", 19f, 0f, 0f, camera, 80f, 0f)
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
            MathF.Sqrt(toFocus.X * toFocus.X + toFocus.Y * toFocus.Y)) * (180f / MathF.PI);
        var bloomOff = new RendererProfilerScenarioPostProcessSettings(
            true,
            false,
            true,
            true);
        var bloomOn = bloomOff with { BloomEnabled = true };

        return FnvPlan(FnvProspectorNeonBloom,
        [
            new RendererProfilerScenarioStep("bloom-off", "NVWastelandClear", 2f, 0f, 0f, camera, pitch, yaw, bloomOff),
            new RendererProfilerScenarioStep("bloom-on", "NVWastelandClear", 2f, 0f, 0f, camera, pitch, yaw, bloomOn)
        ], fixture);
    }

    private static RendererProfilerScenarioPlan CreateSunlightDimmer()
    {
        var camera = new Vector3(78320f, 51000f, 2600f);
        var enabled = new RendererProfilerScenarioPostProcessSettings(
            true,
            false,
            true,
            true);

        return FnvPlan(FnvSunlightDimmer,
        [
            new RendererProfilerScenarioStep("hdr-imagespace-on", "NVWastelandClear", 12f, 0f, 0f, camera, -8f, 0f,
                enabled),
            new RendererProfilerScenarioStep("imagespace-off", "NVWastelandClear", 12f, 0f, 0f, camera, -8f, 0f,
                enabled with { ImagespaceEnabled = false }),
            new RendererProfilerScenarioStep("hdr-off", "NVWastelandClear", 12f, 0f, 0f, camera, -8f, 0f,
                enabled with { HdrEnabled = false })
        ]);
    }

    private static RendererProfilerScenarioPlan CreateAdaptationHistory()
    {
        // FalloutNV.esm's only exterior CELL XCIM is CELL 0x00084336 at grid (6,-5). Its XCIM is
        // NVDefaultExterior, the same IMGS selected by WastelandNV INAM, making this an exact history
        // continuity gate with no authored grade change at the one-unit boundary crossing.
        var west = new Vector3(24575.5f, -18432f, 5000f);
        var east = new Vector3(24576.5f, -18432f, 5000f);
        var enabled = new RendererProfilerScenarioPostProcessSettings(
            true,
            false,
            true,
            true);

        return FnvPlan(FnvAdaptationHistory,
        [
            new RendererProfilerScenarioStep("west-worldspace", "NVWastelandClear", 12f, 0f, 0f, west, -8f, 0f,
                enabled),
            new RendererProfilerScenarioStep("east-cell", "NVWastelandClear", 12f, 0f, 0f, east, -8f, 0f, enabled),
            new RendererProfilerScenarioStep("east-explicit-clear", "NVWastelandClear", 12f, 0f, 0f, east, -8f, 0f,
                enabled,
                true)
        ]);
    }

    private static RendererProfilerScenarioPlan CreateWeatherImageSpaceBands()
    {
        // Retail discriminating fixture: NVColoradoRiverWeather uses different Day and HighNoon
        // adapters. Sky::UpdateHDRValues intentionally makes Day peak at 12:00, opposite the
        // PNAM/NAM0 color clock whose HighNoon color peaks then.
        var camera = new Vector3(78320f, 51000f, 2600f);
        var enabled = new RendererProfilerScenarioPostProcessSettings(
            true,
            false,
            true,
            true);

        return FnvPlan(FnvWeatherImageSpaceBands,
        [
            new RendererProfilerScenarioStep("morning-shoulder", "NVColoradoRiverWeather", 10f, 0f, 0f,
                camera, -8f, 0f, enabled, true),
            new RendererProfilerScenarioStep("noon", "NVColoradoRiverWeather", 12f, 0f, 0f,
                camera, -8f, 0f, enabled, true),
            new RendererProfilerScenarioStep("afternoon-shoulder", "NVColoradoRiverWeather", 15f, 0f, 0f,
                camera, -8f, 0f, enabled, true)
        ]);
    }

    private static RendererProfilerScenarioPlan CreateActiveAdtBase()
    {
        // FalloutNV.esm retail mixed-route authority:
        //   STAT 0x00176233 urbangatedwallstrStone01NV
        //   REFR 0x000A6826 in CELL 0x000E1A03 PrimmParkingLot / WastelandNV
        //   DATA position=(-55889.066,-47042.832,5753.3906), rotationZ=pi.
        // Local block 21 is the sole opaque type-1/vertex-color geometry eligible for the active
        // ID193/BSSM_ADT/SLS2000 route. Blocks 15 and 25 are alpha-tested neighbors and must remain
        // on the bounded fallback. Center the complete local AABB after the runtime -RotZ placement
        // transform, then back up 650 units from the facade.
        var fixture = new RendererProfilerScenarioFixture(
            0x000A6826,
            0x00176233,
            "urbangatedwallstrStone01NV",
            @"architecture\urban\civicspace\gatedwall\urbangatedwallstrstone01_nv.nif",
            new Vector3(-55889.066f, -47042.832f, 5753.3906f),
            new Vector3(0f, 0f, MathF.PI),
            0x000E1A03);
        var focus = fixture.PlacementPosition + RotateZ(
            new Vector3(0.0001f, -26.0110f, 174.81065f),
            -fixture.PlacementRotationRadians.Z);
        var camera = focus + new Vector3(0f, 650f, 50f);
        var toFocus = focus - camera;
        var yaw = MathF.Atan2(toFocus.X, toFocus.Y) * (180f / MathF.PI);
        var pitch = MathF.Atan2(
            toFocus.Z,
            MathF.Sqrt(toFocus.X * toFocus.X + toFocus.Y * toFocus.Y)) * (180f / MathF.PI);
        var isolated = new RendererProfilerScenarioPostProcessSettings(
            false,
            false,
            false,
            false,
            false);

        return FnvPlan(FnvActiveAdtBase,
        [
            new RendererProfilerScenarioStep(
                "retail-mixed",
                "NVWastelandClear",
                12f,
                0f,
                0f,
                camera,
                pitch,
                yaw,
                isolated,
                true)
        ], fixture);
    }

    private static Vector3 RotateZ(Vector3 value, float radians)
    {
        var sin = MathF.Sin(radians);
        var cos = MathF.Cos(radians);
        return new Vector3(
            value.X * cos - value.Y * sin,
            value.X * sin + value.Y * cos,
            value.Z);
    }

    private static RendererProfilerScenarioPlan FnvPlan(
        string name,
        IReadOnlyList<RendererProfilerScenarioStep> steps,
        RendererProfilerScenarioFixture? fixture = null)
    {
        return new RendererProfilerScenarioPlan(name, BethesdaGame.FalloutNewVegas, "WastelandNV", steps, fixture);
    }
}
