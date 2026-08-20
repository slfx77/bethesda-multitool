namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Resolved transparency state for a NIF shape: render mode plus the raw NiAlphaProperty blend/test fields and
///     material alpha.
/// </summary>
internal readonly record struct NifAlphaRenderState(
    NifAlphaRenderMode RenderMode,
    bool HasAlphaBlend,
    bool HasAlphaTest,
    byte AlphaTestThreshold,
    byte AlphaTestFunction,
    byte SrcBlendMode,
    byte DstBlendMode,
    float MaterialAlpha,
    // DepthWritingBlend: the LEGACY viewer classification — a shape that both blends and
    // alpha-tests with a cutting threshold (e.g. NVSeaPlant02 foliage, window-cutout hulls) is
    // drawn with a depth-writing blend PSO before the water pass, so water occludes it from
    // above. This is a viewer heuristic, NOT the engine's rule (see EngineZWriteOff); it keeps
    // serving every stream-off route (legacy pass order, non-FNV games, ortho, exports).
    bool DepthWritingBlend = false,
    // ENGINE z-write rule (decompile-proven — memory note fnv_alpha_zwrite_order_re_2026_08_04):
    // z-write is ambient-ON for the alpha pass and is turned OFF only for decals (Shader Flags
    // bits 26/27), hair (bit 18 + blend; the engine's tech-6/8 pass also drops color-write — a
    // multi-pass pipeline we approximate), and the BSShaderNoLighting family honoring
    // Shader Flags 2 bit 0 "zbuffer write" (CLEAR ⇒ OFF; e.g. FXMistLow01Long's SmokeWispsTile
    // sheet authors 0x0). Ordinary blended LIT geometry KEEPS z-write — that is what resolves
    // WhiteHorseNettle. Consumed only by the unified transparency stream's per-draw PSO choice;
    // computed only from authored shader metadata — an evidence-less shape (particle systems,
    // NiTexturing-era games, unmatched property types) MIRRORS the legacy classification
    // instead of guessing a lit default.
    bool EngineZWriteOff = false,
    // NoLighting shapes also honor Shader Flags bit 31 "zbuffer test": CLEAR ⇒ depth test OFF
    // (the authored default 0x82000000 has it SET, so this is rare). Same stream-only consumer.
    bool DepthTestOff = false)
{
    // A2C writes depth at per-sample granularity (each surviving sub-pixel deposits depth), so for
    // sort/group purposes it behaves like Cutout/Opaque, not like Blend. A DepthWritingBlend shape stays
    // in Blend mode but ALSO writes depth (the reference renderer draws it pre-water).
    public bool WritesDepth => RenderMode != NifAlphaRenderMode.Blend || DepthWritingBlend;
}
