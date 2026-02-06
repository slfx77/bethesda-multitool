namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

public record RecipeIngredient
{
    public uint ItemFormId { get; init; }
    public uint Count { get; init; }
}
