using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Selects the staged sky route recovered from the FO3/FNV renderer. Later Bethesda games keep
///     their established unified route until their own pass ordering has an equally strong oracle.
/// </summary>
internal static class SkyRenderPassPolicy12
{
    public static bool UsesFallout3NewVegasSunOrder(BethesdaGame game)
    {
        return game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas;
    }
}

/// <summary>Subset of retained sky-NIF geometry to submit during one sky stage.</summary>
internal enum SkyGeometryPass12
{
    All,
    AtmosphereAndStars,
    Clouds
}

/// <summary>Subset of celestial billboards to submit during one sky stage.</summary>
internal enum SkyBillboardPass12
{
    All,
    SunBase,
    SunGlareAndMoons
}
