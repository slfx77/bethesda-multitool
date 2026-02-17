namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Armor Addon (ARMA) record.
///     Defines the visual model for an armor piece, with separate male/female body part models.
/// </summary>
public record ArmaRecord
{
    /// <summary>FormID of the armor addon record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (FULL subrecord).</summary>
    public string? FullName { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Male biped model path (MODL subrecord).</summary>
    public string? MaleModelPath { get; init; }

    /// <summary>Female biped model path (MOD2 subrecord).</summary>
    public string? FemaleModelPath { get; init; }

    /// <summary>Male first-person model path (MOD3 subrecord).</summary>
    public string? MaleFirstPersonModelPath { get; init; }

    /// <summary>Female first-person model path (MOD4 subrecord).</summary>
    public string? FemaleFirstPersonModelPath { get; init; }

    /// <summary>Biped flags (which body parts this covers).</summary>
    public uint BipedFlags { get; init; }

    /// <summary>General flags.</summary>
    public uint GeneralFlags { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
