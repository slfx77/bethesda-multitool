namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>A single world placement of a base object in a cell.</summary>
public record WorldPlacement(PlacedReference Ref, CellRecord Cell);