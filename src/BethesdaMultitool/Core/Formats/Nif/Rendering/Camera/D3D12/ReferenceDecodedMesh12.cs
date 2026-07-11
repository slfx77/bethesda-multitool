#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

// CollisionPositions/CollisionTriangles carry the NIF's decoded Havok (bhk*) collision soup when
// present (root-local treatRootsAsIdentity frame, same as the visual submeshes). BuildCollisionMesh
// prefers them over the visual-mesh soup so walk mode rides the gapless physics mesh. Null when the
// NIF has no decodable Havok collision (→ visual-mesh fallback).
internal sealed record DecodedNifMesh12(
    IReadOnlyList<DecodedSubmesh12> Submeshes,
    Vector3[]? CollisionPositions = null,
    int[]? CollisionTriangles = null);

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
    Vector2 UvScrollVelocity = default);
#endif
