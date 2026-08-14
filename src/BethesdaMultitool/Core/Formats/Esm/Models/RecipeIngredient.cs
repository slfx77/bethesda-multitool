namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>One input item (FormID + quantity) consumed by an FNV RCPE recipe.</summary>
public record RecipeIngredient
{
    public uint ItemFormId { get; init; }
    public uint Count { get; init; }
}
