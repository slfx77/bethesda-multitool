namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Music Type (MUSC) record.
///     Defines a music type with file path and attenuation.
/// </summary>
public record MusicTypeRecord
{
    /// <summary>FormID of the music type record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Music file path (FNAM subrecord / cSoundFile BSStringT).</summary>
    public string? FileName { get; init; }

    /// <summary>Attenuation in dB.</summary>
    public float Attenuation { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
