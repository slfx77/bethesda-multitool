namespace BethesdaMultitool.Core.WorldData;

internal enum InteriorLightingSource { Unavailable, Cell, Template }

internal readonly record struct InteriorLightingField(
    object? Value, InteriorLightingSource Source, bool InheritanceRequested, bool UsedFallback);

/// <summary>Authored XCLL/LGTM field selection shared by rendering and provenance reports.</summary>
internal static class InteriorLightingFieldResolver
{
    // xEdit CELL LNAM / XCLL Inherits flags. Aliases belong to one field, so source
    // precedence must be applied before choosing a spelling (not once per alias).
    internal static readonly string[] Fields =
    [
        "AmbientColor", "DirectionalColor", "FogColor", "FogColorFar", "FogNear", "FogFar",
        "DirectionalRotationXY", "DirectionalRotationZ", "DirectionalFade", "FogClipDistance",
        "FogPower", "FogMax", "LightFadeBegin", "LightFadeEnd"
    ];

    internal static InteriorLightingField Resolve(string field,
        IReadOnlyDictionary<string, object?>? cell,
        IReadOnlyDictionary<string, object?>? template, uint inheritanceFlags)
    {
        var bit = field switch
        {
            "AmbientColor" => 0,
            "DirectionalColor" => 1,
            "FogColor" or "FogColorFar" => 2,
            "FogNear" => 3,
            "FogFar" => 4,
            "DirectionalRotationXY" or "DirectionalRotationZ" => 5,
            "DirectionalFade" => 6,
            "FogClipDistance" or "FogClipDist" => 7,
            "FogPower" or "FogPow" => 8,
            "FogMax" => 9,
            "LightFadeBegin" or "LightFadeEnd" => 10,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown lighting field")
        };
        var inherit = (inheritanceFlags & (1u << bit)) != 0;
        var primary = inherit ? template : cell;
        var secondary = inherit ? cell : template;
        var value = Read(primary, field);
        if (value is not null)
            return new(value, inherit ? InteriorLightingSource.Template : InteriorLightingSource.Cell, inherit, false);
        value = Read(secondary, field);
        return value is not null
            ? new(value, inherit ? InteriorLightingSource.Cell : InteriorLightingSource.Template, inherit, true)
            : new(null, InteriorLightingSource.Unavailable, inherit, false);
    }

    private static object? Read(IReadOnlyDictionary<string, object?>? data, string field)
    {
        if (data is null) return null;
        if (field is "FogPower" or "FogPow")
            return data.GetValueOrDefault("FogPow") ?? data.GetValueOrDefault("FogPower");
        if (field is "FogClipDistance" or "FogClipDist")
            return data.GetValueOrDefault("FogClipDistance") ?? data.GetValueOrDefault("FogClipDist");
        return data.GetValueOrDefault(field);
    }
}
