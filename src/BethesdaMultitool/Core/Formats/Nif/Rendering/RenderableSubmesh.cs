using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     One geometry block's renderable data with transforms already applied.
/// </summary>
internal sealed class RenderableSubmesh
{
    /// <summary>
    ///     Sentinel <see cref="DiffuseTexturePath" /> marking placed water-shader geometry
    ///     (WaterShaderProperty NIFs carry no diffuse). The D3D12 mesh cache maps it to
    ///     <c>GpuTextureCache12.WaterSurface</c> so cave/pool water renders as translucent blue water
    ///     rather than the opaque-white fallback. Reuses the diffuse-path string so no new field has
    ///     to be threaded through the decode + persistent-cache pipeline.
    /// </summary>
    public const string WaterSurfaceTexturePath = "fallout:water-surface";

    /// <summary>Name of the source NiTriShape/NiTriStrips block, if available.</summary>
    public string? ShapeName { get; init; }

    /// <summary>X, Y, Z per vertex (length = numVertices * 3).</summary>
    public required float[] Positions { get; init; }

    /// <summary>
    ///     Serialized NiGeometryData NiBound transformed into the same baked mesh-root-local frame
    ///     as <see cref="Positions" />. Null for generated/deformed geometry or malformed authored
    ///     bounds; the reference decoder then derives a deterministic sphere from the vertices.
    /// </summary>
    public NifLocalBounds? LocalBounds { get; set; }

    /// <summary>3 indices per triangle (length = numTriangles * 3).</summary>
    public required ushort[] Triangles { get; init; }

    /// <summary>X, Y, Z per vertex (optional, for shading). Same length as Positions.</summary>
    public float[]? Normals { get; init; }

    /// <summary>U, V per vertex (optional, for texture mapping). Length = numVertices * 2.</summary>
    public float[]? UVs { get; init; }

    /// <summary>R, G, B, A per vertex (optional). Length = numVertices * 4.</summary>
    public byte[]? VertexColors { get; init; }

    /// <summary>Tangent X, Y, Z per vertex (optional, for bump mapping). Same length as Positions.</summary>
    public float[]? Tangents { get; set; }

    /// <summary>Bitangent X, Y, Z per vertex (optional, for bump mapping). Same length as Positions.</summary>
    public float[]? Bitangents { get; set; }

    /// <summary>Diffuse texture path resolved from shader properties (e.g., "textures\architecture\foo.dds").</summary>
    public string? DiffuseTexturePath { get; set; }

    /// <summary>
    ///     Clamp the diffuse/material texture on U instead of wrapping it. FO4/FO76 external
    ///     materials author this as the inverse of the BGSM/BGEM <c>TileU</c> flag.
    /// </summary>
    public bool ClampTextureU { get; set; }

    /// <summary>
    ///     Clamp the diffuse/material texture on V instead of wrapping it. FO4/FO76 external
    ///     materials author this as the inverse of the BGSM/BGEM <c>TileV</c> flag.
    /// </summary>
    public bool ClampTextureV { get; set; }

    /// <summary>Normal map texture path resolved from shader properties (slot 1).</summary>
    public string? NormalMapTexturePath { get; set; }

    /// <summary>
    ///     FO4/FO76 specular map path (<c>_s.dds</c>: R = per-texel specular mask) from the BGSM
    ///     material (slot 6 on FO4, slot 8 reflectance on FO76). Null for games whose mask rides the
    ///     normal map's alpha (FNV DXT5) or when the material has no specular texture.
    /// </summary>
    public string? SpecularMapTexturePath { get; set; }

    /// <summary>
    ///     FO4/FO76 grayscale-to-palette texture (BGSM slot 3), or null. The shader replaces the
    ///     diffuse RGB with <c>palette.Sample(u: diffuse.G, v: GradientMapV × vertexColor.R)</c> —
    ///     without it, palette-tinted assets render their authoring base (FO4's lavender bricks).
    /// </summary>
    public string? GradientMapTexturePath { get; set; }

    /// <summary>Palette row for the grayscale-to-palette lookup (0–1); meaningless when the map is null.</summary>
    public float GradientMapV { get; set; }

    /// <summary>
    ///     FO4 environment/cubemap texture (BGSM slot 4 — typically
    ///     <c>shared\cubemaps\mipblur_defaultoutside1.dds</c>), or null. The shader adds
    ///     <c>cube(reflect(V,N)) × EnvironmentMapScale × _s.R × g(N·V)</c> — the dominant FO4
    ///     "shiny" term; without it metal/gloss reads as matte diffuse.
    /// </summary>
    public string? EnvironmentMapTexturePath { get; set; }

    /// <summary>
    ///     Environment reflection strength: fo76utils <c>min(envScale × specular strength, 8)</c>.
    ///     0 disables the term. Distinct from the FNV eye-cubemap <see cref="EnvMapScale" />.
    /// </summary>
    public float EnvironmentMapScale { get; set; }

    /// <summary>
    ///     Material specular smoothness (0–1) for the env term's gloss: selects the cube mip
    ///     ((1−smoothness)·maxMip) and the geometry factor, per-texel modulated by the
    ///     <c>_s</c> map's G channel in the shader (fo76utils drawPixel_FO4).
    /// </summary>
    public float EnvironmentMapSmoothness { get; set; }

    /// <summary>
    ///     FO3/FNV classic PP-lighting environment cubemap (texture-set slot 4), or null. This is a
    ///     distinct additive shader pass whose mask comes from slot 5 red, falling back to the normal
    ///     map alpha; it must not inherit FO4's <c>_s</c> mask/smoothness semantics.
    /// </summary>
    public string? ClassicEnvironmentMapTexturePath { get; set; }

    /// <summary>Optional FO3/FNV custom environment mask (texture-set slot 5, red channel).</summary>
    public string? ClassicEnvironmentMaskTexturePath { get; set; }

    /// <summary>Authored classic <c>BSShaderProperty.EnvMapScale</c>; zero disables the pass.</summary>
    public float ClassicEnvironmentMapScale { get; set; }

    /// <summary>
    ///     True for BSShaderFlags bit 21 (Window_Environment_Mapping). It selects SLS2058
    ///     <c>ENVMAP_W</c>, whose incident-vector sign is opposite the ordinary SLS2057 pass.
    /// </summary>
    public bool ClassicEnvironmentMapUsesWindowReflection { get; set; }

    /// <summary>
    ///     FO3/FNV simple PP-lighting height map (texture-set slot 3), or null. This is populated
    ///     only when bit 11 is set, bit 28 (POM) is clear, and the mesh has usable UV/TBN data.
    /// </summary>
    public string? ClassicParallaxHeightMapTexturePath { get; set; }

    /// <summary>
    ///     True when the material marks this shape a decal — coplanar overlay geometry (grime,
    ///     cracks, posters) the engine draws with a depth bias over the surface underneath. Sources:
    ///     the BGSM/BGEM decal byte (FO4/FO76 material files) and shader-flags bits 26/27
    ///     (Decal / Dynamic_Decal — same bit positions in FO3/FNV BSShaderFlags and Skyrim+ SLSF1).
    ///     Without the bias these shapes z-fight their backing surface.
    /// </summary>
    public bool IsDecal { get; set; }

    /// <summary>
    ///     Effect-shader color tint: inline Skyrim BSEffect / BGEM base color × base color scale,
    ///     multiplied into the source texture RGB. (1,1,1) for non-effect materials.
    /// </summary>
    public (float R, float G, float B) EffectTint { get; set; } = (1f, 1f, 1f);

    /// <summary>
    ///     Effect view-angle falloff (BGEM): opacity ramps StartOpacity→StopOpacity as |N·V| crosses
    ///     StartAngle→StopAngle (both stored as cosines). Null when the material has no falloff —
    ///     the term that fades effect planes at grazing angles; without it crossed-plane mist blobs
    ///     render every plane at full opacity (blinding additive blooms).
    /// </summary>
    public (float StartAngle, float StopAngle, float StartOpacity, float StopOpacity)? EffectFalloff { get; set; }

    /// <summary>
    ///     Authored scene-depth intersection falloff in game units. Currently sourced from the
    ///     inline Skyrim-family BSEffectShaderProperty when SLSF1_Soft_Effect is set; zero means the
    ///     source format supplied no usable soft depth and the narrowly-scoped runtime policy may
    ///     choose an engine-family fallback for particle/effects geometry.
    /// </summary>
    public float SoftParticleFalloffDepth { get; set; }

    /// <summary>Shader property metadata resolved from the source NIF.</summary>
    public NifShaderTextureMetadata? ShaderMetadata { get; init; }

    /// <summary>True if this submesh uses BSShaderNoLightingProperty (self-illuminated, e.g., neon signs).</summary>
    public bool IsEmissive { get; set; }

    /// <summary>True if BSShaderFlags2 bit 5 (Vertex_Colors) is set, meaning vertex colors should modulate the texture.</summary>
    public bool UseVertexColors { get; init; }

    /// <summary>
    ///     True when the vertex-color alpha channel is authored opacity. False keeps the raw channel
    ///     available as non-opacity shader data while render/export consumers substitute opaque alpha.
    ///     FO3/FNV <c>TallGrassShaderProperty</c> uses this channel as wind amplitude, not coverage.
    /// </summary>
    public bool UseVertexAlphaForOpacity { get; set; } = true;

    /// <summary>True if NiStencilProperty DrawMode is DRAW_BOTH (3), meaning both sides should be rendered.</summary>
    public bool IsDoubleSided { get; set; }

    /// <summary>True if NiAlphaProperty flags bit 0 is set (alpha blending enabled).</summary>
    public bool HasAlphaBlend { get; set; }

    /// <summary>True if NiAlphaProperty flags bit 9 is set (alpha testing enabled).</summary>
    public bool HasAlphaTest { get; set; }

    /// <summary>Alpha test threshold (0-255). Pixels with alpha &lt;= this value are discarded. Default 128 per NIF spec.</summary>
    public byte AlphaTestThreshold { get; set; } = 128;

    /// <summary>
    ///     Alpha test comparison function from NiAlphaProperty bits 10-12.
    ///     0=ALWAYS, 1=LESS, 2=EQUAL, 3=LEQUAL, 4=GREATER, 5=NOTEQUAL, 6=GEQUAL, 7=NEVER.
    ///     Default 4 (GREATER) matches the standard renderer semantics (pass if a &gt; threshold).
    /// </summary>
    public byte AlphaTestFunction { get; set; } = 4;

    /// <summary>Source blend factor from NiAlphaProperty bits 1-4 (default 6 = SRC_ALPHA).</summary>
    public byte SrcBlendMode { get; set; } = 6;

    /// <summary>Dest blend factor from NiAlphaProperty bits 5-8 (default 7 = INV_SRC_ALPHA).</summary>
    public byte DstBlendMode { get; set; } = 7;

    /// <summary>
    ///     Material alpha from NiMaterialProperty/BGSM/BGEM. Authored values above 1 are retained for
    ///     the effect pipeline; values below 1 trigger alpha blending.
    /// </summary>
    public float MaterialAlpha { get; set; } = 1f;

    /// <summary>
    ///     Optional manager-driven NiAlphaController bound to this submesh's NiMaterialProperty.
    ///     The GPU renderer samples it from the shared animation clock and multiplies the result by
    ///     <see cref="MaterialAlpha" />. Null keeps the ordinary static-material path byte-identical.
    /// </summary>
    public Animation.NifMaterialAlphaController? MaterialAlphaController { get; set; }

    /// <summary>Material glossiness from NiMaterialProperty. Fallout 3 / New Vegas commonly default this to 10.</summary>
    public float MaterialGlossiness { get; init; } = 10f;

    /// <summary>Material specular color from NiMaterialProperty (RGB, 0-1). Controls specular highlight tint and intensity.</summary>
    public (float R, float G, float B) SpecularColor { get; init; } = (0, 0, 0);

    /// <summary>
    ///     Legacy NiMaterialProperty diffuse color (BsVersion &lt; 26 — TES3/TES4-era streams only),
    ///     carried so UNTEXTURED shapes can bind it as their base color instead of the opaque-white
    ///     fallback (e.g. SewerExitGateExterior01's authored-black 'Plane02'). Null when the shape
    ///     has no NiMaterialProperty or the stream is FO3+ (whose materials have no diffuse lane).
    /// </summary>
    public (float R, float G, float B)? MaterialDiffuse { get; set; }

    /// <summary>True if BSShaderFlags bit 17 (Eye_Environment_Mapping = 0x20000) is set.</summary>
    public bool IsEyeEnvmap { get; init; }

    /// <summary>BSShaderProperty EnvMapScale — controls eye cubemap reflection strength. Typical 0.5-1.0.</summary>
    public float EnvMapScale { get; init; }

    /// <summary>
    ///     Render order for layer-based compositing (engine renders head parts in scene graph order).
    ///     0 = head (default), 1 = hair, 2 = eyes. Higher layers render after lower layers.
    /// </summary>
    public int RenderOrder { get; set; }

    /// <summary>
    ///     Multiplicative tint color (R, G, B in 0-1 range). Applied to texture color during rasterization.
    ///     Used for hair color tinting (engine applies HCLR as shader uniform on hair/eyebrow/beard submeshes).
    ///     Null = no tint (1.0 multiplier).
    /// </summary>
    public (float R, float G, float B)? TintColor { get; set; }

    /// <summary>
    ///     True if this submesh uses the FaceGen skin shader (shader type 14, flag bit 10).
    ///     When set, the renderer applies a subsurface scattering approximation using
    ///     <see cref="SubsurfaceColor" /> to simulate light transmission through skin.
    /// </summary>
    public bool IsFaceGen { get; set; }

    /// <summary>
    ///     Subsurface scattering color for FaceGen skin shader (normalized 0-1 RGB).
    ///     Derived from the BSShaderTextureSet slot 2 face tint texture (_sk).
    ///     The engine multiplies this by a scatter intensity to add warm red backlighting
    ///     that counteracts green casts from EGT texture morphs.
    ///     Only used when <see cref="IsFaceGen" /> is true. Default = (0, 0, 0) = no scatter.
    /// </summary>
    public (float R, float G, float B) SubsurfaceColor { get; set; }

    /// <summary>
    ///     Animated emissive color from NiMaterialColorController (TC_SELF_ILLUM or TC_DIFFUSE).
    ///     When set, the submesh is treated as self-illuminated with this color applied additively.
    ///     Extracted from the first keyframe of the controller's NiPosData.
    /// </summary>
    public (float R, float G, float B)? AnimatedEmissiveColor { get; set; }

    /// <summary>
    ///     Static material emissive glow color (NiMaterialProperty Emissive Color × Emissive Mult),
    ///     read for self-illuminated (SHADER_NOLIGHTING / effect) shapes. This IS the engine's glow
    ///     color — the diffuse of such shapes is typically a white gradient sheet the emissive tints
    ///     (NV_BarrelPile03's goo: white shadefade01.dds × green (0.05, 1, 0) × 2 — without this the
    ///     glow renders clipped white). Null when the shape has no NiMaterialProperty (render as-is).
    /// </summary>
    public (float R, float G, float B)? EmissiveColor { get; set; }

    /// <summary>
    ///     Raw selected emittance for a standard FO3/FNV Lighting30 material (NiMaterialProperty,
    ///     or resolved XEMI when External_Emittance is set). Unlike <see cref="EmissiveColor" />,
    ///     this remains scene-lit and is folded into the shader's ambient term (or multiplied by
    ///     <see cref="Lighting30GlowMapTexturePath" />). Null means no source was available.
    /// </summary>
    public (float R, float G, float B)? Lighting30EmissionColor { get; set; }

    /// <summary>
    ///     Explicit classic Lighting30 route. Kept even when the authored emission is black and no
    ///     glow map is present so downstream code never infers shader identity from color values.
    /// </summary>
    public bool IsLighting30 { get; set; }

    /// <summary>
    ///     NiMaterialProperty Emissive Mult kept separate from the raw Lighting30 color: the engine
    ///     applies it only in HDR mode. XEMI replaces only RGB and retains this material multiplier.
    /// </summary>
    public float Lighting30EmissionMultiplier { get; set; } = 1f;

    /// <summary>
    ///     Standard Lighting30 GlowMap (classic PP texture-set slot 2). Hair and FaceGen also use
    ///     slot 2, so this is populated only by <see cref="NifLighting30EmissionPolicy" />.
    /// </summary>
    public string? Lighting30GlowMapTexturePath { get; set; }

    /// <summary>
    ///     Constant UV scroll velocity (UV units/second) resolved from a TES3-era NiUVController
    ///     looping ramp (waterfalls, lava). Zero = no scroll. The renderer applies
    ///     <c>uv += frac(velocity × animClock)</c> as a per-draw constant — the authored UVs stay
    ///     untouched, so a renderer without an animation clock draws the static base frame.
    /// </summary>
    public System.Numerics.Vector2 UvScrollVelocity { get; set; }

    /// <summary>
    ///     Pre-skinning vertex positions in world space (same layout as <see cref="Positions" />).
    ///     Populated only for skinned submeshes; used by boundary vertex stitching to identify
    ///     coincident vertices across different source NIFs, then cleared after stitching.
    /// </summary>
    public float[]? BindPosePositions { get; set; }

    /// <summary>
    ///     Path of the source NIF file this submesh was extracted from.
    ///     Used by boundary vertex stitching to distinguish cross-NIF seams from within-NIF geometry.
    /// </summary>
    public string? SourceNifPath { get; set; }

    /// <summary>
    ///     Block index of the source NiTriShape/NiTriStrips in the NIF file.
    ///     Used to partition submeshes by attachment group without re-extracting.
    /// </summary>
    public int SourceBlockIndex { get; set; } = -1;

    /// <summary>
    ///     Sky-dome classification when this submesh came from a <c>SkyShaderProperty</c> (FO3/FNV) or
    ///     <c>BSSkyShaderProperty</c> (Skyrim+) — i.e. it's a layer of a climate sky NIF (atmosphere /
    ///     stars / clouds). Null for ordinary world geometry. The sky renderer keys blend + texture-source
    ///     off this (gradient for SKY, additive for STARS, alpha + weather-override for CLOUDS), so the
    ///     decision is driven by the real NIF data, not a per-game heuristic. <see cref="DiffuseTexturePath" />
    ///     holds the layer's baked texture (empty for the gradient atmosphere layer).
    /// </summary>
    public SkyObjectType? SkyType { get; set; }

    /// <summary>
    ///     True if this submesh's geometry sat under a <c>NiBillboardNode</c> in the source NIF
    ///     (e.g. the smoke glow under "billboardUp" in effects\NVashpile01.NIF). The billboard node's
    ///     own rotation is dropped during the bake (only its translation is kept) so the renderer can
    ///     re-aim the quad at the camera per frame instead of using the baked-in orientation. Only set
    ///     when the caller opts in via <c>NifGeometryExtractor.Extract(collectBillboards: true)</c>.
    /// </summary>
    public bool IsBillboard { get; set; }

    /// <summary>
    ///     Camera-facing rule authored by the containing <c>NiBillboardNode</c>. Skyrim fire commonly
    ///     uses <see cref="NifBillboardMode.AlwaysFaceCenter" />, which must pitch as well as yaw;
    ///     treating every node as rotate-about-up collapses its flame cards into a flat glow.
    /// </summary>
    public NifBillboardMode BillboardMode { get; set; } = NifBillboardMode.RotateAboutUp;

    /// <summary>
    ///     True for SpeedTree leaf cards: each quad is a camera-facing billboard (SpeedTree RT builds
    ///     leaf cards CPU-side as flat 2D corner offsets around a center — <c>CLeafGeometry::Update</c> —
    ///     and re-faces them to the camera per frame). The GPU does the equivalent in the vertex shader:
    ///     the per-vertex <see cref="Tangents" /> carry the card CENTER (mesh-local) and
    ///     <see cref="Bitangents" /> the signed 2D card-space corner offset <c>(±halfW, ±halfH, 0)</c>, so
    ///     the VS reconstructs <c>worldCenter + camRight·off.x + camUp·off.y</c>. Distinct from
    ///     <see cref="IsBillboard" />, which re-aims a WHOLE submesh as one rigid quad.
    /// </summary>
    public bool IsLeafBillboard { get; set; }

    /// <summary>
    ///     True for a baked particle cloud whose vertices are four-vertex camera-facing quads.
    ///     Unlike SpeedTree leaf cards, particle quads must also be depth-sorted independently
    ///     inside the submesh each frame. The per-quad center is carried in the first vertex's
    ///     tangent (the same center used by the billboard vertex-shader route).
    /// </summary>
    public bool IsParticleCloud { get; set; }

    /// <summary>
    ///     Non-persisted legacy particle source for the opt-in live D3D12 path. Null for ordinary geometry
    ///     and for renderers that consume only the static baked arrays above.
    /// </summary>
    public Particles.ParticleRuntimeDefinition? ParticleRuntime { get; set; }

    /// <summary>
    ///     True for SpeedTree bark/frond geometry carrying the specialized branch-wind payload.
    ///     Tangent direction/length encodes TBN + matrix slot, bitangent direction/length encodes
    ///     TBN + wind weight; the general vertex format remains unchanged.
    /// </summary>
    public bool IsSpeedTreeBranch { get; set; }

    /// <summary>TREE CNAM (RockSpeed, RustleSpeed), applied per SpeedTree batch.</summary>
    public System.Numerics.Vector2 SpeedTreeWindSpeeds { get; set; } = System.Numerics.Vector2.One;

    /// <summary>
    ///     Non-persisted identity for one component of the temporary opt-in SpeedTree runtime-LOD
    ///     sequence. Null on the established single-LOD path and on ordinary NIF geometry.
    /// </summary>
    public BethesdaMultitool.Core.Formats.SpeedTree.SpeedTreeLodMetadata? SpeedTreeLod { get; set; }

    /// <summary>
    ///     True when this submesh came from a <c>BSMeshLODTriShape</c> whose LOD0 slice is EMPTY, so
    ///     the far-LOD fallback slice (LOD1/LOD2) was extracted instead. The engine only draws such
    ///     slices at distance; near the camera the shape contributes nothing. When the model carries
    ///     any near-content geometry the extractor drops these (they're distant imposters that would
    ///     z-fight the real meshes — e.g. workshop rubble's LOD2-only floor slab vs its coplanar
    ///     _Foundation ref); when the WHOLE model is far-slices (VineHanging05 authors everything in
    ///     LOD2) they're kept so the reference renders at all.
    /// </summary>
    public bool IsFarLodFallback { get; init; }

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Triangles.Length / 3;
}
