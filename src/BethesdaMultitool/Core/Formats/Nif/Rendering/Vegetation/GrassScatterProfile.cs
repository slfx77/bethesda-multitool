using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;

internal enum GrassPositionQuantization
{
    None,
    FloorWorldUnits,
    HalfRelativeToTwelveCellBlock,
}

/// <summary>
///     Engine-family grass scatter constants. The candidate formula and defaults are recovered from
///     FNV's MemDebug XEX/PDB, retail Skyrim 1.9.32's executable/MAP, and Fallout 4's official PDB.
/// </summary>
internal readonly record struct GrassScatterProfile(
    bool Supported,
    float MinGrassSize,
    int EvalRadius,
    int MaxGrassEntriesPerTexture,
    float TexturePercentageThreshold,
    GrassPositionQuantization PositionQuantization,
    TerrainTriangleTopology? TerrainTopology,
    bool FloorSampledHeight,
    GrassDistanceEnvelope DistanceEnvelope)
{
    internal static GrassScatterProfile ForGame(BethesdaGame game) => game switch
    {
        // Oblivion_default.ini [Grass]: iMinGrassSize=80, iGrassDensityEvalSize=2,
        // iMaxGrassTypesPerTexure=2 (sic — same inclusive zero-based semantics as the descendant
        // FNV loop, so up to three LTEX GNAM entries), fGrassStartFadeDistance=2000 with
        // fGrassEndDistance=3000 (TES4 authors an END distance, not a range → range 1000).
        // Position quantization, terrain topology, and height flooring are UNVERIFIED for TES4
        // (no CreateGrass decompile yet) — conservative None/null/false until recovered; the FNV
        // values are plausible ancestry but not proven.
        BethesdaGame.Oblivion => new(
            true, 80f, 2, 3, 0f, GrassPositionQuantization.None,
            null, false,
            new GrassDistanceEnvelope(FadeStart: 2000f, FadeRange: 1000f)),

        // Shipped iMinGrassSize=80 and iGrassDensityEvalSize=2. The INI's
        // iMaxGrassTypesPerTexture=2 is an inclusive zero-based index: the engine loop uses <=,
        // so up to three GNAM entries are consumed. CreateGrass floors jittered XY, queries
        // TESObjectLAND's checkerboard triangle planes, then floors the returned Z.
        BethesdaGame.FalloutNewVegas => new(
            true, 80f, 2, 3, 0f, GrassPositionQuantization.FloorWorldUnits,
            TerrainTriangleTopology.AlternatingCheckerboard, true,
            new GrassDistanceEnvelope(FadeStart: 7000f, FadeRange: 1000f)),

        // TESV.exe 1.9.32 dynamic initializers: 20, 2 (inclusive => three entries), and 0.
        BethesdaGame.Skyrim => new(
            true, 20f, 2, 3, 0f, GrassPositionQuantization.HalfRelativeToTwelveCellBlock,
            null, false,
            // TESV 1.9.32 retail defaults: fGrassStartFadeDistance=3500 and
            // fGrassFadeRange=1000. Like the recovered FNV path, the shader's fade signal is an
            // endpoint gate rather than a gradual opacity fade.
            new GrassDistanceEnvelope(FadeStart: 3500f, FadeRange: 1000f)),

        // Fallout4_Default.ini ships iMinGrassSize=20; the PDB-backed manager uses eval radius 2
        // and the same inclusive maximum-entry loop.
        BethesdaGame.Fallout4 => new(
            true, 20f, 2, 3, 0f, GrassPositionQuantization.HalfRelativeToTwelveCellBlock,
            null, false, default),
        _ => default,
    };
}

/// <summary>
///     Viewer grass-distance envelope in horizontal world units. The viewer keeps an authoritative CPU
///     hard end and does not invent alpha, RGB, or mip fading.
///     <para>
///         Retail behavior is NOT uniform across games, so do not re-generalize this: for FNV and
///         Skyrim the engine supplies the configured start/range to a transformed-origin shader
///         metric and turns the result into only a zero/nonzero endpoint gate — not smooth opacity
///         (see <c>docs/research/grass_placement_pipeline.md</c>). OBLIVION differs: its
///         <c>GRASS2020.vso</c> (retail <c>Shaders\shaderpackage019.sdp</c>, disassembled 2026-07-25)
///         computes a genuine LINEAR opacity ramp —
///         <c>oT5.w = saturate((d - AlphaParam.x) / AlphaParam.y) * (1 - saturate((d - AlphaParam.z) / AlphaParam.w))</c>
///         — which its pixel shader multiplies straight into a blended output with no binarization.
///         The viewer applies its hard end (2000 → 3000) for TES4 instead. Reproducing the smooth
///         TES4 ramp requires the grass draw to BLEND, which it now does again: the 2026-07-25
///         cutout/A2C reroute was reverted 2026-07-26 after retail in-game oracles showed soft blade
///         silhouettes, so the smooth ramp is implementable here whenever it is picked up.
///     </para>
/// </summary>
internal readonly record struct GrassDistanceEnvelope(float FadeStart, float FadeRange)
{
    internal bool Enabled =>
        float.IsFinite(FadeStart) && FadeStart >= 0f &&
        float.IsFinite(FadeRange) && FadeRange >= 0f &&
        float.IsFinite(FadeStart + FadeRange) &&
        FadeStart + FadeRange > 0f;

    internal float HardEnd => Enabled ? FadeStart + FadeRange : float.PositiveInfinity;

    /// <summary>The grass hard end cannot extend beyond the active horizontal render radius.</summary>
    internal float EffectiveHardEnd(float activeRenderDistance)
    {
        if (!Enabled) return float.PositiveInfinity;
        if (!float.IsFinite(activeRenderDistance)) return HardEnd;
        return MathF.Min(HardEnd, MathF.Max(0f, activeRenderDistance));
    }
}

/// <summary>
///     Recovered FNV GRASS2000 phase and distance signals. The GRAS field is intentionally named a
///     multiplier: retail SetupGeometryConstants multiplies it directly by the default shader timer
///     divided by 3600, then multiplied by 2π. The available evidence does not establish the timer's
///     units and does not justify interpreting the multiplier as seconds or taking its reciprocal.
///     Wind displacement is fixed world +Y with a 5→125 setting interpolation, and vertex alpha is
///     squared.
/// </summary>
internal static class FnvTallGrassWind
{
    internal const float SpatialPhaseDivisor = 128f;
    // Retail defaults for the fGrassWindMagnitudeMin/Max settings. The shader lerps these
    // world-unit amplitudes with BSShaderManager::fWindMagnitude (weather wind byte / 255).
    internal const float GrassWindMagnitudeMinDefault = 5f;
    internal const float GrassWindMagnitudeMaxDefault = 125f;
    internal const double TimerUnitsPerCycleBase = 3600.0;
    internal const double TwoPi = Math.PI * 2.0;

    /// <summary>
    ///     The recovered GRASS2000 wind contract is specific to Fallout: New Vegas. Keep this
    ///     capability separate from the grass-distance envelope: another game may acquire a
    ///     distance policy without thereby opting into FNV shader constants.
    /// </summary>
    internal static bool IsSupported(BethesdaGame game) =>
        game == BethesdaGame.FalloutNewVegas;

    internal static float SanitizeWaveMultiplier(float value) =>
        float.IsFinite(value) && value > 0f ? value : 0f;

    internal static float ComputeWindMagnitude(float windMagnitudeFraction)
    {
        var fraction = float.IsFinite(windMagnitudeFraction)
            ? Math.Clamp(windMagnitudeFraction, 0f, 1f)
            : 0f;
        return GrassWindMagnitudeMinDefault +
               (GrassWindMagnitudeMaxDefault - GrassWindMagnitudeMinDefault) * fraction;
    }

    internal static float ComputeTimePhaseRadians(double timerValue, float grassWaveMultiplier)
    {
        var multiplier = SanitizeWaveMultiplier(grassWaveMultiplier);
#pragma warning disable S1244 // exact zero is the "wave disabled" sentinel produced by SanitizeWaveMultiplier
        if (!double.IsFinite(timerValue) || multiplier == 0f)
#pragma warning restore S1244
        {
            return 0f;
        }

        var phase = timerValue / TimerUnitsPerCycleBase * multiplier * TwoPi;
        phase %= TwoPi;
        if (phase < 0.0) phase += TwoPi;
        return (float)phase;
    }

    internal static Vector2 EvaluateWorldOffset(
        Vector2 absolutePlacement,
        float windMagnitude,
        double timerValue,
        float grassWaveMultiplier,
        float authoredVertexAlpha)
    {
#pragma warning disable S1244 // exact zero is the "wind disabled" sentinel; near-zero magnitudes still sway
        if (!float.IsFinite(windMagnitude) || windMagnitude == 0f)
#pragma warning restore S1244
        {
            return Vector2.Zero;
        }

        var weight = Math.Clamp(authoredVertexAlpha, 0f, 1f);
        var phase = (absolutePlacement.X + absolutePlacement.Y) / SpatialPhaseDivisor +
                    ComputeTimePhaseRadians(timerValue, grassWaveMultiplier);
        var amplitude = MathF.Sin(phase) * windMagnitude * weight * weight;
        return Vector2.UnitY * amplitude;
    }

    /// <summary>
    ///     Documents the configured 7000→8000 envelope using the viewer's horizontal culling metric.
    ///     Retail GRASS2000 evaluates its analogous signal from a transformed instance origin, then
    ///     turns the square into a binary output-alpha gate. This helper is therefore a culling-envelope
    ///     oracle, not an exact shader-space distance oracle and not a gradual-opacity or RGB fade.
    ///     Production visibility stays with the authoritative hard end.
    /// </summary>
    internal static float ComputeConfiguredEnvelopeSignal(
        float horizontalDistance,
        in GrassDistanceEnvelope envelope)
    {
        if (!envelope.Enabled) return 1f;
        if (!float.IsFinite(horizontalDistance)) return 0f;
        if (envelope.FadeRange <= 0f) return horizontalDistance < envelope.HardEnd ? 1f : 0f;
        return 1f - Math.Clamp(
            (MathF.Max(horizontalDistance, 0f) - envelope.FadeStart) / envelope.FadeRange,
            0f,
            1f);
    }
}

// REMOVED 2026-07-26: GrassCutoutPolicy / ReferenceRenderer12.IsGrassCutout. TES4 grass was rerouted
// from the sorted-blend path to the alpha-to-coverage cutout path on 2026-07-25, on the strength of a
// shaderpackage019 read claiming GRASS2002-2008.pso binarizes diffuse alpha. Retail IN-GAME oracles
// (user, 2026-07-26) show SOFT blade silhouettes, and the cutout made every card's texture boundary
// razor-sharp — with only 7-10 crossed cards per authored grass NIF (groundcovermediumgrass01 = 28
// verts / 14 tris, gclonggrass01 = 40 / 20) that exposed the individual quads the retail blend hides,
// which the user reported as "a couple triangles with harsh cutoffs". Reverted to the committed
// blend path. NOTE the shader claim was never persisted to an artifact and remains UNVERIFIED; the
// authored NiAlphaProperty (0x12ED = blend AND test @64-128) is consistent with the engine both
// testing and blending, which is exactly what the restored path does.

/// <summary>
///     Shared establishment/exact-cull predicate for the grass hard end. Establishment may pass the
///     cull-cache drift slack so its survivor list remains a superset; per-frame filtering passes zero
///     slack and therefore remains exact while a frozen batch is reused.
/// </summary>
internal static class GrassDistanceCullPolicy
{
    /// <summary>
    ///     Batch identity for the hard-end policy. Raw <c>IsGrass</c> is intentionally insufficient:
    ///     only game profiles with a recovered envelope split the established non-grass topology.
    /// </summary>
    internal static bool UsesEnvelope(bool isGrass, in GrassDistanceEnvelope envelope)
        => isGrass && envelope.Enabled;

    /// <summary>
    ///     Grass needs a per-instance copy even when generic tolerant/frustum refiltering is off.
    ///     In particular, disabling reference-frustum culling must not turn a widened 8512-unit
    ///     establishment survivor into a draw beyond the exact 8000-unit hard end.
    /// </summary>
    internal static bool RequiresExactPerInstanceFiltering(
        bool genericRefilter,
        bool usesGrassDistanceEnvelope)
        => genericRefilter || usesGrassDistanceEnvelope;

    internal static bool Passes(
        bool isGrass,
        in GrassDistanceEnvelope envelope,
        float horizontalDistanceSquared,
        float activeRenderDistance,
        float establishmentSlack = 0f)
    {
        if (!UsesEnvelope(isGrass, in envelope)) return true;
        if (!float.IsFinite(horizontalDistanceSquared) || horizontalDistanceSquared < 0f) return false;

        var hardEnd = envelope.EffectiveHardEnd(activeRenderDistance);
        var slack = float.IsFinite(establishmentSlack) ? MathF.Max(0f, establishmentSlack) : 0f;
        var reach = hardEnd + slack;
        return horizontalDistanceSquared <= reach * reach;
    }
}

internal static class GrassPositionQuantizer
{
    internal static void Quantize(
        ref float x,
        ref float y,
        int cellX,
        int cellY,
        float cellSize,
        GrassPositionQuantization quantization)
    {
        switch (quantization)
        {
            case GrassPositionQuantization.FloorWorldUnits:
                // FNV's CreateGrass explicitly floors both jittered world coordinates before the
                // terrain query. This matters for negative cells, where truncation would be wrong.
                x = MathF.Floor(x);
                y = MathF.Floor(y);
                break;
            case GrassPositionQuantization.HalfRelativeToTwelveCellBlock:
                // Skyrim/FO4 round-trip XY through binary16 relative to (cell / 12) * 12. The
                // recovered signed integer division truncates toward zero, so negative cells
                // -1..-11 deliberately use block origin zero rather than geometric floor(-1/12).
                var blockOriginX = (cellX / 12) * 12 * cellSize;
                var blockOriginY = (cellY / 12) * 12 * cellSize;
                x = blockOriginX + (float)(Half)(x - blockOriginX);
                y = blockOriginY + (float)(Half)(y - blockOriginY);
                break;
            case GrassPositionQuantization.None:
            default:
                break;
        }
    }
}
