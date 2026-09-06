using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Authored Vector3 Hermite segment: earlier Backward/out and later Forward/in, in normalized
///     key-segment coordinates. Tangents are serialized values, not derivatives per second.
/// </summary>
internal static class NifQuadraticVectorCurve
{
    internal static Vector3 Sample(
        Vector3 earlier, Vector3 later, Vector3 earlierBackward, Vector3 laterForward, float fraction)
    {
        var delta = later - earlier;
        var quadratic = 3f * delta - laterForward - 2f * earlierBackward;
        var cubic = earlierBackward + laterForward - 2f * delta;
        return earlier + fraction * (earlierBackward + fraction * (quadratic + fraction * cubic));
    }

    internal static bool IsFiniteAuthored(Vector3 value) =>
        float.IsFinite(value.X) && MathF.Abs(value.X) < 1e30f &&
        float.IsFinite(value.Y) && MathF.Abs(value.Y) < 1e30f &&
        float.IsFinite(value.Z) && MathF.Abs(value.Z) < 1e30f;
}
