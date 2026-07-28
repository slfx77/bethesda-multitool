using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     CPU reference-facing form of the recovered FO3/FNV SKY vertex-shader blend contract. Vertex RGB
///     selects three authored colors; it is not a literal RGB tint.
/// </summary>
internal static class SkyBlendWeights
{
    public static Vector3 Evaluate(
        Vector3 vertexWeights,
        Vector3 blendColor0,
        Vector3 blendColor1,
        Vector3 blendColor2) =>
        (blendColor0 * vertexWeights.X) +
        (blendColor1 * vertexWeights.Y) +
        (blendColor2 * vertexWeights.Z);

    /// <summary>
    ///     Opaque equivalent of retail alpha blending the authored atmosphere over its horizon color.
    /// </summary>
    public static Vector3 CompositeAtmosphere(Vector3 weightedColor, Vector3 horizonColor, float vertexAlpha) =>
        Vector3.Lerp(horizonColor, weightedColor, Math.Clamp(vertexAlpha, 0f, 1f));
}
