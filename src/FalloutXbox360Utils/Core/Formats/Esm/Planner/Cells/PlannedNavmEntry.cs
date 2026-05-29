namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     One new NAVM entry the planner has staged for emission. Mirrors the legacy
///     <c>NewNavmEntry</c> shape (in <c>Plugin/Nav/NavInfoMapBuilder.cs</c>) so the
///     planned writer can synthesize the NAVI override identically. <see cref="LocationFormId" />
///     must be non-zero — the FNV runtime null-derefs at
///     <c>FalloutNV+0x0069DFDC</c> during NavMeshInfoMap iteration if it isn't.
/// </summary>
public sealed record PlannedNavmEntry
{
    /// <summary>The emit-time FormID the NAVM record will use.</summary>
    public required uint NavmFormId { get; init; }

    /// <summary>
    ///     Parent worldspace FormID for exterior cells; parent cell FormID for interior cells.
    ///     For new worldspaces this is the emitted (post-allocation) FormID, not the DMP source.
    /// </summary>
    public required uint LocationFormId { get; init; }

    /// <summary>True for cells inside a top-level CELL GRUP; false for cells inside WRLD.</summary>
    public required bool IsInterior { get; init; }

    /// <summary>Cell grid X coordinate (exterior only; 0 for interior).</summary>
    public required int GridX { get; init; }

    /// <summary>Cell grid Y coordinate (exterior only; 0 for interior).</summary>
    public required int GridY { get; init; }

    /// <summary>
    ///     Raw NVVX subrecord bytes — vertex array used to compute the NVMI centroid the
    ///     engine reads when triangulating navmesh queries. May be empty; the writer falls
    ///     back to the grid centre when so.
    /// </summary>
    public required byte[] NvvxBytes { get; init; }
}
