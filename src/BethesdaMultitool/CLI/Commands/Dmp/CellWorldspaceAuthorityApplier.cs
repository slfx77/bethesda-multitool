using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Cells;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;

namespace BethesdaMultitool.Core.Formats.Esm;

internal sealed record CellWorldspaceAuthorityApplyResult(
    int Applied,
    int Added,
    int Overrode,
    int SynthesizedWorldspaces,
    int TerrainCellsAttached = 0,
    int ReferencesReattached = 0,
    int ReferenceCellsCreated = 0,
    int ReferenceWindowsApplied = 0,
    int ReferenceWindowAmbiguousMatches = 0);

internal static class CellWorldspaceAuthorityApplier
{
    private const string SourceAuthorityOffsetCluster = "AuthorityOffsetCluster";
    private const string SourceAuthorityRefWindow = "AuthorityRefWindow";
    private const int AuthorityOffsetClusterGapBytes = 0x1000;
    private const int AuthorityOffsetClusterWindowBytes = 0x20000;
    private const int AuthorityOffsetClusterNearestBandBytes = 0x4000;
    private const int AuthorityOffsetClusterGridExpansion = 2;
    private const int AuthorityOffsetClusterMinPlacements = 2;
    private const string SourceOffsetCluster = "OffsetCluster";
    private const string SourceVirtual = "Virtual";
    private const string SourceInteriorOffsetCluster = "InteriorOffsetCluster";
    private const string SourceBoundsInference = "WorldspaceBoundsInference";

    /// <summary>Exact-grid reassignment requires this many placements in the target cell.</summary>
    private const int ExactGridReassignmentMinPlacements = 10;

    private readonly record struct ResolvedReferenceWindow(
        uint CellFormId,
        long MinOffset,
        long MaxOffset,
        string? Label);

    /// <summary>
    ///     Applies authoritative CELL metadata to the semantic record model and rebuilds
    ///     worldspace child lists so reports and map views see the same ownership that
    ///     DMP to ESM conversion uses.
    /// </summary>
    public static CellWorldspaceAuthorityApplyResult Apply(
        RecordCollection records,
        IReadOnlyDictionary<uint, uint>? authority,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null,
        EsmRecordScanResult? scanResult = null,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata = null,
        IReadOnlyDictionary<uint, uint>? refToCell = null,
        IReadOnlyList<CellReferenceParentWindow>? refWindows = null,
        bool inferUnresolvedPlacements = true)
    {
        cellMetadata ??= authority?.ToDictionary(
            kv => kv.Key,
            kv => new CellAuthorityMetadata { WorldspaceFormId = kv.Value });
        var hasCellMetadata = cellMetadata is { Count: > 0 };
        var hasReferenceParents = refToCell is { Count: > 0 };
        var hasReferenceWindows = refWindows is { Count: > 0 };
        if ((!hasCellMetadata && !hasReferenceParents && !hasReferenceWindows) || records.Cells.Count == 0)
        {
            return new CellWorldspaceAuthorityApplyResult(0, 0, 0, 0);
        }

        var applied = 0;
        var overrode = 0;
        var added = 0;
        var matched = 0;

        for (var i = 0; hasCellMetadata && i < records.Cells.Count; i++)
        {
            var cell = records.Cells[i];
            if (!cellMetadata!.TryGetValue(cell.FormId, out var metadata))
            {
                continue;
            }

            matched++;
            var wsFid = metadata.IsInterior == true ? null : metadata.WorldspaceFormId;
            if (scanResult is not null && wsFid is > 0)
            {
                scanResult.CellToWorldspaceMap[cell.FormId] = wsFid.Value;
            }

            var flags = cell.Flags;
            if (metadata.IsInterior == true)
            {
                flags |= 0x01;
            }
            else if (metadata.IsInterior == false || wsFid is > 0)
            {
                flags = (byte)(flags & ~0x01);
            }

            if ((metadata.IsInterior == true && cell.WorldspaceFormId is > 0) ||
                (wsFid is > 0 && cell.WorldspaceFormId is { } prior && prior != 0u && prior != wsFid.Value))
            {
                overrode++;
            }
            else if (wsFid is > 0 && (cell.WorldspaceFormId is null || cell.WorldspaceFormId == 0u))
            {
                added++;
            }

            var gridX = metadata.IsInterior == true ? null : metadata.GridX ?? cell.GridX;
            var gridY = metadata.IsInterior == true ? null : metadata.GridY ?? cell.GridY;
            var updated = cell with
            {
                Flags = flags,
                GridX = gridX,
                GridY = gridY,
                WorldspaceFormId = metadata.IsInterior == true ? null : wsFid ?? cell.WorldspaceFormId,
                WorldspaceAssignmentSource = wsFid is > 0 ? "Authority" : cell.WorldspaceAssignmentSource,
                EditorId = string.IsNullOrWhiteSpace(cell.EditorId) ? metadata.EditorId : cell.EditorId,
                FullName = string.IsNullOrWhiteSpace(cell.FullName) ? metadata.FullName : cell.FullName
            };

            if (updated != cell)
            {
                records.Cells[i] = updated;
                applied++;
            }
        }

        var terrainBefore = CountCellsWithTerrain(records);
        var referenceMove = ReattachUnresolvedReferences(
            records,
            refToCell,
            cellMetadata,
            authority,
            scanResult);
        var windowMove = ReattachWindowedUnresolvedReferences(
            records,
            refWindows,
            cellMetadata,
            authority,
            scanResult);
        var offsetMove = ReattachOffsetClusteredUnresolvedReferences(records, scanResult);
        var interiorMove = ReattachOffsetClusteredInteriorUnresolvedReferences(records, scanResult);
        var boundsMove = inferUnresolvedPlacements
            ? ReattachUnresolvedByBoundsInference(records, scanResult)
            : (Moved: 0, CreatedCells: 0);

        if (scanResult is not null &&
            (matched > 0 || referenceMove.Moved > 0 || windowMove.Moved > 0 || offsetMove.Moved > 0 ||
             interiorMove.Moved > 0 || boundsMove.Moved > 0))
        {
            EsmLandEnricher.EnrichLandRecordsWithCellWorldspaces(scanResult, records.Cells);
            CellRecordHandler.AttachTerrainDataFromLandRecords(records.Cells, scanResult);
        }

        var synthesized = RebuildWorldspaceCellLists(records, worldspaceNames);
        var terrainAttached = Math.Max(0, CountCellsWithTerrain(records) - terrainBefore);
        return new CellWorldspaceAuthorityApplyResult(
            applied,
            added,
            overrode,
            synthesized,
            terrainAttached,
            referenceMove.Moved + windowMove.Moved + offsetMove.Moved + interiorMove.Moved + boundsMove.Moved,
            referenceMove.CreatedCells + windowMove.CreatedCells + offsetMove.CreatedCells
                + interiorMove.CreatedCells + boundsMove.CreatedCells,
            windowMove.AppliedWindows,
            windowMove.AmbiguousMatches);
    }

    /// <summary>
    ///     Final rescue pass for refs still stranded in <c>[Unresolved …]</c> buckets after the
    ///     authority / window / offset-cluster passes: place each ref by its own coordinates,
    ///     using (1) an exact-grid match against a captured real exterior cell whose grid coord
    ///     is unique across all captured cells and that holds at least
    ///     <see cref="ExactGridReassignmentMinPlacements" /> placements, then (2) unique
    ///     containment of the ref's grid inside exactly ONE worldspace's captured grid bounds.
    ///     Ambiguous containment (grids inside two worldspaces' spans) leaves the ref where it
    ///     is — this pass never guesses between candidates.
    ///     <para>
    ///     USER RULING 2026-08-05 (playtest finding 3, Utl* block): position inference is ON by
    ///     default and opt-out via <c>--no-cell-inference</c>. Worldspace membership derived
    ///     this way is an inference, not a capture — every moved ref carries
    ///     <c>AssignmentSource = "WorldspaceBoundsInference"</c> and every synthesized cell
    ///     carries the same <c>WorldspaceAssignmentSource</c>, so reports can always separate
    ///     inferred placements from captured ones. Cells synthesized here are REAL exterior
    ///     cells (grid + worldspace, no EditorId — CK convention), NOT <c>IsVirtual</c>: the
    ///     planner removes virtual cells as parse-time buckets, which would silently kill the
    ///     temporary refs this pass exists to save.
    ///     </para>
    /// </summary>
    private static (int Moved, int CreatedCells) ReattachUnresolvedByBoundsInference(
        RecordCollection records,
        EsmRecordScanResult? scanResult)
    {
        var realExteriorCells = records.Cells
            .Where(cell => !cell.IsInterior &&
                           !cell.IsVirtual &&
                           !cell.IsUnresolvedBucket &&
                           cell.WorldspaceFormId is > 0 &&
                           cell.GridX.HasValue &&
                           cell.GridY.HasValue)
            .ToList();

        if (realExteriorCells.Count == 0)
        {
            return (0, 0);
        }

        // Exact-grid targets: captured cells whose (gx,gy) is globally unique and that carry
        // enough placements to prove the grid really was loaded there.
        var exactGrid = new Dictionary<(int Gx, int Gy), CellRecord?>();
        foreach (var cell in realExteriorCells)
        {
            var key = (cell.GridX!.Value, cell.GridY!.Value);
            exactGrid[key] = exactGrid.ContainsKey(key) ? null : cell; // null = ambiguous
        }

        var worldspaceBounds = realExteriorCells
            .GroupBy(cell => cell.WorldspaceFormId!.Value)
            .Select(g => (Ws: g.Key,
                MinX: g.Min(c => c.GridX!.Value), MinY: g.Min(c => c.GridY!.Value),
                MaxX: g.Max(c => c.GridX!.Value), MaxY: g.Max(c => c.GridY!.Value)))
            .ToList();

        // Elevation plausibility for the X/Y containment test below, measured from CAPTURED cells only
        // and BEFORE this pass fabricates anything. Without it, an uncaptured interior's refs (interior-
        // LOCAL coordinates) get fabricated into whichever exterior worldspace's grid span happens to
        // contain them — confirmed on xex21, where 463 Hoover Dam interior placements at Z 10368-12416
        // landed in TheStripWorld, whose real captured content tops out at Z 2667.
        var verticalBand = WorldspaceVerticalBand.Measure(records.Cells);

        var cellIndexByFormId = BuildCellIndex(records.Cells);
        var moved = 0;
        var createdCells = 0;
        var rejectedByElevation = 0;
        var nextSyntheticFormId = NextAvailableSyntheticCellFormId(records, 0xFE900001u);

        for (var i = 0; i < records.Cells.Count; i++)
        {
            var source = records.Cells[i];
            if (!source.IsUnresolvedBucket || source.PlacedObjects.Count == 0)
            {
                continue;
            }

            var movedFormIds = new HashSet<uint>();
            foreach (var placed in source.PlacedObjects)
            {
                var (gx, gy) = CellUtils.WorldToCellCoordinates(placed.X, placed.Y);

                uint worldspaceFormId;
                if (exactGrid.TryGetValue((gx, gy), out var uniqueCell)
                    && uniqueCell is not null
                    && uniqueCell.PlacedObjects.Count >= ExactGridReassignmentMinPlacements)
                {
                    worldspaceFormId = uniqueCell.WorldspaceFormId!.Value;
                }
                else
                {
                    var matches = 0;
                    worldspaceFormId = 0;
                    foreach (var bounds in worldspaceBounds)
                    {
                        if (gx >= bounds.MinX && gx <= bounds.MaxX && gy >= bounds.MinY && gy <= bounds.MaxY)
                        {
                            worldspaceFormId = bounds.Ws;
                            if (++matches > 1)
                            {
                                break;
                            }
                        }
                    }

                    if (matches != 1)
                    {
                        continue; // Ambiguous or uncontained — never guess.
                    }
                }

                // X/Y put the ref in this worldspace; Z has to agree. An interior tileset's local
                // coordinates satisfy the containment test but sit nowhere near the worldspace's
                // terrain, and fabricating an exterior copy of an interior is worse than leaving the
                // ref unresolved.
                if (!verticalBand.IsPlausibleElevation(worldspaceFormId, placed.Z))
                {
                    rejectedByElevation++;
                    continue;
                }

                var targetIndex = GetOrCreateBoundsInferenceCell(
                    records, cellIndexByFormId, worldspaceFormId, gx, gy, placed.IsBigEndian,
                    ref nextSyntheticFormId, out var created);
                if (created)
                {
                    createdCells++;
                }

                var target = records.Cells[targetIndex];
                if (!target.PlacedObjects.Any(p => p.FormId == placed.FormId))
                {
                    target.PlacedObjects.Add(placed with { AssignmentSource = SourceBoundsInference });
                }

                MoveScanResultRefLink(scanResult, source.FormId, target.FormId, placed.FormId);
                movedFormIds.Add(placed.FormId);
                moved++;
            }

            if (movedFormIds.Count > 0)
            {
                records.Cells[i] = source with
                {
                    PlacedObjects = source.PlacedObjects
                        .Where(placed => !movedFormIds.Contains(placed.FormId))
                        .ToList()
                };
            }
        }

        if (rejectedByElevation > 0)
        {
            Logger.Instance.Debug(
                "  [CellAuthority] Bounds inference rejected {0} placement(s) whose Z lies outside the " +
                "target worldspace's captured elevation band (likely refs of an uncaptured INTERIOR); " +
                "they stay in their unresolved bucket.",
                rejectedByElevation);
        }

        if (moved == 0)
        {
            return (0, 0);
        }

        records.Cells.RemoveAll(c => c.IsUnresolvedBucket && c.PlacedObjects.Count == 0);
        CellRecordHandler.ResolveDoorLinks(records.Cells);
        return (moved, createdCells);
    }

    /// <summary>
    ///     Reuse-or-create for <see cref="ReattachUnresolvedByBoundsInference" />: prefers any
    ///     existing real cell at (worldspace, gx, gy); otherwise synthesizes a REAL exterior
    ///     cell (not <c>IsVirtual</c> — see the pass doc for why).
    /// </summary>
    private static int GetOrCreateBoundsInferenceCell(
        RecordCollection records,
        Dictionary<uint, int> cellIndexByFormId,
        uint worldspaceFormId,
        int gridX,
        int gridY,
        bool isBigEndian,
        ref uint nextSyntheticFormId,
        out bool created)
    {
        for (var i = 0; i < records.Cells.Count; i++)
        {
            var existingCell = records.Cells[i];
            if (!existingCell.IsUnresolvedBucket &&
                existingCell.WorldspaceFormId == worldspaceFormId &&
                existingCell.GridX == gridX &&
                existingCell.GridY == gridY &&
                !existingCell.IsPersistentCell)
            {
                created = false;
                return i;
            }
        }

        while (cellIndexByFormId.ContainsKey(nextSyntheticFormId))
        {
            nextSyntheticFormId++;
        }

        var cellFormId = nextSyntheticFormId++;
        var newCell = new CellRecord
        {
            FormId = cellFormId,
            GridX = gridX,
            GridY = gridY,
            WorldspaceFormId = worldspaceFormId,
            WorldspaceAssignmentSource = SourceBoundsInference,
            EditorId = null,
            PlacedObjects = [],
            IsVirtual = false,
            IsBigEndian = isBigEndian
        };

        records.Cells.Add(newCell);
        var index = records.Cells.Count - 1;
        cellIndexByFormId[cellFormId] = index;
        created = true;
        return index;
    }

    private static (int Moved, int CreatedCells) ReattachUnresolvedReferences(
        RecordCollection records,
        IReadOnlyDictionary<uint, uint>? refToCell,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata,
        IReadOnlyDictionary<uint, uint>? authority,
        EsmRecordScanResult? scanResult)
    {
        if (refToCell is not { Count: > 0 })
        {
            return (0, 0);
        }

        var cellIndexByFormId = BuildCellIndex(records.Cells);
        var moved = 0;
        var createdCells = 0;
        var originalCellCount = records.Cells.Count;

        for (var i = 0; i < originalCellCount; i++)
        {
            var source = records.Cells[i];
            if (!CanReattachAuthorityMappedReferencesFrom(source) || source.PlacedObjects.Count == 0)
            {
                continue;
            }

            var kept = new List<PlacedReference>(source.PlacedObjects.Count);
            var sourceChanged = false;

            foreach (var placed in source.PlacedObjects)
            {
                if (!refToCell.TryGetValue(placed.FormId, out var targetCellFormId) ||
                    targetCellFormId == 0 ||
                    targetCellFormId == source.FormId ||
                    !TryGetOrCreateAuthorityCell(
                        records,
                        cellIndexByFormId,
                        targetCellFormId,
                        cellMetadata,
                        authority,
                        scanResult,
                        out var targetIndex,
                        out var created))
                {
                    kept.Add(placed);
                    continue;
                }

                if (created)
                {
                    createdCells++;
                }

                var target = records.Cells[targetIndex];
                if (!target.PlacedObjects.Any(p => p.FormId == placed.FormId))
                {
                    target.PlacedObjects.Add(placed with { AssignmentSource = "AuthorityRefParent" });
                }

                MoveScanResultRefLink(scanResult, source.FormId, targetCellFormId, placed.FormId);
                moved++;
                sourceChanged = true;
            }

            if (sourceChanged)
            {
                records.Cells[i] = source with { PlacedObjects = kept };
            }
        }

        if (moved == 0)
        {
            return (0, 0);
        }

        records.Cells.RemoveAll(c => c.PlacedObjects.Count == 0 && CanRemoveEmptyAuthorityReferenceSource(c));
        CellRecordHandler.ResolveDoorLinks(records.Cells);
        return (moved, createdCells);
    }

    private static bool CanReattachAuthorityMappedReferencesFrom(CellRecord cell)
    {
        if (cell.IsUnresolvedBucket)
        {
            return true;
        }

        if (!cell.IsVirtual || cell.IsPersistentCell)
        {
            return false;
        }

        return cell.PlacedObjects.Any(placed =>
            placed.AssignmentSource is SourceOffsetCluster or SourceVirtual or SourceAuthorityOffsetCluster or
                SourceAuthorityRefWindow);
    }

    private static bool CanRemoveEmptyAuthorityReferenceSource(CellRecord cell)
    {
        if (cell.IsUnresolvedBucket)
        {
            return true;
        }

        return cell.IsVirtual &&
               !cell.IsPersistentCell &&
               cell.WorldspaceAssignmentSource is SourceOffsetCluster or SourceVirtual or SourceAuthorityOffsetCluster or
                   SourceAuthorityRefWindow;
    }

    private static (int Moved, int CreatedCells, int AppliedWindows, int AmbiguousMatches)
        ReattachWindowedUnresolvedReferences(
            RecordCollection records,
            IReadOnlyList<CellReferenceParentWindow>? refWindows,
            IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata,
            IReadOnlyDictionary<uint, uint>? authority,
            EsmRecordScanResult? scanResult)
    {
        if (refWindows is not { Count: > 0 })
        {
            return (0, 0, 0, 0);
        }

        var cellIndexByFormId = BuildCellIndex(records.Cells);
        var createdCells = 0;
        var targetIndexByCellFormId = new Dictionary<uint, int>();
        var resolvedWindows = new List<ResolvedReferenceWindow>();
        foreach (var window in refWindows)
        {
            if (window.CellFormId == 0 ||
                !window.TryResolveRange(
                    anchorReferenceFormId => TryFindAnchorOffset(records, window.CellFormId, anchorReferenceFormId),
                    out var minOffset,
                    out var maxOffset))
            {
                continue;
            }

            resolvedWindows.Add(new ResolvedReferenceWindow(
                window.CellFormId,
                minOffset,
                maxOffset,
                window.Label));
        }

        if (resolvedWindows.Count == 0)
        {
            return (0, createdCells, 0, 0);
        }

        var moved = 0;
        var ambiguousMatches = 0;
        for (var i = 0; i < records.Cells.Count; i++)
        {
            var source = records.Cells[i];
            if (!CanReattachAuthorityMappedReferencesFrom(source) || source.PlacedObjects.Count == 0)
            {
                continue;
            }

            var kept = new List<PlacedReference>(source.PlacedObjects.Count);
            var sourceChanged = false;
            foreach (var placed in source.PlacedObjects)
            {
                if (placed.Offset <= 0)
                {
                    kept.Add(placed);
                    continue;
                }

                if (!TrySelectWindowForOffset(resolvedWindows, placed.Offset, out var window, out var ambiguous))
                {
                    if (ambiguous)
                    {
                        ambiguousMatches++;
                    }

                    kept.Add(placed);
                    continue;
                }

                if (window.CellFormId == source.FormId)
                {
                    kept.Add(placed);
                    continue;
                }

                if (!targetIndexByCellFormId.TryGetValue(window.CellFormId, out var targetIndex))
                {
                    if (!TryGetOrCreateAuthorityCell(
                            records,
                            cellIndexByFormId,
                            window.CellFormId,
                            cellMetadata,
                            authority,
                            scanResult,
                            out targetIndex,
                            out var created))
                    {
                        kept.Add(placed);
                        continue;
                    }

                    if (created)
                    {
                        createdCells++;
                    }

                    targetIndexByCellFormId[window.CellFormId] = targetIndex;
                }

                var target = records.Cells[targetIndex];
                if (!target.PlacedObjects.Any(p => p.FormId == placed.FormId))
                {
                    target.PlacedObjects.Add(placed with { AssignmentSource = SourceAuthorityRefWindow });
                }

                MoveScanResultRefLink(scanResult, source.FormId, window.CellFormId, placed.FormId);
                moved++;
                sourceChanged = true;
            }

            if (sourceChanged)
            {
                records.Cells[i] = source with { PlacedObjects = kept };
            }
        }

        if (moved == 0)
        {
            return (0, createdCells, resolvedWindows.Count, ambiguousMatches);
        }

        records.Cells.RemoveAll(c => c.PlacedObjects.Count == 0 && CanRemoveEmptyAuthorityReferenceSource(c));
        CellRecordHandler.ResolveDoorLinks(records.Cells);
        return (moved, createdCells, resolvedWindows.Count, ambiguousMatches);
    }

    private static long? TryFindAnchorOffset(
        RecordCollection records,
        uint targetCellFormId,
        uint anchorReferenceFormId)
    {
        var targetOffsets = records.Cells
            .Where(cell => cell.FormId == targetCellFormId)
            .SelectMany(cell => cell.PlacedObjects)
            .Where(placed => placed.FormId == anchorReferenceFormId && placed.Offset > 0)
            .Select(placed => placed.Offset)
            .Distinct()
            .ToList();
        if (targetOffsets.Count == 1)
        {
            return targetOffsets[0];
        }

        var offsets = records.Cells
            .SelectMany(cell => cell.PlacedObjects)
            .Where(placed => placed.FormId == anchorReferenceFormId && placed.Offset > 0)
            .Select(placed => placed.Offset)
            .Distinct()
            .ToList();
        return offsets.Count == 1 ? offsets[0] : null;
    }

    private static bool TrySelectWindowForOffset(
        IReadOnlyList<ResolvedReferenceWindow> windows,
        long offset,
        out ResolvedReferenceWindow window,
        out bool ambiguous)
    {
        window = default;
        ambiguous = false;
        var matched = windows
            .Where(candidate => offset >= candidate.MinOffset && offset <= candidate.MaxOffset)
            .GroupBy(candidate => candidate.CellFormId)
            .ToList();
        if (matched.Count == 0)
        {
            return false;
        }

        if (matched.Count > 1)
        {
            ambiguous = true;
            return false;
        }

        window = matched[0].First();
        return true;
    }

    private static (int Moved, int CreatedCells) ReattachOffsetClusteredUnresolvedReferences(
        RecordCollection records,
        EsmRecordScanResult? scanResult)
    {
        var offsetAnchors = records.Cells
            .Where(cell => !cell.IsInterior &&
                           !cell.IsVirtual &&
                           !cell.IsUnresolvedBucket &&
                           cell.WorldspaceFormId is > 0 &&
                           cell.GridX.HasValue &&
                           cell.GridY.HasValue &&
                           cell.Offset > 0)
            .OrderBy(cell => cell.Offset)
            .ToList();

        if (offsetAnchors.Count == 0)
        {
            return (0, 0);
        }

        var cellIndexByFormId = BuildCellIndex(records.Cells);
        var moved = 0;
        var createdCells = 0;
        var nextVirtualFormId = NextAvailableSyntheticCellFormId(records, 0xFE900001u);

        for (var i = 0; i < records.Cells.Count; i++)
        {
            var source = records.Cells[i];
            if (!source.IsUnresolvedBucket || source.PlacedObjects.Count == 0)
            {
                continue;
            }

            var movedFormIds = new HashSet<uint>();
            foreach (var cluster in BuildPlacedOffsetClusters(source.PlacedObjects))
            {
                if (!TryInferAuthorityOffsetClusterWorldspace(cluster, offsetAnchors, out var worldspaceFormId))
                {
                    continue;
                }

                foreach (var placed in cluster)
                {
                    var (gx, gy) = CellUtils.WorldToCellCoordinates(placed.X, placed.Y);
                    var targetIndex = GetOrCreateAuthorityOffsetVirtualCell(
                        records,
                        cellIndexByFormId,
                        worldspaceFormId,
                        gx,
                        gy,
                        placed.IsBigEndian,
                        ref nextVirtualFormId,
                        out var created);
                    if (created)
                    {
                        createdCells++;
                    }

                    var target = records.Cells[targetIndex];
                    if (!target.PlacedObjects.Any(p => p.FormId == placed.FormId))
                    {
                        target.PlacedObjects.Add(placed with { AssignmentSource = SourceAuthorityOffsetCluster });
                    }

                    MoveScanResultRefLink(scanResult, source.FormId, target.FormId, placed.FormId);
                    movedFormIds.Add(placed.FormId);
                    moved++;
                }
            }

            if (movedFormIds.Count > 0)
            {
                records.Cells[i] = source with
                {
                    PlacedObjects = source.PlacedObjects
                        .Where(placed => !movedFormIds.Contains(placed.FormId))
                        .ToList()
                };
            }
        }

        if (moved == 0)
        {
            return (0, 0);
        }

        records.Cells.RemoveAll(c => c.IsUnresolvedBucket && c.PlacedObjects.Count == 0);
        CellRecordHandler.ResolveDoorLinks(records.Cells);
        return (moved, createdCells);
    }

    /// <summary>
    ///     Interior counterpart to <see cref="ReattachOffsetClusteredUnresolvedReferences" />. When a
    ///     partial DMP loses the parent-cell link on an interior cell's refs, they report no interior
    ///     parent, get misclassified as exterior orphans during parse, and land in the global
    ///     <c>[Unresolved …]</c> bucket — they can't be grid-resolved (interiors have no grid) and the
    ///     exterior offset-cluster passes only anchor to worldspace cells. By this point the authority
    ///     ref→cell / window passes have already seeded each captured interior cell with the refs they
    ///     could match by FormID; since an interior's refs are streamed contiguously in memory, any
    ///     remaining unresolved orphan whose dump offset is bracketed by exactly one captured interior
    ///     cell's refs almost certainly belongs to that interior. Attach those by offset adjacency.
    /// </summary>
    private static (int Moved, int CreatedCells) ReattachOffsetClusteredInteriorUnresolvedReferences(
        RecordCollection records,
        EsmRecordScanResult? scanResult)
    {
        var anchors = records.Cells
            .Where(cell => cell.IsInterior &&
                           !cell.IsVirtual &&
                           !cell.IsUnresolvedBucket)
            .SelectMany(cell => cell.PlacedObjects
                .Where(placed => placed.Offset > 0)
                .Select(placed => (placed.Offset, Cell: cell)))
            .OrderBy(anchor => anchor.Offset)
            .ToList();

        if (anchors.Count == 0)
        {
            return (0, 0);
        }

        var anchorOffsets = anchors.Select(anchor => anchor.Offset).ToArray();
        var cellIndexByFormId = BuildCellIndex(records.Cells);
        var moved = 0;

        for (var i = 0; i < records.Cells.Count; i++)
        {
            var source = records.Cells[i];
            if (!source.IsUnresolvedBucket || source.PlacedObjects.Count == 0)
            {
                continue;
            }

            var movedFormIds = new HashSet<uint>();
            foreach (var placed in source.PlacedObjects)
            {
                if (placed.Offset <= 0)
                {
                    continue;
                }

                var owner = FindNearestInteriorOwner(anchors, anchorOffsets, placed.Offset);
                if (owner == null || !cellIndexByFormId.TryGetValue(owner.FormId, out var targetIndex))
                {
                    continue;
                }

                var target = records.Cells[targetIndex];
                if (!target.PlacedObjects.Any(p => p.FormId == placed.FormId))
                {
                    target.PlacedObjects.Add(placed with { AssignmentSource = SourceInteriorOffsetCluster });
                }

                MoveScanResultRefLink(scanResult, source.FormId, owner.FormId, placed.FormId);
                movedFormIds.Add(placed.FormId);
                moved++;
            }

            if (movedFormIds.Count > 0)
            {
                records.Cells[i] = source with
                {
                    PlacedObjects = source.PlacedObjects
                        .Where(placed => !movedFormIds.Contains(placed.FormId))
                        .ToList()
                };
            }
        }

        if (moved == 0)
        {
            return (0, 0);
        }

        records.Cells.RemoveAll(c => c.IsUnresolvedBucket && c.PlacedObjects.Count == 0);
        CellRecordHandler.ResolveDoorLinks(records.Cells);
        return (moved, 0);
    }

    /// <summary>
    ///     Returns the captured interior cell whose ref is nearest in dump offset to
    ///     <paramref name="offset" />, or null when the nearest interior ref is further than the
    ///     adjacency cap. An interior's refs are streamed contiguously, so the nearest captured
    ///     interior ref is the orphan's most likely owner; the cap rejects orphans that sit far
    ///     from every captured interior ref (the cell shell was captured but its refs were not, so
    ///     adjacency is not real evidence).
    /// </summary>
    private static CellRecord? FindNearestInteriorOwner(
        List<(long Offset, CellRecord Cell)> anchors,
        long[] anchorOffsets,
        long offset)
    {
        var idx = Array.BinarySearch(anchorOffsets, offset);
        if (idx >= 0)
        {
            return anchors[idx].Cell;
        }

        idx = ~idx; // first anchor with offset greater than the orphan
        CellRecord? best = null;
        var bestGap = long.MaxValue;
        if (idx < anchors.Count)
        {
            bestGap = anchorOffsets[idx] - offset;
            best = anchors[idx].Cell;
        }
        if (idx - 1 >= 0)
        {
            var gapBelow = offset - anchorOffsets[idx - 1];
            if (gapBelow < bestGap)
            {
                bestGap = gapBelow;
                best = anchors[idx - 1].Cell;
            }
        }

        return best is not null && bestGap <= AuthorityOffsetClusterWindowBytes ? best : null;
    }

    private static IEnumerable<List<PlacedReference>> BuildPlacedOffsetClusters(
        IReadOnlyList<PlacedReference> placedReferences)
    {
        List<PlacedReference>? cluster = null;
        long lastOffset = 0;
        foreach (var placed in placedReferences
                     .Where(placed => placed.Offset > 0)
                     .OrderBy(placed => placed.Offset))
        {
            if (cluster == null || placed.Offset - lastOffset > AuthorityOffsetClusterGapBytes)
            {
                if (cluster is { Count: > 0 })
                {
                    yield return cluster;
                }

                cluster = [];
            }

            cluster.Add(placed);
            lastOffset = placed.Offset;
        }

        if (cluster is { Count: > 0 })
        {
            yield return cluster;
        }
    }

    private static bool TryInferAuthorityOffsetClusterWorldspace(
        List<PlacedReference> cluster,
        List<CellRecord> offsetAnchors,
        out uint worldspaceFormId)
    {
        worldspaceFormId = 0;
        if (cluster.Count < AuthorityOffsetClusterMinPlacements)
        {
            return false;
        }

        var minOffset = cluster.Min(placed => placed.Offset);
        var maxOffset = cluster.Max(placed => placed.Offset);
        var nearbyAnchors = offsetAnchors
            .Select(cell => new
            {
                Cell = cell,
                Distance = DistanceToOffsetRange(cell.Offset, minOffset, maxOffset)
            })
            .Where(anchor => anchor.Distance <= AuthorityOffsetClusterWindowBytes)
            .ToList();

        if (nearbyAnchors.Count == 0)
        {
            return false;
        }

        var minDistance = nearbyAnchors.Min(anchor => anchor.Distance);
        var selectedAnchors = nearbyAnchors
            .Where(anchor => anchor.Distance <= minDistance + AuthorityOffsetClusterNearestBandBytes)
            .Select(anchor => anchor.Cell)
            .ToList();

        var worldspaces = selectedAnchors
            .Select(cell => cell.WorldspaceFormId!.Value)
            .Distinct()
            .ToList();
        if (worldspaces.Count != 1)
        {
            return false;
        }

        var anchorMinX = selectedAnchors.Min(cell => cell.GridX!.Value) - AuthorityOffsetClusterGridExpansion;
        var anchorMaxX = selectedAnchors.Max(cell => cell.GridX!.Value) + AuthorityOffsetClusterGridExpansion;
        var anchorMinY = selectedAnchors.Min(cell => cell.GridY!.Value) - AuthorityOffsetClusterGridExpansion;
        var anchorMaxY = selectedAnchors.Max(cell => cell.GridY!.Value) + AuthorityOffsetClusterGridExpansion;

        foreach (var placed in cluster)
        {
            var (gx, gy) = CellUtils.WorldToCellCoordinates(placed.X, placed.Y);
            if (gx < anchorMinX || gx > anchorMaxX || gy < anchorMinY || gy > anchorMaxY)
            {
                return false;
            }
        }

        worldspaceFormId = worldspaces[0];
        return true;
    }

    private static long DistanceToOffsetRange(long offset, long minOffset, long maxOffset)
    {
        if (offset < minOffset)
        {
            return minOffset - offset;
        }

        return offset > maxOffset ? offset - maxOffset : 0;
    }

    private static int GetOrCreateAuthorityOffsetVirtualCell(
        RecordCollection records,
        Dictionary<uint, int> cellIndexByFormId,
        uint worldspaceFormId,
        int gridX,
        int gridY,
        bool isBigEndian,
        ref uint nextVirtualFormId,
        out bool created)
    {
        for (var i = 0; i < records.Cells.Count; i++)
        {
            var existingCell = records.Cells[i];
            if (!existingCell.IsUnresolvedBucket &&
                existingCell.WorldspaceFormId == worldspaceFormId &&
                existingCell.GridX == gridX &&
                existingCell.GridY == gridY &&
                !existingCell.IsPersistentCell)
            {
                created = false;
                return i;
            }
        }

        while (cellIndexByFormId.ContainsKey(nextVirtualFormId))
        {
            nextVirtualFormId++;
        }

        var cellFormId = nextVirtualFormId++;
        var newCell = new CellRecord
        {
            FormId = cellFormId,
            GridX = gridX,
            GridY = gridY,
            WorldspaceFormId = worldspaceFormId,
            WorldspaceAssignmentSource = SourceAuthorityOffsetCluster,
            EditorId = $"[Virtual {gridX},{gridY}]",
            PlacedObjects = [],
            IsVirtual = true,
            IsBigEndian = isBigEndian
        };

        records.Cells.Add(newCell);
        var index = records.Cells.Count - 1;
        cellIndexByFormId[cellFormId] = index;
        created = true;
        return index;
    }

    private static uint NextAvailableSyntheticCellFormId(RecordCollection records, uint start)
    {
        var used = records.Cells.Select(cell => cell.FormId).ToHashSet();
        while (used.Contains(start))
        {
            start++;
        }

        return start;
    }

    private static Dictionary<uint, int> BuildCellIndex(List<CellRecord> cells)
    {
        var index = new Dictionary<uint, int>();
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.FormId == 0)
            {
                continue;
            }

            if (!index.TryGetValue(cell.FormId, out var existingIndex) ||
                cells[existingIndex].IsUnresolvedBucket)
            {
                index[cell.FormId] = i;
            }
        }

        return index;
    }

    private static bool TryGetOrCreateAuthorityCell(
        RecordCollection records,
        Dictionary<uint, int> cellIndexByFormId,
        uint cellFormId,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata,
        IReadOnlyDictionary<uint, uint>? authority,
        EsmRecordScanResult? scanResult,
        out int index,
        out bool created)
    {
        if (cellIndexByFormId.TryGetValue(cellFormId, out index))
        {
            created = false;
            return true;
        }

        CellAuthorityMetadata? metadata = null;
        cellMetadata?.TryGetValue(cellFormId, out metadata);
        uint? worldspaceFormId = metadata?.WorldspaceFormId;
        if ((worldspaceFormId is null or 0u) &&
            authority is not null &&
            authority.TryGetValue(cellFormId, out var authorityWorldspaceFormId))
        {
            worldspaceFormId = authorityWorldspaceFormId;
        }

        if (metadata is null && worldspaceFormId is not > 0)
        {
            created = false;
            return false;
        }

        var isInterior = metadata?.IsInterior ?? (worldspaceFormId is not > 0u);
        var cell = new CellRecord
        {
            FormId = cellFormId,
            Flags = (byte)(isInterior ? 0x01 : 0),
            GridX = isInterior ? null : metadata?.GridX,
            GridY = isInterior ? null : metadata?.GridY,
            WorldspaceFormId = isInterior ? null : worldspaceFormId,
            WorldspaceAssignmentSource = !isInterior && worldspaceFormId is > 0 ? "AuthorityRefParent" : null,
            EditorId = metadata?.EditorId,
            FullName = metadata?.FullName
        };

        records.Cells.Add(cell);
        index = records.Cells.Count - 1;
        cellIndexByFormId[cellFormId] = index;
        created = true;

        if (scanResult is not null && !isInterior && worldspaceFormId is > 0)
        {
            scanResult.CellToWorldspaceMap[cellFormId] = worldspaceFormId.Value;
        }

        return true;
    }

    private static void MoveScanResultRefLink(
        EsmRecordScanResult? scanResult,
        uint sourceCellFormId,
        uint targetCellFormId,
        uint referenceFormId)
    {
        if (scanResult is null || referenceFormId == 0)
        {
            return;
        }

        if (scanResult.CellToRefrMap.TryGetValue(sourceCellFormId, out var sourceRefs))
        {
            sourceRefs.Remove(referenceFormId);
        }

        if (!scanResult.CellToRefrMap.TryGetValue(targetCellFormId, out var targetRefs))
        {
            targetRefs = [];
            scanResult.CellToRefrMap[targetCellFormId] = targetRefs;
        }

        if (!targetRefs.Contains(referenceFormId))
        {
            targetRefs.Add(referenceFormId);
        }
    }

    private static int CountCellsWithTerrain(RecordCollection records)
    {
        return records.Cells.Count(c =>
            c.Heightmap is not null ||
            c.LandVisualData?.HasAny == true ||
            c.RuntimeTerrainMesh is not null);
    }

    private static int RebuildWorldspaceCellLists(
        RecordCollection records,
        IReadOnlyDictionary<uint, string>? worldspaceNames)
    {
        foreach (var (worldspaceFormId, name) in worldspaceNames ?? new Dictionary<uint, string>())
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                records.FormIdToEditorId.TryAdd(worldspaceFormId, name);
            }
        }

        var cellsByWorldspace = records.Cells
            .Where(c => !c.IsInterior && c.WorldspaceFormId is > 0)
            .GroupBy(c => c.WorldspaceFormId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var worldspaceIds = new HashSet<uint>(records.Worldspaces.Select(w => w.FormId));
        var synthesized = 0;
        foreach (var worldspaceFormId in cellsByWorldspace.Keys.OrderBy(id => id))
        {
            if (worldspaceIds.Contains(worldspaceFormId))
            {
                continue;
            }

            var name = worldspaceNames is not null &&
                       worldspaceNames.TryGetValue(worldspaceFormId, out var resolvedName)
                ? resolvedName
                : null;
            records.Worldspaces.Add(new WorldspaceRecord
            {
                FormId = worldspaceFormId,
                EditorId = string.IsNullOrWhiteSpace(name) ? null : name,
                Cells = []
            });
            worldspaceIds.Add(worldspaceFormId);
            synthesized++;
        }

        for (var i = 0; i < records.Worldspaces.Count; i++)
        {
            var ws = records.Worldspaces[i];
            cellsByWorldspace.TryGetValue(ws.FormId, out var cells);
            records.Worldspaces[i] = ws with { Cells = cells ?? [] };
        }

        return synthesized;
    }
}
