namespace BethesdaMultitool.Core.Formats.Esm;

internal sealed record CellWorldspaceAuthorityLoadResult(
    Dictionary<uint, CellAuthorityMetadata>? Cells,
    Dictionary<uint, uint>? RefToCell,
    List<CellReferenceParentWindow>? RefWindows,
    Dictionary<uint, string>? WorldspaceNames,
    string? Path,
    string? Warning)
{
    public Dictionary<uint, uint>? CellToWorldspace => Cells?
        .Where(kv => kv.Value.WorldspaceFormId is > 0)
        .ToDictionary(kv => kv.Key, kv => kv.Value.WorldspaceFormId!.Value);
}
