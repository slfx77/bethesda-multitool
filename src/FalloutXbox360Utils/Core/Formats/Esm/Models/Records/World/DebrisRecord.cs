namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Debris (DEBR) record. List of broken-object models spawned when an
///     object is destroyed. PDB struct: BGSDebris (52 bytes, FormType 0x52)
///     — single own-class field is a BSSimpleList of debris variants.
/// </summary>
public record DebrisRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>Number of debris variants (DATA subrecords on ESM; BSSimpleList nodes at runtime).</summary>
    public int VariantCount { get; init; }

    /// <summary>Per-variant DATA payloads from ESM side.</summary>
    public List<DebrisVariantData> Variants { get; init; } = [];

    /// <summary>Per-variant model paths. Kept for runtime-derived records and older callers.</summary>
    public List<string> ModelPaths { get; init; } = [];

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     One DEBR DATA payload: 1-byte percentage, null-terminated model path, then 1-byte flags.
/// </summary>
public sealed record DebrisVariantData(byte Percentage, string ModelPath, byte Flags);
