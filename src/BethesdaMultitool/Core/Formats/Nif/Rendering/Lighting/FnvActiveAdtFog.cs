using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     Finite-input algebraic oracle for PC package013 SLS2000's vertex fog and pixel composite.
///     The owned installed PC runtime proves FogParam=(end,end-start,power,0); this does not assume
///     bit-identical legacy GPU transcendental precision. Fog is evaluated before interpolation,
///     using forward-projection clip XYZ before division by W, not world/eye distance.
/// </summary>
internal static class FnvActiveAdtFog
{
    /// <summary>
    ///     Conservative source admission before AtmosphereState's shared range/power repairs.
    ///     Only the complete six-float FNV FNAM is supported. Powers below the existing 0.01
    ///     resolver floor retain fallback rather than claiming that floor is the authored value.
    /// </summary>
    internal static bool HasUnmodifiedWeatherSource(BethesdaGame game, IReadOnlyList<float>? distances)
    {
        return game == BethesdaGame.FalloutNewVegas && distances is { Count: 6 } &&
               distances[4] >= 0.01f && distances[5] >= 0.01f &&
               TryPack(distances[0], distances[1], distances[4], out _) &&
               TryPack(distances[2], distances[3], distances[5], out _);
    }

    /// <summary>Preserves the actual PC writer packing within a finite normal-FP32 reciprocal cohort.</summary>
    internal static bool TryPack(float start, float end, float power, out Vector3 parameters)
    {
        // D3D shader arithmetic can flush denormals. The subtraction operands and positive
        // power, range and reciprocal must survive; finite CPU arithmetic alone is insufficient.
        // The PC writer only special-cases both endpoints zero; signed normal endpoints are valid.
        // NVWastelandClear authors start=-10 in both day/night FNAM pairs. Keep that value intact.
        const float minimumNormal = 1.17549435E-38f;
        parameters = default;
        var range = end - start;
        var inverseRange = 1f / range;
        if (!float.IsFinite(start) || !float.IsFinite(end) || !float.IsFinite(power) ||
            (!start.Equals(0f) && MathF.Abs(start) < minimumNormal) ||
            (!end.Equals(0f) && MathF.Abs(end) < minimumNormal) || end <= start || power < minimumNormal ||
            !float.IsFinite(range) || range < minimumNormal ||
            !float.IsFinite(inverseRange) || inverseRange < minimumNormal)
        {
            return false;
        }

        parameters = new Vector3(end, range, power);
        return true;
    }

    /// <summary>
    ///     Rechecks the exact uploaded values. The recovered PC lane has one RGB fog color and
    ///     no maximum-opacity multiplier; dual-color/capped or unproved inputs retain fallback.
    /// </summary>
    internal static bool IsSupported(
        bool hasUnmodifiedSource, float start, float end, float power,
        Vector3 color, Vector3 farColor, float maximumOpacity)
    {
        return hasUnmodifiedSource && TryPack(start, end, power, out _) &&
               float.IsFinite(color.X) && float.IsFinite(color.Y) && float.IsFinite(color.Z) &&
               color.Equals(farColor) && maximumOpacity.Equals(1f);
    }

    /// <summary>Undoes CameraState's Z-only reverse-depth remap without perspective division.</summary>
    internal static Vector4 RecoverForwardClipPosition(Vector4 reversedClipPosition)
    {
        return new Vector4(reversedClipPosition.X, reversedClipPosition.Y,
            reversedClipPosition.W - reversedClipPosition.Z, reversedClipPosition.W);
    }

    /// <summary>
    ///     Evaluates the SLS2000.vso c14 operation for an already-forward-projected position.
    ///     The intended finite domain has a positive range (Y) and power (Z); no viewer-specific
    ///     near/far repair, power clamping or maximum-opacity multiplier is added here.
    /// </summary>
    internal static float EvaluateAmount(Vector4 forwardClipPosition, Vector3 fogParam)
    {
        var projected = new Vector3(forwardClipPosition.X, forwardClipPosition.Y, forwardClipPosition.Z);
        var distance = projected.Length();
        var inverseRange = 1f / fogParam.Y;
        var linear = 1f - Math.Clamp((fogParam.X - distance) * inverseRange, 0f, 1f);
        return MathF.Pow(linear, fogParam.Z);
    }

    /// <summary>
    ///     SLS2000.pso applies interpolated COLOR1 after base lighting when Toggles.y is positive.
    ///     Nonpositive toggles select the unfogged result. Alpha is a separate unchanged output.
    /// </summary>
    internal static Vector3 Composite(
        Vector3 litRgb, Vector3 interpolatedFogRgb, float interpolatedFogAmount, float fogToggle)
    {
        return fogToggle > 0f ? Vector3.Lerp(litRgb, interpolatedFogRgb, interpolatedFogAmount) : litRgb;
    }
}
