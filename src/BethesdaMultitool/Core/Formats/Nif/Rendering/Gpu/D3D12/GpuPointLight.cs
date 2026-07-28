using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     CPU mirror of the 64-byte <c>PointLight</c> structured-buffer element declared by the
///     terrain/reference fragment shaders.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct GpuPointLight
{
    public readonly Vector4 PositionRadius;
    public readonly Vector4 ColorIntensity;
    public readonly Vector4 AuthoredMetadata;
    public readonly Vector4 Reserved;

    internal const uint ByteSize = 4 * 16;

    internal GpuPointLight(PlacedLight light, Vector3 renderOrigin)
    {
        PositionRadius = new Vector4(light.Position - renderOrigin, light.Radius);
        ColorIntensity = new Vector4(light.Color, light.Intensity);
        // Preserved for diagnostics/future engine-family ports only. FNV GenDynamic creates an
        // NiPointLight even when these DATA fields/flags are present, so the current shader does
        // not interpret them as a spotlight cone.
        AuthoredMetadata = new Vector4(light.FalloffExponent, light.FieldOfView, light.Flags, 0f);
        Reserved = Vector4.Zero;
    }
}
