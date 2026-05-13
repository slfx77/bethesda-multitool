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

    /// <summary>Per-variant model paths (MODT subrecords on ESM side).</summary>
    public List<string> ModelPaths { get; init; } = [];

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
