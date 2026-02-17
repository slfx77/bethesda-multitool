namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Sound (SOUN) record.
///     Defines a sound effect with file path and playback properties.
/// </summary>
public record SoundRecord
{
    /// <summary>FormID of the sound record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Sound file path (FNAM subrecord).</summary>
    public string? FileName { get; init; }

    /// <summary>Minimum attenuation distance.</summary>
    public ushort MinAttenuationDistance { get; init; }

    /// <summary>Maximum attenuation distance.</summary>
    public ushort MaxAttenuationDistance { get; init; }

    /// <summary>Static attenuation (in hundredths of dB).</summary>
    public short StaticAttenuation { get; init; }

    /// <summary>Sound flags (loop, rumble, etc.).</summary>
    public uint Flags { get; init; }

    /// <summary>Start time (0-24).</summary>
    public byte StartTime { get; init; }

    /// <summary>End time (0-24).</summary>
    public byte EndTime { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
