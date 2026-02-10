namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Reconstructed Global Variable (GLOB) from memory dump.
///     Type determined by DATA subrecord: 's' = short, 'l' = long, 'f' = float.
/// </summary>
public record GlobalRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>Value type: 's' = short, 'l' = long, 'f' = float.</summary>
    public char ValueType { get; init; }

    /// <summary>The float-encoded value (all GLOB values are stored as float in DATA).</summary>
    public float Value { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }

    public string DisplayValue => ValueType switch
    {
        's' => ((short)Value).ToString(),
        'l' => ((int)Value).ToString(),
        _ => Value.ToString("F4")
    };

    public string TypeName => ValueType switch
    {
        's' => "Short",
        'l' => "Long",
        'f' => "Float",
        _ => "Unknown"
    };
}
