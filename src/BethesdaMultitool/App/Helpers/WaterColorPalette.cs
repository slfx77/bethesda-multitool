using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool;

/// <summary>
///     Per-worldspace water tint pair (Shallow + Deep) sourced from the WATR record's DNAM
///     colors. The 2D map's water overlay lerps Shallow→Deep by the water-mask intensity:
///     fully-blurred shorelines render Shallow, interior water renders Deep. Reflection
///     color is intentionally unused — runtime mixes it via Fresnel in the pixel shader,
///     which isn't reproducible at overview scale.
///     <para>
///         Decompile of <c>TESWaterSystem::UpdateWaterShaderProperties</c> shows the
///         packed uint32 bytes map to RGB as: <c>R = packed &amp; 0xFF</c>,
///         <c>G = (packed &gt;&gt; 8) &amp; 0xFF</c>, <c>B = (packed &gt;&gt; 16) &amp; 0xFF</c>
///         (alpha byte unused — forced to 1.0 at shader upload). The schema reader produces
///         canonical-LE uints regardless of source platform, so this mapping holds.
///     </para>
///     <para>
///         Returns null when the WATR record is missing, has no DNAM, or DNAM lacks both
///         colors — caller falls back to the legacy solid-blue tint.
///     </para>
/// </summary>
internal sealed record WaterColorPalette(
    (byte R, byte G, byte B) Shallow,
    (byte R, byte G, byte B) Deep)
{
    internal static WaterColorPalette? GetOrCreate(WorldViewData data, uint waterFormId)
    {
        if (waterFormId == 0) return null;
        if (!data.WatersByFormId.TryGetValue(waterFormId, out var water)) return null;
        return FromVisualProperties(water.VisualProperties);
    }

    /// <summary>
    ///     Direct factory off a WATR DNAM properties dictionary. Returns null when both
    ///     ShallowColor and DeepColor are absent (or both zero) — caller falls back to a
    ///     solid tint. When only one is present, the missing endpoint mirrors the other so
    ///     the overlay's Shallow→Deep lerp degenerates to a single color cleanly.
    /// </summary>
    internal static WaterColorPalette? FromVisualProperties(IReadOnlyDictionary<string, object?>? props)
    {
        // Delegate to the shared Core decoder so the packed-uint32 → RGB mapping lives in one
        // place (the 3D WaterRenderer12 reads the same WaterAppearance). The 2D overlay only
        // needs Shallow + Deep; Reflection is decoded but unused here (runtime Fresnel-mixes it,
        // which isn't reproducible at overview scale).
        var appearance = WaterAppearance.FromVisualProperties(props, noiseTexture: null);
        return appearance is null ? null : new WaterColorPalette(appearance.Shallow, appearance.Deep);
    }
}
