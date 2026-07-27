using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Pure CPU mirror for the evidence-backed subset of the default-off modern display pass. FO4's
///     named Middle Gray and Auto Exposure Min/Max fields define the exposure clamp; cinematic and
///     tint use the authored shared blocks. Tonemap-E, Skyrim White/Strength, LUT grading, and modern
///     bloom remain neutral until their shader equations/resource topology are recovered.
/// </summary>
internal static class ModernImageSpaceReference
{
    internal static float ResolveExposure(float adaptedLuminance, in GpuTonemapSettings settings)
    {
        if (settings.ModernFamily != ImageSpaceModernFamily.Fallout4) return 1f;
        var min = MathF.Min(settings.AutoExposureMin, settings.AutoExposureMax);
        var max = MathF.Max(settings.AutoExposureMin, settings.AutoExposureMax);
        var raw = settings.MiddleGray / MathF.Max(adaptedLuminance, 1e-6f);
        return Math.Clamp(raw, min, max);
    }

    internal static Vector3 Apply(Vector3 scene, float adaptedLuminance, in GpuTonemapSettings settings)
    {
        var color = Vector3.Max(scene, Vector3.Zero)
                    * ResolveExposure(adaptedLuminance, settings)
                    * settings.Exposure;
        var luma = Vector3.Dot(color, new Vector3(0.299f, 0.587f, 0.114f));
        color = Vector3.Lerp(new Vector3(luma), color, settings.Saturation);
        color = Vector3.Lerp(color, new Vector3(luma) * new Vector3(
            settings.TintR, settings.TintG, settings.TintB), settings.TintAmount);
        color = settings.Contrast * (settings.Brightness * color - new Vector3(settings.ContrastAvgLum))
                + new Vector3(settings.ContrastAvgLum);
        return Vector3.Clamp(color, Vector3.Zero, Vector3.One);
    }
}
