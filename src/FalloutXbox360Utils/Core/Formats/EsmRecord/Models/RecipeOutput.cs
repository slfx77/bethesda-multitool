namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

public record RecipeOutput
{
    public uint ItemFormId { get; init; }
    public uint Count { get; init; }
}