using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Compact placement details for an NPC base record. Built while cells are still
///     loaded so NPC reports can show world usage without retaining full CellRecord data.
/// </summary>
internal sealed record NpcPlacementInfo(
    PlacedReference Ref,
    uint CellFormId,
    string? CellEditorId,
    string? CellName,
    uint? WorldspaceFormId,
    int? GridX,
    int? GridY);
