#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.SpeedTree;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

// CollisionPositions/CollisionTriangles carry the NIF's decoded Havok (bhk*) collision soup when
// present (root-local treatRootsAsIdentity frame, same as the visual submeshes). BuildCollisionMesh
// prefers them over the visual-mesh soup so walk mode rides the gapless physics mesh. Null when the
// NIF has no decodable Havok collision (→ visual-mesh fallback).
// Animation (v32+) carries the keyframe rig for animated statics (banners); the baked Vertices stay
// REST-POSE, so a consumer that ignores it still draws the correct static mesh.
internal sealed record DecodedNifMesh12(
    IReadOnlyList<DecodedSubmesh12> Submeshes,
    Vector3[]? CollisionPositions = null,
    int[]? CollisionTriangles = null,
    NifMeshAnimation? Animation = null,
    // Persistent source provenance, deliberately distinct from IsParticleCloud: a controller-delayed
    // system can produce no baked cloud while its containing NIF still needs a source decode in live mode.
    bool ContainsParticleSource = false);

internal sealed record DecodedSubmesh12(
    GpuMeshUploader.GpuVertex[] Vertices,
    ushort[] Indices,
    string? DiffuseTexturePath,
    string? NormalMapTexturePath,
    bool HasBump,
    NifAlphaRenderMode AlphaRenderMode,
    bool AlphaBlend,
    bool AlphaTest,
    float AlphaTestThreshold,
    byte AlphaTestFunction,
    byte SrcBlendMode,
    byte DstBlendMode,
    float MaterialAlpha,
    bool DoubleSided,
    bool IsEmissive,
    Vector3 LocalBoundsCenter,
    float LocalBoundsRadius,
    bool IsBillboard,
    // NiMaterialProperty specular: highlight tint + Phong exponent, gated to where the shader enables
    // it (1A). Carried through the decode + persistent cache so it can drive a GPU specular term.
    Vector3 SpecularColor = default,
    float Glossiness = 0f,
    bool SpecularEnabled = false,
    // SpeedTree leaf cards: GPU re-faces each quad to the camera (tangent = card center, bitangent =
    // signed 2D offset). Persisted in ReferenceDecodedMeshDiskCache12 v7+.
    bool IsLeafBillboard = false,
    // Effects-folder foliage (e.g. NVSeaPlant02): an alpha-blend shape the engine writes depth for. The
    // reference renderer draws it inline before the water pass with a depth-writing blend PSO so water
    // occludes it from above. Persisted in ReferenceDecodedMeshDiskCache12 v10+.
    bool DepthWritingBlend = false,
    // FO4/FO76 material specular map (_s.dds: R = per-texel specular mask). Without it the shader has
    // no mask for BC5 normal maps (no alpha channel) and specular is suppressed rather than uniform —
    // a mask of 1.0 everywhere blows out whole scenes. Persisted in v21+.
    string? SpecularMapTexturePath = null,
    // FO4/FO76 grayscale-to-palette texture + row: the shader replaces diffuse RGB with
    // palette(u: diffuse.G, v: GradientMapV × vertexColor.R). Persisted in v22+.
    string? GradientMapTexturePath = null,
    float GradientMapV = 0f,
    // Decal (BGSM decal byte / shader-flags bits 26-27): coplanar overlay geometry drawn with a
    // depth-biased PSO so it wins the depth tie against its backing surface. Persisted in v24+.
    bool IsDecal = false,
    // BGEM effect terms (fo76utils getDiffuseColor_Effect): rgb tint = baseColor × scale;
    // FalloffParams = (startAngle, stopAngle, startOpacity, stopOpacity) — an |N·V| opacity ramp,
    // enabled when HasFalloff. Persisted in v28+ (without them, mist blobs render blinding white).
    Vector3 EffectTint = default,
    Vector4 EffectFalloffParams = default,
    bool HasEffectFalloff = false,
    // FO4 cubemap environment mapping (BGSM slot 4): the shader adds cube(reflect(V,N)) ×
    // EnvironmentMapScale × _s.R × g(N·V), mip-selected by smoothness × _s.G. Persisted in v29+
    // (without them, FO4 metal/gloss reads matte).
    string? EnvironmentMapTexturePath = null,
    float EnvironmentMapScale = 0f,
    float EnvironmentMapSmoothness = 0f,
    // TES3 NiUVController constant scroll (waterfalls, lava): UV units/second the renderer applies
    // as a per-draw offset off the animation clock. Zero = static. Persisted in v32+.
    Vector2 UvScrollVelocity = default,
    // CPU skinning inputs for keyframe playback (v32+): raw skin-space base geometry + influences
    // + inverse binds, null for unskinned/unanimated submeshes. The baked Vertices are rest-pose.
    NifSubmeshSkin? Skin = null,
    // SpeedTree bark/frond route + per-tree TREE.CNAM time multipliers.
    bool IsSpeedTreeBranch = false,
    Vector2 SpeedTreeWindSpeeds = default,
    // BGSM/BGEM common TileU/TileV flags, normalized to sampler clamp booleans.
    bool ClampTextureU = false,
    bool ClampTextureV = false,
    // Baked particle-cloud marker. The cache derives one center per four-vertex quad and the
    // blended renderer emits a camera-sorted transient index buffer each frame.
    bool IsParticleCloud = false,
    // Authored BSEffect soft-intersection depth in game units. Legacy FNV effects generally leave
    // this zero and are admitted only by NifSoftParticlePolicy's scoped particle/effects fallback.
    float SoftParticleFalloffDepth = 0f,
    // NiControllerSequence → NiAlphaController material-opacity curve. Persisted so warm-cache
    // effects retain their authored fade instead of reverting to full opacity.
    NifMaterialAlphaController? MaterialAlphaController = null,
    // Strict FNV BS34 Havok-lite route. Pivot/axis are already converted from constraint body-local
    // into the extractor's baked root-local frame. Phase remains per placed reference at draw time.
    PhysicsLiteSwayDescriptor? PhysicsLiteSway = null,
    // Non-persisted source graph for the opt-in live path. Persistent payloads intentionally leave
    // this null; live mode bypasses disk-cache reads so a source decode always supplies it.
    ParticleRuntimeDefinition? ParticleRuntime = null,
    // Non-persisted opt-in .spt runtime-LOD identity. .spt decodes bypass the persistent cache.
    SpeedTreeLodMetadata? SpeedTreeLod = null,
    // Classic FO3/FNV Lighting30 material emission (v52+). Kept distinct from IsEmissive:
    // Lighting30 stays scene-lit and optionally masks its emission with the classified glow map.
    bool IsLighting30 = false,
    string? Lighting30GlowMapTexturePath = null,
    Vector3 Lighting30EmissionColor = default,
    float Lighting30EmissionMultiplier = 1f,
    // Exact FO3/FNV property identity. The FNV placement policy separately gates wind/hard-end;
    // this marker exists so the VS can consume raw vertex alpha and restore coverage alpha.
    bool IsTallGrass = false,
    // Classic FO3/FNV PP-lighting environment pass (v54+). Slot 5 is a red-channel custom mask;
    // when absent, the shader uses the normal map's alpha. The final bool preserves bit-21/SLS2058
    // window direction. Kept separate from FO4 BGSM _s data.
    string? ClassicEnvironmentMapTexturePath = null,
    string? ClassicEnvironmentMaskTexturePath = null,
    float ClassicEnvironmentMapScale = 0f,
    bool ClassicEnvironmentMapUsesWindowReflection = false,
    // Classic simple parallax (v55+). Policy has already excluded bit-28 POM and unusable UV/TBN.
    string? ClassicParallaxHeightMapTexturePath = null,
    // Audit-only PC-final SLS1009/SLS1013 identity (v57+), with all-vertex validity in v58,
    // static/effective material scope in v59, transformed-basis preservation in v60, and raw
    // type-1/non-skinned/non-single-pass scope in v62. Active retail ADT reuses it only as a strict
    // ordinary-material/vertex-color discriminator behind its own FNV draw gate.
    FnvClassicBasicShaderMode ClassicBasicShaderMode = FnvClassicBasicShaderMode.None,
    // Stable source-shape provenance (v63+). This is CPU-only identity for diagnostics and future
    // property-associated light observations; -1 means the source block is unavailable.
    int SourceBlockIndex = -1,
    // Authored NiBillboardNode mode (v64+). RotateAboutUp preserves pre-v64 behavior.
    NifBillboardMode BillboardMode = NifBillboardMode.RotateAboutUp);
#endif
