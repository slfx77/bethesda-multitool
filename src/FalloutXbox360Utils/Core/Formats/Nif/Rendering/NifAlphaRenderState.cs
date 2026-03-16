namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal readonly record struct NifAlphaRenderState(
    NifAlphaRenderMode RenderMode,
    bool HasAlphaBlend,
    bool HasAlphaTest,
    byte AlphaTestThreshold,
    byte AlphaTestFunction,
    byte SrcBlendMode,
    byte DstBlendMode,
    float MaterialAlpha)
{
    public bool WritesDepth => RenderMode != NifAlphaRenderMode.Blend;
}