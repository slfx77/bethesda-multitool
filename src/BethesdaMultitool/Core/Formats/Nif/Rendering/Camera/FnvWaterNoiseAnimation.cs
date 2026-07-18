using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Recovered FO3/FNV WATR noise-layer animation math shared by the renderer and retail-data
///     regression tests. DNAM directions are compass-style: zero degrees scrolls toward +Y.
/// </summary>
internal static class FnvWaterNoiseAnimation
{
    public static float TextureScale(float authoredScale) =>
        MathF.Max(1f, MathF.Ceiling(authoredScale * 0.01f));

    public static Vector2 Scroll(WaterNoiseLayer layer, float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds) || !float.IsFinite(layer.WindDirDegrees) ||
            !float.IsFinite(layer.WindSpeed))
        {
            return Vector2.Zero;
        }

        // Keep the recovered direction reduction in float, but accumulate phase in double. Retail
        // DefaultWater literally carries the MSVC debug-fill float 0xCDCDCDCD in layers 2/3; the
        // engine integrates those finite values a frame at a time. A single-precision closed form
        // loses every fractional UV at that magnitude and incorrectly makes the surface stationary.
        // Double produces the deterministic continuous-time equivalent of the recovered incremental
        // wrap; it intentionally does not reproduce frame-rate-dependent float-rounding aliases.
        var radians = layer.WindDirDegrees * (MathF.PI / 180f);
        var distance = (double)layer.WindSpeed * elapsedSeconds;
        return new Vector2(
            WrapUnit(MathF.Sin(radians) * distance),
            WrapUnit(MathF.Cos(radians) * distance));
    }

    private static float WrapUnit(double value)
    {
        if (!double.IsFinite(value)) return 0f;

        var wrapped = value - Math.Floor(value);
        // Trig at cardinal directions can produce a tiny negative residual, which wraps to almost
        // one even though it is texture-address-equivalent to zero. Canonicalize that seam so CPU
        // animation assertions and uploaded constants remain stable.
        return wrapped <= 1e-6 || wrapped >= 1.0 - 1e-6 ? 0f : (float)wrapped;
    }
}
