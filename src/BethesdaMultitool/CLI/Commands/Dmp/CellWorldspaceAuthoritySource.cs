namespace BethesdaMultitool.CLI.Commands.Dmp;

internal sealed record CellWorldspaceAuthoritySource(
    string Type,
    string Path,
    int AddedCells,
    int ObservedCells);
