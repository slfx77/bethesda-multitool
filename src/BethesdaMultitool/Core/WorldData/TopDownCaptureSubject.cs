namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     What kind of scene a <see cref="TopDownCaptureSubject" /> names. Decides which argument the
///     top-down renderer is driven by — the two are mutually exclusive there.
/// </summary>
internal enum TopDownSubjectKind
{
    /// <summary>An exterior worldspace, rendered by <c>worldspaceFormId</c>.</summary>
    Worldspace,

    /// <summary>A single interior cell, rendered by <c>interiorCellFormId</c> with a ceiling clip.</summary>
    Interior,

    /// <summary>
    ///     The unlinked-exterior set: cells with grid coordinates but no resolved parent worldspace.
    ///     Rendered by passing a null worldspace FormID. Common in memory dumps, where a captured
    ///     cell's owning worldspace is often not recoverable — without this they render nowhere.
    /// </summary>
    UnlinkedExterior
}

/// <summary>
///     One renderable scene for batch top-down capture, with the world-XY rectangle that frames it.
///     <para>
///         The extent is the subject's OWN footprint, not a fixed window: an exterior's cell-grid
///         bounds, an interior's placed-object bounds. A fixed window either crops a large worldspace
///         or wastes most of the image on a small interior.
///     </para>
/// </summary>
/// <param name="Kind">Which renderer argument drives this subject.</param>
/// <param name="FormId">Worldspace or interior cell FormID; ignored for <see cref="TopDownSubjectKind.UnlinkedExterior" />.</param>
/// <param name="Name">EditorID where available, else FullName, else the hex FormID. Used for the output filename.</param>
/// <param name="MinX">West edge of the framed rectangle, world units.</param>
/// <param name="MaxX">East edge.</param>
/// <param name="MinY">South edge (world north is +Y).</param>
/// <param name="MaxY">North edge.</param>
/// <param name="MinZ">
///     Bottom of the content, world units — the lowest placed-object origin in scope. A tilted
///     camera has to frame vertically as well as horizontally, and it must use the subject's REAL
///     height: a fixed allowance that suits a worldspace buries a single room in dead space.
/// </param>
/// <param name="MaxZ">Top of the content, world units.</param>
/// <param name="CellCount">Cells contributing to the extent — diagnostic, and the exterior sort key.</param>
/// <param name="PlacementCount">Placed references in scope — diagnostic; zero means nothing will draw.</param>
/// <param name="HasWater">
///     Whether water should be drawn for this subject. For an interior this is the cell's own water
///     flag (CELL DATA bit 1) — most interiors have none, and drawing a water plane on one that
///     never declared it lays an opaque sheet over the entire floor plan. Exteriors always have it:
///     water there is worldspace-level, not per-cell.
/// </param>
/// <param name="NonPersistentCount">
///     Placements that are NOT persistent refs. This is the capture-residency signal (USER
///     RULING): every cell in the file owns its persistent refs (doors, activators) whether or
///     not it was ever resident, so a subject with ONLY persistent refs was never loaded and
///     renders as doors floating in a void. Non-persistent refs exist precisely because the cell
///     was resident at capture time.
/// </param>
/// <param name="TerrainCellCount">
///     Cells in scope with terrain data (heightmap or runtime terrain mesh). Captured terrain is
///     residency evidence in its own right, so a terrain-only exterior still renders.
/// </param>
internal readonly record struct TopDownCaptureSubject(
    TopDownSubjectKind Kind,
    uint FormId,
    string Name,
    float MinX,
    float MaxX,
    float MinY,
    float MaxY,
    float MinZ,
    float MaxZ,
    int CellCount,
    int PlacementCount,
    bool HasWater,
    int NonPersistentCount = 0,
    int TerrainCellCount = 0)
{
    /// <summary>Width of the framed rectangle in world units.</summary>
    public float Width => MaxX - MinX;

    /// <summary>Height of the framed rectangle in world units.</summary>
    public float Height => MaxY - MinY;
}
