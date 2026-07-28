using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>Resolves REFR XEMI targets and applies the recovered effect-shader color blend.</summary>
internal static class ExternalEmittanceResolver
{
    public static Dictionary<uint, Vector3> BuildIndex(
        IReadOnlyList<RegionRecord> regions,
        IReadOnlyList<LightRecord> lights)
    {
        var result = new Dictionary<uint, Vector3>(regions.Count + lights.Count);
        foreach (var region in regions)
        {
            result[region.FormId] = new Vector3(
                region.EmittanceColorR / 255f,
                region.EmittanceColorG / 255f,
                region.EmittanceColorB / 255f);
        }

        foreach (var light in lights)
        {
            var packed = light.Color;
            result[light.FormId] = new Vector3(
                (packed & 0xff) / 255f,
                ((packed >> 8) & 0xff) / 255f,
                ((packed >> 16) & 0xff) / 255f);
        }

        return result;
    }

    /// <summary>
    ///     Retail BSEffect unlit path: <c>rgb *= lerp(1, externalColor, LightingInfluence)</c>.
    ///     Classic external-emittance properties have no packed influence and pass 1.
    /// </summary>
    public static Vector3 Modulation(Vector3 externalColor, float influence) =>
        Vector3.Lerp(Vector3.One, externalColor, Math.Clamp(influence, 0f, 1f));
}
