namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>Resolved transparency state for a NIF shape: render mode plus the raw NiAlphaProperty blend/test fields and material alpha.</summary>
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
