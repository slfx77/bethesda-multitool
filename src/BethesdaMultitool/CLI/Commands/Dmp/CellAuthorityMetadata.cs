using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Esm;

public sealed record CellAuthorityMetadata
{
    public uint? WorldspaceFormId { get; init; }
    public bool? IsInterior { get; init; }
    public int? GridX { get; init; }
    public int? GridY { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    public static CellAuthorityMetadata FromCell(CellRecord cell, uint? worldspaceFormId = null)
    {
        return new CellAuthorityMetadata
        {
            WorldspaceFormId = cell.IsInterior ? null : worldspaceFormId ?? cell.WorldspaceFormId,
            IsInterior = cell.IsInterior,
            GridX = cell.IsInterior ? null : cell.GridX,
            GridY = cell.IsInterior ? null : cell.GridY,
            EditorId = cell.EditorId,
            FullName = cell.FullName
        };
    }

    public CellAuthorityMetadata MergeMissing(CellAuthorityMetadata other)
    {
        return this with
        {
            WorldspaceFormId = WorldspaceFormId is > 0 ? WorldspaceFormId : other.WorldspaceFormId,
            IsInterior = IsInterior ?? other.IsInterior,
            GridX = GridX ?? other.GridX,
            GridY = GridY ?? other.GridY,
            EditorId = string.IsNullOrWhiteSpace(EditorId) ? other.EditorId : EditorId,
            FullName = string.IsNullOrWhiteSpace(FullName) ? other.FullName : FullName
        };
    }
}
