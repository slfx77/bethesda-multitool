using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Export;

/// <summary>
///     Compact placement details for a container base record.
/// </summary>
internal sealed record ContainerPlacementInfo(
    PlacedReference Ref,
    uint CellFormId,
    string? CellEditorId,
    string? CellName,
    uint? WorldspaceFormId,
    int? GridX,
    int? GridY);
