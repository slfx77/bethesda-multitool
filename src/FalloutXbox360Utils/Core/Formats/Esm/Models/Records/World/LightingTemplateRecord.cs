namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Lighting Template (LGTM) record.
///     Defines interior cell lighting properties (ambient, directional, fog colors/distances).
/// </summary>
public record LightingTemplateRecord
{
    /// <summary>FormID of the lighting template record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Lighting data fields (from DATA subrecord, 40 bytes, schema-parsed).</summary>
    public Dictionary<string, object?>? LightingData { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
