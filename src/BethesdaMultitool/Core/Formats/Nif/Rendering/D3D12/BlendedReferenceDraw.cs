#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     One deferred blended (transparent) reference submesh draw, frozen with everything the
///     back-to-front pass needs to re-sort and re-issue it.
/// </summary>
/// <param name="SourceWorld">
///     The ABSOLUTE placement world matrix, kept so batch-reuse frames can refresh what the
///     camera moves without re-resolving the reference: the back-to-front sort distance and,
///     for billboards, the camera-facing <paramref name="World" /> matrix.
/// </param>
internal readonly record struct BlendedReferenceDraw(
    Matrix4x4 World,
    CachedSubmesh12 Submesh,
    float DistanceSquared,
    Vector4 AlphaState,
    Vector4 RenderState,
    Vector4 Specular,
    Matrix4x4 SourceWorld,
    Vector4 ReferenceBounds,
    uint PhysicsLiteSeed,
    bool IsGrass,
    float GrassWaveMultiplier,
    float WorldBoundsCenterZ,
    float WorldBoundsRadius,
    float WorldBoundsMaxZ,
    float WorldBoundsMinZ,
    uint MeshId,
    Vector2 WorldBoundsCenterXY,
    uint ExternalEmittanceFormId);
#endif
