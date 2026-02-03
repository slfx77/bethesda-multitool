using FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Fully reconstructed Game Setting (GMST) from memory dump.
///     The setting type is determined by the first letter of the Editor ID:
///     'f' = float, 'i' = integer, 's' = string, 'b' = boolean.
/// </summary>
public record ReconstructedGameSetting
{
    /// <summary>FormID of the GMST record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (setting name, e.g., "fActorStrengthEncumbranceMult").</summary>
    public string? EditorId { get; init; }

    /// <summary>The type of value this setting holds.</summary>
    public GameSettingType ValueType { get; init; }

    /// <summary>Float value (if ValueType is Float).</summary>
    public float? FloatValue { get; init; }

    /// <summary>Integer value (if ValueType is Integer or Boolean).</summary>
    public int? IntValue { get; init; }

    /// <summary>String value (if ValueType is String).</summary>
    public string? StringValue { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable value representation.</summary>
    public string DisplayValue => ValueType switch
    {
        GameSettingType.Float => FloatValue?.ToString("F4") ?? "null",
        GameSettingType.Integer => IntValue?.ToString() ?? "null",
        GameSettingType.Boolean => IntValue != 0 ? "true" : "false",
        GameSettingType.String => StringValue ?? "null",
        _ => "unknown"
    };
}
