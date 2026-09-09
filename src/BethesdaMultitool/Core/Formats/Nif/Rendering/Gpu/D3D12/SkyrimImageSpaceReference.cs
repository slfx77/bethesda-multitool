using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Pure reference implementation of the recovered Oldrim HDR image-space math. The constants and
///     instruction order come from <c>ImageSpaceEffectHDR::UpdateParams</c> in the map-matched 1.1.21
///     executable and <c>BSImagespaceShaderHDRTonemapBlendCinematic</c> in the shipped 1.9.32
///     <c>imagespace002.fxp</c>. Keeping this independent of D3D12 makes the retail equations directly
///     testable.
/// </summary>
internal static class SkyrimImageSpaceReference
{
    internal const float MinimumAdaptationStep = 1f / 256f;

    /// <summary>
    ///     Resolves the two factors written to the retail adapt shader's Param.zw. Inputs are deliberately
    ///     not clamped here: valid IMGS data is passed through the recovered equations literally.
    /// </summary>
    internal static AdaptationFactors ResolveAdaptationFactors(
        float eyeAdaptSpeed,
        float eyeAdaptStrength,
        float deltaSeconds)
    {
        var exponent = 30f * deltaSeconds;
        var fast = 1f - MathF.Pow(1f - eyeAdaptSpeed / 100f, exponent);
        var slow = 1f - MathF.Pow(
            1f - eyeAdaptSpeed / (100f * eyeAdaptStrength), exponent);
        return new AdaptationFactors(fast, slow);
    }

    /// <summary>
    ///     One lane of the pair-19/pair-21 temporal update. Retail guarantees at least 1/256 movement
    ///     toward the new luminance per rendered update, without overshooting the remaining delta.
    /// </summary>
    internal static float StepAdaptedLuminance(float previous, float current, float factor)
    {
        var delta = current - previous;
        var magnitude = MathF.Min(
            MathF.Abs(delta),
            MathF.Max(MathF.Abs(delta * factor), MinimumAdaptationStep));
        return previous + MathF.Sign(delta) * magnitude;
    }

    /// <summary>
    ///     Literal RGB projection of shipped Oldrim shader pair 13. Bloom is the explicit output of
    ///     Oldrim's recovered two-pass blur route; Special Edition remains unverified.
    /// </summary>
    internal static Vector3 ApplyTonemapBlendCinematic(
        Vector3 scene,
        Vector3 bloom,
        float adaptedSlow,
        float adaptedFast,
        in GpuTonemapSettings settings)
    {
        var luminance = Vector3.Dot(scene, new Vector3(0.2125f, 0.7154f, 0.0721f));
        var q = luminance * (adaptedFast / adaptedSlow);
        var mapped = q * (1f + q / (settings.White * settings.White)) / (1f + q);
        if (luminance < 0f)
        {
            mapped = 0f;
        }

        var hdr = scene * (mapped / luminance)
                  + bloom * Math.Clamp(settings.ReceiveBloomThreshold - mapped, 0f, 1f);
        var gray = Vector3.Dot(hdr, new Vector3(0.2125f, 0.7154f, 0.0721f));
        var graded = Vector3.Lerp(new Vector3(gray), hdr, settings.Saturation);
        graded = Vector3.Lerp(
            graded,
            gray * new Vector3(settings.TintR, settings.TintG, settings.TintB),
            settings.TintAmount);
        return settings.Contrast * (settings.Brightness * graded - new Vector3(adaptedSlow))
               + new Vector3(adaptedSlow);
    }

    internal readonly record struct AdaptationFactors(float Fast, float Slow);
}
