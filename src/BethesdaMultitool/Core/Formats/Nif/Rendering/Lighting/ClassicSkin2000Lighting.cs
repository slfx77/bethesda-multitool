namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     Scalar form of Oblivion's retail <c>SKIN2000.pso</c> light equation. Texture composition is
///     handled by the FaceGen asset path; this helper owns only the source-proven direct and rim
///     terms shared by deterministic CPU tests and the software renderer.
/// </summary>
internal static class ClassicSkin2000Lighting
{
    internal const float RimScale = 0.5f;

    /// <summary>
    ///     Computes <c>Ambient + Light * max(N.L, 0) + 0.5 * Light *
    ///     (1 - max(N.V, 0))^3</c>. Inputs are dot products because each renderer already owns the
    ///     coordinate-space transforms and normalization that produce them.
    /// </summary>
    internal static float Compute(
        float normalDotLight,
        float normalDotView,
        float lightIntensity,
        float ambient)
    {
        var direct = MathF.Max(normalDotLight, 0f);
        var view = MathF.Max(normalDotView, 0f);
        var oneMinusView = 1f - view;
        var rim = RimScale * lightIntensity *
                  oneMinusView * oneMinusView * oneMinusView;
        return MathF.Max(
            ambient + lightIntensity * direct + rim,
            0f);
    }
}
