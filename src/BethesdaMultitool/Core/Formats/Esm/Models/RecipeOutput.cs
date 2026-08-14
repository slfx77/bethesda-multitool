namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>The output item (FormID + quantity) produced by an FNV RCPE recipe.</summary>
public record RecipeOutput
{
    public uint ItemFormId { get; init; }
    public uint Count { get; init; }
}
