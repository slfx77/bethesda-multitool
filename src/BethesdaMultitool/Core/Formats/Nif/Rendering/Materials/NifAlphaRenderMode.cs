namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>How a NIF shape's transparency is rasterized, derived from its NiAlphaProperty.</summary>
internal enum NifAlphaRenderMode
{
    Opaque,
    Cutout,
    Blend,

    /// <summary>
    ///     Alpha-to-coverage: pixels are alpha-tested against a stochastic per-pixel threshold
    ///     (Bayer 2x2 in screen space). Pass = opaque write with depth, fail = discard. Combined
    ///     with the supersampled rasterization buffer + downsample (or hardware MSAA on the GPU),
    ///     this produces sub-pixel-soft edges without blend stacking — Bethesda's engine uses
    ///     `BSRenderState::SetAlphaToCoverageEnable` for hair / brow / lash for the same reason.
    /// </summary>
    AlphaToCoverage
}
