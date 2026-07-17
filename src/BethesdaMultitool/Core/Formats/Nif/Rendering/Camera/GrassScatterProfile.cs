using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

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
            null, false, default),

        // Fallout4_Default.ini ships iMinGrassSize=20; the PDB-backed manager uses eval radius 2
        // and the same inclusive maximum-entry loop.
        BethesdaGame.Fallout4 => new(
            true, 20f, 2, 3, 0f, GrassPositionQuantization.HalfRelativeToTwelveCellBlock,
            null, false, default),
        _ => default,
    };
}

/// <summary>
///     Authored grass-distance envelope in horizontal world units. This slice records the retail
///     fade start/range but deliberately enforces only the hard end; fading remains an engine
///     shader behavior and must not be approximated by changing material alpha or mip selection.
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
///     Shared establishment/exact-cull predicate for the grass hard end. Establishment may pass the
///     cull-cache drift slack so its survivor list remains a superset; per-frame filtering passes zero
///     slack and therefore remains exact while a frozen batch is reused.
/// </summary>
internal static class GrassDistanceCullPolicy
{
    /// <summary>
    ///     Batch identity for the hard-end policy. Raw <c>IsGrass</c> is intentionally insufficient:
    ///     Skyrim/FO4 grass has no recovered envelope in this slice and must retain the established
    ///     non-grass batch topology.
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
