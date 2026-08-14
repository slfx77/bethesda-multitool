namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Ingredient (INGR) record. Legacy Oblivion-era alchemy ingredient.
///     This is a partial forensic projection: FNV's separate ENIT block and required effect
///     group are not represented, so new INGR emission is intentionally disabled.
///     PDB struct: IngredientItem (180 bytes, FormType 0x1D).
/// </summary>
public record IngredientRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public string? ModelPath { get; init; }

    /// <summary>Item weight (fWeight at +152).</summary>
    public float Weight { get; init; }

    /// <summary>Equip type enum (BGSEquipType at +160).</summary>
    public uint EquipType { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
