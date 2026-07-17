using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool;

/// <summary>Where the cell used for image-space selection came from.</summary>
internal enum ImageSpaceCellContextSource : uint
{
    Unavailable = 0,
    SelectedInterior = 1,
    CameraGrid = 2,
    ProjectionFocusGrid = 3,
}

/// <summary>
///     The active CELL query for image-space selection. Grid coordinates are retained even when the
///     loaded world has no CELL at that coordinate, so captures can distinguish a missing tile from
///     an unavailable camera/grid context.
/// </summary>
internal readonly record struct ImageSpaceCellContext(
    CellRecord? Cell,
    int? GridX,
    int? GridY,
    ImageSpaceCellContextSource Source)
{
    internal uint? CellFormId => Cell?.FormId;

    internal string SourceTelemetry => Source switch
    {
        ImageSpaceCellContextSource.SelectedInterior => "selected-interior",
        ImageSpaceCellContextSource.CameraGrid => "camera-grid",
        ImageSpaceCellContextSource.ProjectionFocusGrid => "projection-focus-grid",
        _ => "unavailable",
    };

    internal static ImageSpaceCellContext ForSelectedInterior(CellRecord cell) =>
        new(cell, cell.GridX, cell.GridY, ImageSpaceCellContextSource.SelectedInterior);
}

/// <summary>The authoritative source of the base IMGS selected for the current view.</summary>
internal enum ImageSpaceSelectionSource : uint
{
    Unavailable = 0,
    CellXcim = 1,
    WorldspaceInam = 2,
    ParentWorldspaceInam = 3,
    DefaultInterior = 4,
    DefaultExterior = 5,
    GameFamilyDefault = 6,
}

/// <summary>
///     Pure result of TESObjectCELL::GetUsableImageSpace-style selection. The context WRLD is the
///     world being viewed; the source WRLD is the direct or inherited record that supplied INAM.
/// </summary>
internal readonly record struct ResolvedImageSpaceSelection(
    uint? ImageSpaceFormId,
    ImageSpaceSelectionSource Source,
    ImageSpaceCellContext CellContext,
    uint? ContextWorldspaceFormId,
    uint? SourceWorldspaceFormId)
{
    internal uint HistoryContextId => ContextWorldspaceFormId ?? CellContext.CellFormId ?? 0u;

    internal uint HistoryCellId => CellContext.CellFormId ?? 0u;

    internal uint HistorySourceTag => (uint)Source;

    internal string SourceTelemetry => Source switch
    {
        ImageSpaceSelectionSource.CellXcim => "cell-xcim",
        ImageSpaceSelectionSource.WorldspaceInam => "worldspace-inam",
        ImageSpaceSelectionSource.ParentWorldspaceInam => "parent-worldspace-inam",
        ImageSpaceSelectionSource.DefaultInterior => "default-interior",
        ImageSpaceSelectionSource.DefaultExterior => "default-exterior",
        ImageSpaceSelectionSource.GameFamilyDefault => "game-family-default",
        _ => "unavailable",
    };
}

/// <summary>
///     Resolves the active camera CELL and its usable image space without UI or renderer state.
/// </summary>
internal static class ImageSpaceSelectionResolver
{
    internal const uint DefaultImageSpaceInteriorFormId = 0x160;
    internal const uint DefaultImageSpaceExteriorFormId = 0x161;

    private const ushort UseParentImageSpaceFlag = 1 << 5;

    /// <summary>
    ///     Maps a world position to an exterior CELL with the same floor rule used by terrain,
    ///     reference streaming, and walk collision. Exact positive boundaries enter the next cell;
    ///     negative fractional coordinates remain in the negative cell.
    /// </summary>
    internal static ImageSpaceCellContext ResolveExteriorCell(
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellsByGrid,
        float worldX,
        float worldY,
        float cellSize,
        ImageSpaceCellContextSource source = ImageSpaceCellContextSource.CameraGrid)
    {
        if (cellsByGrid is null || !float.IsFinite(worldX) || !float.IsFinite(worldY)
            || !float.IsFinite(cellSize) || cellSize <= 0f)
        {
            return new ImageSpaceCellContext(null, null, null, ImageSpaceCellContextSource.Unavailable);
        }

        var gx = (int)MathF.Floor(worldX / cellSize);
        var gy = (int)MathF.Floor(worldY / cellSize);
        cellsByGrid.TryGetValue((gx, gy), out var cell);
        return new ImageSpaceCellContext(cell, gx, gy, source);
    }

    /// <summary>
    ///     Engine precedence: active CELL XCIM, then the context WRLD's usable INAM (following PNAM
    ///     bit 5 through its parent chain), then DefaultImageSpaceInterior/Exterior for the classic
    ///     engine family. Other families retain their renderer family default when no authored IMGS
    ///     resolves.
    /// </summary>
    internal static ResolvedImageSpaceSelection Resolve(
        ImageSpaceCellContext cellContext,
        WorldspaceRecord? contextWorldspace,
        IReadOnlyList<WorldspaceRecord>? allWorldspaces,
        bool interior,
        bool useClassicDefault)
    {
        if (cellContext.Cell?.ImageSpaceFormId is uint cellImageSpaceId && cellImageSpaceId != 0)
        {
            return new ResolvedImageSpaceSelection(
                cellImageSpaceId,
                ImageSpaceSelectionSource.CellXcim,
                cellContext,
                contextWorldspace?.FormId,
                SourceWorldspaceFormId: null);
        }

        if (TryResolveWorldspaceImageSpace(
                contextWorldspace, allWorldspaces,
                out var worldImageSpaceId, out var sourceWorldspaceId, out var inherited))
        {
            return new ResolvedImageSpaceSelection(
                worldImageSpaceId,
                inherited
                    ? ImageSpaceSelectionSource.ParentWorldspaceInam
                    : ImageSpaceSelectionSource.WorldspaceInam,
                cellContext,
                contextWorldspace?.FormId,
                sourceWorldspaceId);
        }

        if (useClassicDefault)
        {
            return new ResolvedImageSpaceSelection(
                interior ? DefaultImageSpaceInteriorFormId : DefaultImageSpaceExteriorFormId,
                interior ? ImageSpaceSelectionSource.DefaultInterior : ImageSpaceSelectionSource.DefaultExterior,
                cellContext,
                contextWorldspace?.FormId,
                SourceWorldspaceFormId: null);
        }

        return new ResolvedImageSpaceSelection(
            ImageSpaceFormId: null,
            ImageSpaceSelectionSource.GameFamilyDefault,
            cellContext,
            contextWorldspace?.FormId,
            SourceWorldspaceFormId: null);
    }

    private static bool TryResolveWorldspaceImageSpace(
        WorldspaceRecord? contextWorldspace,
        IReadOnlyList<WorldspaceRecord>? allWorldspaces,
        out uint imageSpaceFormId,
        out uint sourceWorldspaceFormId,
        out bool inherited)
    {
        imageSpaceFormId = 0;
        sourceWorldspaceFormId = 0;
        inherited = false;
        if (contextWorldspace is null)
        {
            return false;
        }

        var current = contextWorldspace;
        var visited = new HashSet<uint>();
        while (visited.Add(current.FormId))
        {
            var usesParentImageSpace = (current.ParentUseFlags.GetValueOrDefault() & UseParentImageSpaceFlag) != 0;
            if (!usesParentImageSpace)
            {
                var resolvedId = current.ImageSpaceFormId.GetValueOrDefault();
                if (resolvedId == 0)
                {
                    return false;
                }

                imageSpaceFormId = resolvedId;
                sourceWorldspaceFormId = current.FormId;
                return true;
            }

            var parentId = current.ParentWorldspaceFormId.GetValueOrDefault();
            if (parentId == 0 || FindWorldspace(allWorldspaces, parentId) is not { } parent)
            {
                // PNAM says the parent owns this field. Do not silently fall back to a child INAM
                // when the retained parent is unavailable; the engine's default is the safe result.
                return false;
            }

            inherited = true;
            current = parent;
        }

        // Malformed parent cycle: fail closed to the family default.
        return false;
    }

    private static WorldspaceRecord? FindWorldspace(
        IReadOnlyList<WorldspaceRecord>? allWorldspaces,
        uint formId)
    {
        if (allWorldspaces is null)
        {
            return null;
        }

        foreach (var worldspace in allWorldspaces)
        {
            if (worldspace.FormId == formId)
            {
                return worldspace;
            }
        }

        return null;
    }
}
