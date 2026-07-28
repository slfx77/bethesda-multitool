namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>Resolved transparency state for a NIF shape: render mode plus the raw NiAlphaProperty blend/test fields and material alpha.</summary>
internal readonly record struct NifAlphaRenderState(
    NifAlphaRenderMode RenderMode,
    bool HasAlphaBlend,
    bool HasAlphaTest,
    byte AlphaTestThreshold,
    byte AlphaTestFunction,
    byte SrcBlendMode,
    byte DstBlendMode,
    float MaterialAlpha,
    // DepthWritingBlend: an alpha-BLEND shape that the engine ALSO writes depth for. Per the decompiled
    // engine render-state setup (BSShader::SetupGeometryRenderStates), the alpha pass writes depth exactly
    // when the alpha-TEST bit is set — so this is a shape that BOTH blends and alpha-tests (e.g. NVSeaPlant02
    // foliage, window-cutout hulls). Its kept cutout texels are opaque, so it writes depth while staying a
    // blend. The reference renderer draws these inline BEFORE the water pass with a depth-writing blend PSO,
    // so water occludes them from above instead of them painting over it.
    bool DepthWritingBlend = false)
{
    // A2C writes depth at per-sample granularity (each surviving sub-pixel deposits depth), so for
    // sort/group purposes it behaves like Cutout/Opaque, not like Blend. A DepthWritingBlend shape stays
    // in Blend mode but ALSO writes depth (the reference renderer draws it pre-water).
    public bool WritesDepth => RenderMode != NifAlphaRenderMode.Blend || DepthWritingBlend;
}
