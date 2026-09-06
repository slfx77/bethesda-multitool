using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     Finite-input algebraic oracle for PC package013 SLS2000's vertex fog and pixel composite.
///     FogParam is supplied in shader-register form; this does not assume its CPU upload packing
///     or bit-identical legacy GPU transcendental precision. Fog is evaluated before interpolation,
///     using forward-projection clip XYZ before division by W, not world/eye distance.
/// </summary>
internal static class FnvActiveAdtFog
{
    /// <summary>Undoes CameraState's Z-only reverse-depth remap without perspective division.</summary>
    internal static Vector4 RecoverForwardClipPosition(Vector4 reversedClipPosition) =>
        new(reversedClipPosition.X, reversedClipPosition.Y,
            reversedClipPosition.W - reversedClipPosition.Z, reversedClipPosition.W);

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
        Vector3 litRgb, Vector3 interpolatedFogRgb, float interpolatedFogAmount, float fogToggle) =>
        fogToggle > 0f ? Vector3.Lerp(litRgb, interpolatedFogRgb, interpolatedFogAmount) : litRgb;
}
