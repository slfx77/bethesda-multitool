namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Reconstructed Recipe (RCPE) from memory dump.
///     Links crafting ingredients to output items.
/// </summary>
public record ReconstructedRecipe
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>Required skill actor value index from DATA.</summary>
    public int RequiredSkill { get; init; }

    /// <summary>Required skill level from DATA.</summary>
    public int RequiredSkillLevel { get; init; }

    /// <summary>Recipe category FormID from DATA.</summary>
    public uint CategoryFormId { get; init; }

    /// <summary>Subcategory from DATA.</summary>
    public uint SubcategoryFormId { get; init; }

    /// <summary>Crafting ingredients (RCIL subrecords).</summary>
    public List<RecipeIngredient> Ingredients { get; init; } = [];

    /// <summary>Output items (RCOD subrecords).</summary>
    public List<RecipeOutput> Outputs { get; init; } = [];

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}