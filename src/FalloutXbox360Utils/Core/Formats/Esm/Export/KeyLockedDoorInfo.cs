using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Compact reverse-reference details for a key's locked placed references.
///     Built while cells are still loaded so key reports can retain door/cell links.
/// </summary>
internal sealed record KeyLockedDoorInfo(
    PlacedReference Ref,
    uint CellFormId,
    string? CellEditorId,
    string? CellName,
    uint? WorldspaceFormId,
    int? GridX,
    int? GridY);
