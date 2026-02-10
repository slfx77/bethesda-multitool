namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Light (LIGH) record.
///     Defines a light source with radius, color, duration, and flags.
/// </summary>
public record LightRecord
{
    /// <summary>FormID of the light record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    // DATA subrecord (32 bytes)
    /// <summary>Duration in seconds (0 = infinite).</summary>
    public int Duration { get; init; }

    /// <summary>Light radius.</summary>
    public uint Radius { get; init; }

    /// <summary>Light color as RGBA packed value.</summary>
    public uint Color { get; init; }

    /// <summary>Light flags (Can Take, Flicker, Off By Default, etc.).</summary>
    public uint Flags { get; init; }

    /// <summary>Falloff exponent.</summary>
    public float FalloffExponent { get; init; }

    /// <summary>Field of View angle.</summary>
    public float FOV { get; init; }

    /// <summary>Base value in caps.</summary>
    public int Value { get; init; }

    /// <summary>Weight.</summary>
    public float Weight { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
