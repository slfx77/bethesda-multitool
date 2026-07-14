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
    GrassPositionQuantization PositionQuantization)
{
    internal static GrassScatterProfile ForGame(BethesdaGame game) => game switch
    {
        // Shipped iMinGrassSize=80 and iGrassDensityEvalSize=2. The INI's
        // iMaxGrassTypesPerTexture=2 is an inclusive zero-based index: the engine loop uses <=,
        // so up to three GNAM entries are consumed.
        BethesdaGame.FalloutNewVegas => new(
            true, 80f, 2, 3, 0f, GrassPositionQuantization.FloorWorldUnits),

        // TESV.exe 1.9.32 dynamic initializers: 20, 2 (inclusive => three entries), and 0.
        BethesdaGame.Skyrim => new(
            true, 20f, 2, 3, 0f, GrassPositionQuantization.HalfRelativeToTwelveCellBlock),

        // Fallout4_Default.ini ships iMinGrassSize=20; the PDB-backed manager uses eval radius 2
        // and the same inclusive maximum-entry loop.
        BethesdaGame.Fallout4 => new(
            true, 20f, 2, 3, 0f, GrassPositionQuantization.HalfRelativeToTwelveCellBlock),
        _ => default,
    };
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
