namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

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

    /// <summary>Texture-hash blob for MODL (MODT subrecord, opaque byte-array passthrough).</summary>
    public byte[]? MaleTextureHashData { get; init; }

    /// <summary>Texture-hash blob for MOD2 (MO2T subrecord).</summary>
    public byte[]? FemaleTextureHashData { get; init; }

    /// <summary>Texture-hash blob for MOD3 (MO3T subrecord).</summary>
    public byte[]? MaleFirstPersonTextureHashData { get; init; }

    /// <summary>Texture-hash blob for MOD4 (MO4T subrecord).</summary>
    public byte[]? FemaleFirstPersonTextureHashData { get; init; }

    /// <summary>Male inventory icon path (ICON subrecord).</summary>
    public string? MaleIconPath { get; init; }

    /// <summary>Female inventory icon path (MIC2 subrecord).</summary>
    public string? FemaleIconPath { get; init; }

    /// <summary>Detection sound level enum from DNAM (Loud=0, Normal=1, Silent=2).</summary>
    public byte DetectionSoundLevel { get; init; }

    /// <summary>Biped flags (which body parts this covers) from BMDT.</summary>
    public uint BipedFlags { get; init; }

    /// <summary>General flags from BMDT.</summary>
    public byte GeneralFlags { get; init; }

    /// <summary>Inventory value in caps (DATA Int32).</summary>
    public int Value { get; init; }

    /// <summary>Maximum condition value (DATA Int32).</summary>
    public int MaxCondition { get; init; }

    /// <summary>Weight in pounds (DATA Float).</summary>
    public float Weight { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
