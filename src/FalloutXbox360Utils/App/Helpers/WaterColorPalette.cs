using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils;

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
    ///     the overlay's Shallow→Deep lerp degenerates to a single colour cleanly.
    /// </summary>
    internal static WaterColorPalette? FromVisualProperties(IReadOnlyDictionary<string, object?>? props)
    {
        if (props is null) return null;

        var shallow = ExtractColor(props, "ShallowColor");
        var deep = ExtractColor(props, "DeepColor");
        if (shallow is null && deep is null) return null;

        var s = shallow ?? deep!.Value;
        var d = deep ?? shallow!.Value;
        return new WaterColorPalette(s, d);
    }

    private static (byte R, byte G, byte B)? ExtractColor(
        IReadOnlyDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var value) || value is null) return null;
        var packed = value switch
        {
            uint u => u,
            int i => (uint)i,
            _ => 0u
        };
        // A packed value of 0 is ambiguous (could be "no color set" or "actual black"); treat
        // as missing so a record with only one valid color falls back to that one rather than
        // lerping toward black.
        if (packed == 0) return null;
        return ((byte)(packed & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)((packed >> 16) & 0xFF));
    }
}
