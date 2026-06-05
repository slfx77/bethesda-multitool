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
    // A2C writes depth at per-sample granularity (each surviving sub-pixel deposits depth),
    // so for sort/group purposes it behaves like Cutout/Opaque, not like Blend.
    public bool WritesDepth => RenderMode != NifAlphaRenderMode.Blend;
}
