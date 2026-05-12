namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Parsed Recipe Category (RCCT) record. Categories are lightweight lookup tables
///     referenced from RecipeRecord.CategoryFormId / SubcategoryFormId.
///     PDB struct: TESRecipeCategory (56 bytes). Subrecord layout: EDID + FULL? + DATA(1B flags).
/// </summary>
public record RecipeCategoryRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>Category flags from DATA subrecord (1 byte).</summary>
    public byte Flags { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
