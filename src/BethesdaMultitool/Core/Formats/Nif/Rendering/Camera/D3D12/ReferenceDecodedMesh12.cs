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
    bool DepthWritingBlend = false);
#endif
