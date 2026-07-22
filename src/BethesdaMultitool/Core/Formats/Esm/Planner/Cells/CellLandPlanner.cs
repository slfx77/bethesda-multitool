using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Subrecords;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;
using BethesdaMultitool.Core.Formats.Esm.Terrain;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

public enum CellLandHeightSource
{
    CapturedHeightmap,
    CompleteRuntimeMesh,
}

/// <summary>The immutable terrain payload selected for one exterior cell's LAND emission.</summary>
public sealed record CellLandDecision
{
    public required uint CellSourceFormId { get; init; }
    public required LandHeightmap Heightmap { get; init; }
    public LandVisualData? VisualData { get; init; }
    public required CellLandHeightSource HeightSource { get; init; }

    /// <summary>
    ///     Master LAND FormID this decision overrides (master-anchored cells whose master
    ///     already owns terrain). Null emits a NEW LAND with an allocated FormID.
    /// </summary>
    public uint? MasterLandFormId { get; init; }
}

/// <summary>
///     Selects safe LAND sources before any LAND FormID is allocated. DMP-new exterior
///     cells emit NEW LAND; master-anchored (override) exterior cells emit an OVERRIDE of
///     master's LAND when the capture holds a complete heightmap — legacy
///     <see cref="LandOverrideBuilder" /> parity, including flat-rejection against the
///     master baseline (an effectively-flat capture never replaces sculpted retail terrain).
/// </summary>
public static class CellLandPlanner
{
    private const int LandVertexCount = 33 * 33;

    public static CellLandPlanningResult DecideAll(
        IReadOnlyList<CellCatalogEntry> entries,
        IReadOnlyDictionary<uint, ParsedMainRecord>? masterRecordsByFormId = null,
        IReadOnlyDictionary<uint, List<uint>>? masterLandsByCell = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var decisions = ImmutableDictionary.CreateBuilder<uint, CellLandDecision>();
        var diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();
        var masterLandCache = new Dictionary<uint, LandSubrecordParseResult?>();

        foreach (var entry in entries)
        {
            var isOverride = entry.Source == SourceKind.DmpOverride;
            if (entry.Source is not (SourceKind.DmpNew or SourceKind.DmpOverride)
                || entry.DmpModel is not { } cell
                || cell.IsPersistentCell || cell.IsVirtual || cell.IsUnresolvedBucket)
            {
                continue;
            }

            // Override captures may lack grouping data the master context carries; DMP-new
            // cells have only the capture to consult.
            var isInterior = isOverride
                ? entry.MasterContext?.IsInterior ?? cell.IsInterior
                : cell.IsInterior;
            var hasWorldspace = isOverride
                ? entry.MasterContext?.WorldspaceFormId is not null || cell.WorldspaceFormId.HasValue
                : cell.WorldspaceFormId.HasValue;
            if (isInterior || !hasWorldspace
                || (isOverride && entry.MasterContext?.IsPersistentCellContainer == true))
            {
                continue;
            }

            var cellKind = isOverride ? "master-override" : "DMP-new";
            LandHeightmap? heightmap = null;
            var source = CellLandHeightSource.CapturedHeightmap;
            var fallbackHeightmap = cell.Heightmap is { ExactHeights: null } && IsUsable(cell.Heightmap)
                ? cell.Heightmap
                : null;
            var capturedHeightmap = IsUsable(cell.CapturedLandHeightmap)
                ? cell.CapturedLandHeightmap
                : fallbackHeightmap;
            if (capturedHeightmap is not null)
            {
                if (capturedHeightmap.SourceParentCellFormId == cell.FormId)
                {
                    heightmap = capturedHeightmap;
                }
                else
                {
                    diagnostics.Add(new PlanDiagnostic
                    {
                        Kind = PlanDiagnosticKind.Warning,
                        Phase = "CellLand",
                        Code = "land.captured-heightmap-parent-mismatch",
                        RecordType = "CELL",
                        FormId = cell.FormId,
                        Message = $"Skipped captured LAND for {cellKind} CELL 0x{cell.FormId:X8}: source parent was "
                                  + FormatParent(capturedHeightmap.SourceParentCellFormId) + ".",
                    });
                }
            }

            if (heightmap is null && cell.RuntimeTerrainMesh is { } runtimeMesh)
            {
                var quality = runtimeMesh.DiagnoseQuality(
                    cell.GridX ?? 0, cell.GridY ?? 0, cell.FormId);
                // DiagnoseQuality's Flat/FewPixels labels describe authored height
                // variation, not capture completeness. Require complete source occupancy,
                // allowing only the single unmasked (0,0,0) ambiguity that the runtime
                // mesh reader intentionally omits.
                var hasExactParent = runtimeMesh.SourceParentCellFormId == cell.FormId;
                var hasRuntimeBaseHeight = runtimeMesh.RuntimeBaseHeight.HasValue;
                var hasCompleteRuntimeSource = HasCompleteRuntimeSource(runtimeMesh, quality);
                if (hasExactParent && hasRuntimeBaseHeight && hasCompleteRuntimeSource)
                {
                    try
                    {
                        // Runtime LAND enrichment has already applied LoadedLandData.BaseHeight
                        // to cell.Heightmap. Rebuilding directly from the mesh here would drop
                        // that per-cell absolute offset because runtime mesh Z values are local.
                        // Keep the completeness gate above, then reuse the exact enriched grid
                        // when it belongs to this CELL. If the enrichment projection is ever
                        // absent, reconstruct only with the base-height provenance carried by
                        // the mesh itself; an unavailable base fails closed below.
                        heightmap = cell.Heightmap is
                            {
                                ExactHeights: not null,
                                SourceParentCellFormId: var heightmapParent,
                            } enrichedRuntimeHeightmap
                            && heightmapParent == cell.FormId
                                ? enrichedRuntimeHeightmap
                                : runtimeMesh.ToLandHeightmap(runtimeMesh.RuntimeBaseHeight!.Value);
                        source = CellLandHeightSource.CompleteRuntimeMesh;
                    }
                    catch (InvalidOperationException)
                    {
                        // The quality probe and encoder share reconstruction inputs, but
                        // keep this defensive gate so an unusual mesh never allocates LAND.
                    }
                }

                if (heightmap is null)
                {
                    var parentMismatch = !hasExactParent;
                    var baseHeightMissing = hasExactParent && hasCompleteRuntimeSource && !hasRuntimeBaseHeight;
                    string skipCode;
                    string skipMessage;
                    if (parentMismatch)
                    {
                        skipCode = "land.runtime-mesh-parent-mismatch";
                        skipMessage = $"Skipped runtime LAND for {cellKind} CELL 0x{cell.FormId:X8}: source parent was "
                            + FormatParent(runtimeMesh.SourceParentCellFormId) + ".";
                    }
                    else if (baseHeightMissing)
                    {
                        skipCode = "land.runtime-base-height-missing";
                        skipMessage = $"Skipped runtime LAND for {cellKind} CELL 0x{cell.FormId:X8}: "
                            + "LoadedLandData base-height provenance was unavailable.";
                    }
                    else
                    {
                        skipCode = "land.runtime-mesh-not-complete";
                        skipMessage = $"Skipped LAND for {cellKind} CELL 0x{cell.FormId:X8}: runtime terrain source coverage was "
                            + $"{quality.SourceSampleCount} accepted samples "
                            + $"({quality.SourceCoveragePercent:F1}%, {quality.Classification}).";
                    }

                    diagnostics.Add(new PlanDiagnostic
                    {
                        Kind = PlanDiagnosticKind.Warning,
                        Phase = "CellLand",
                        Code = skipCode,
                        RecordType = "CELL",
                        FormId = cell.FormId,
                        Message = skipMessage,
                    });
                }
            }

            if (heightmap is null)
            {
                continue;
            }

            // Master baseline (override cells only): the override target FormID, the
            // flat-rejection reference, and the visual-data fallback the legacy enricher
            // used to pre-merge (the planner sees un-enriched captures).
            uint? masterLandFormId = null;
            LandHeightmap? masterHeightmap = null;
            LandVisualData? masterVisualData = null;
            if (isOverride && masterLandsByCell is not null && masterRecordsByFormId is not null
                && masterLandsByCell.TryGetValue(entry.CellFormId, out var masterLandIds)
                && masterLandIds.Count > 0)
            {
                masterLandFormId = masterLandIds[0];
                var parsed = ParseMasterLand(masterLandFormId.Value, masterRecordsByFormId, masterLandCache);
                masterHeightmap = parsed?.Heightmap;
                masterVisualData = parsed?.VisualData;
            }

            if (isOverride && LandOverrideBuilder.ShouldRejectFlatOverride(heightmap, masterHeightmap))
            {
                diagnostics.Add(new PlanDiagnostic
                {
                    Kind = PlanDiagnosticKind.Decision,
                    Phase = "CellLand",
                    Code = "land.flat-rejected",
                    RecordType = "CELL",
                    FormId = cell.FormId,
                    Message = $"Rejected effectively-flat captured LAND for master CELL 0x{entry.CellFormId:X8}; "
                              + "master terrain retained.",
                });
                continue; // Writer keeps master LAND verbatim via the carry-forward fallback.
            }

            byte[]? runtimeVertexColors = null;
            if (cell.RuntimeTerrainMesh is { SourceParentCellFormId: var meshParent } mesh
                && meshParent == cell.FormId)
            {
                runtimeVertexColors = mesh.ToLandVertexColorBytes();
            }

            var visualData = cell.LandVisualData;
            if (visualData is not null && visualData.SourceParentCellFormId != cell.FormId)
            {
                diagnostics.Add(new PlanDiagnostic
                {
                    Kind = PlanDiagnosticKind.Warning,
                    Phase = "CellLand",
                    Code = "land.visual-data-parent-mismatch",
                    RecordType = "CELL",
                    FormId = cell.FormId,
                    Message = $"Dropped LAND visual data for {cellKind} CELL 0x{cell.FormId:X8}: source parent was "
                              + FormatParent(visualData.SourceParentCellFormId) + ".",
                });
                visualData = null;
            }

            decisions[entry.CellFormId] = new CellLandDecision
            {
                CellSourceFormId = cell.FormId,
                Heightmap = heightmap,
                VisualData = LandVisualData.MergeForEmission(
                    visualData, runtimeVertexColors, fallback: masterVisualData),
                HeightSource = source,
                MasterLandFormId = masterLandFormId,
            };
        }

        return new CellLandPlanningResult
        {
            DecisionsByCellSourceFormId = decisions.ToImmutable(),
            Diagnostics = diagnostics.ToImmutable(),
        };
    }

    private static bool IsUsable(LandHeightmap? heightmap) =>
        heightmap?.HeightDeltas is { Length: LandVertexCount };

    private static bool HasCompleteRuntimeSource(RuntimeTerrainMesh mesh, TerrainMeshDiagnostic quality)
    {
        if (!string.Equals(quality.HeightSource, "RuntimeMESH", StringComparison.Ordinal)
            || HasDeclaredSourceHole(mesh))
        {
            return false;
        }

        if (quality.SourceCoveragePercent >= 100.0f)
        {
            return mesh.SanitizedZCount == 0
                   && mesh.SanitizedMask?.Any(static value => value) != true;
        }

        // Unmasked captures cannot distinguish a real local (0,0,0) vertex from an
        // unfilled destination slot. Flat meshes can also sanitize exactly that
        // vertex. Permit only the one corresponding canonical hole, regardless of
        // whether local origin is a corner or the center of the source buffer.
        if (quality.DetectedGridSize != RuntimeTerrainMesh.GridSize
            || quality.SourceSampleCount != RuntimeTerrainMesh.VertexCount - 1)
        {
            return false;
        }

        var reconstruction = mesh.TryReconstructHeightGrid();
        if (reconstruction?.SourceCoverageMask is not { } coverageMask
            || !TryGetSingleCoverageHole(coverageMask, out var holeX, out var holeY)
            || !TryGetOriginAmbiguityIndex(mesh, out var originIndex))
        {
            return false;
        }

        var vertexOffset = originIndex * 3;
        var originX = mesh.Vertices[vertexOffset];
        var originY = mesh.Vertices[vertexOffset + 1];
        if (MathF.Abs(originX) >= 0.001f || MathF.Abs(originY) >= 0.001f)
        {
            return false;
        }

        var mappedOrigin = reconstruction.UsesCanonicalLocalFrame
            ? TerrainCoordinateMapper.TryMapLocalVertexToCanonicalCell(originX, originY)
            : TryMapWorldFrameOrigin(reconstruction, originX, originY);
        return mappedOrigin is { } mapped && mapped.X == holeX && mapped.Y == holeY;
    }

    private static bool HasDeclaredSourceHole(RuntimeTerrainMesh mesh)
    {
        var sourceMask = mesh.SourceVertexMask;
        if (sourceMask is null)
        {
            return false;
        }

        return sourceMask.Length < RuntimeTerrainMesh.VertexCount
               || sourceMask.Take(RuntimeTerrainMesh.VertexCount).Any(static present => !present);
    }

    private static bool TryGetSingleCoverageHole(bool[,] coverageMask, out int holeX, out int holeY)
    {
        holeX = -1;
        holeY = -1;
        for (var y = 0; y < RuntimeTerrainMesh.GridSize; y++)
        {
            for (var x = 0; x < RuntimeTerrainMesh.GridSize; x++)
            {
                if (coverageMask[y, x])
                {
                    continue;
                }

                if (holeX >= 0)
                {
                    return false;
                }

                holeX = x;
                holeY = y;
            }
        }

        return holeX >= 0;
    }

    private static bool TryGetOriginAmbiguityIndex(RuntimeTerrainMesh mesh, out int originIndex)
    {
        originIndex = -1;
        if (mesh.SourceVertexMask is not null)
        {
            return false;
        }

        var sanitizedMask = mesh.SanitizedMask;
        if (sanitizedMask is not null || mesh.SanitizedZCount != 0)
        {
            if (mesh.SanitizedZCount != 1
                || sanitizedMask is not { Length: >= RuntimeTerrainMesh.VertexCount })
            {
                return false;
            }

            for (var i = 0; i < RuntimeTerrainMesh.VertexCount; i++)
            {
                if (!sanitizedMask[i])
                {
                    continue;
                }

                if (originIndex >= 0)
                {
                    return false;
                }

                originIndex = i;
            }

            return originIndex >= 0
                   && mesh.SanitizedZeroOriginIndex == originIndex
                   && originIndex * 3 + 2 < mesh.Vertices.Length;
        }

        // With no authoritative source mask, an exact zero triple is omitted by
        // CollectValidSamples because it is indistinguishable from an empty slot.
        var vertexCount = Math.Min(RuntimeTerrainMesh.VertexCount, mesh.Vertices.Length / 3);
        for (var i = 0; i < vertexCount; i++)
        {
            var offset = i * 3;
            if (MathF.Abs(mesh.Vertices[offset]) >= 0.001f
                || MathF.Abs(mesh.Vertices[offset + 1]) >= 0.001f
                || MathF.Abs(mesh.Vertices[offset + 2]) >= 0.001f)
            {
                continue;
            }

            if (originIndex >= 0)
            {
                return false;
            }

            originIndex = i;
        }

        return originIndex >= 0;
    }

    private static TerrainGridMapping? TryMapWorldFrameOrigin(
        RuntimeTerrainMesh.TerrainGridReconstruction reconstruction,
        float x,
        float y)
    {
        var denominator = reconstruction.SourceGridSize - 1;
        if (denominator <= 0)
        {
            return null;
        }

        var spacingX = (reconstruction.MaxX - reconstruction.MinX) / denominator;
        var spacingY = (reconstruction.MaxY - reconstruction.MinY) / denominator;
        if (spacingX <= 0f || spacingY <= 0f)
        {
            return null;
        }

        var gridX = (int)MathF.Round((x - reconstruction.MinX) / spacingX);
        var gridY = (int)MathF.Round((y - reconstruction.MinY) / spacingY);
        if (gridX < 0 || gridX >= reconstruction.SourceGridSize
            || gridY < 0 || gridY >= reconstruction.SourceGridSize)
        {
            return null;
        }

        var expectedX = reconstruction.MinX + gridX * spacingX;
        var expectedY = reconstruction.MinY + gridY * spacingY;
        var deltaX = x - expectedX;
        var deltaY = y - expectedY;
        var fitError = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        return fitError <= TerrainConstants.CanonicalVertexFitTolerance
            ? new TerrainGridMapping(gridX, gridY, fitError)
            : null;
    }

    /// <summary>
    ///     Parse a master LAND record's heightmap + visual data (PC little-endian), cached
    ///     per FormID — a worldspace's cells frequently share probe order with their
    ///     neighbors and re-parsing the same LAND per cell is pure waste.
    /// </summary>
    private static LandSubrecordParseResult? ParseMasterLand(
        uint masterLandFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        Dictionary<uint, LandSubrecordParseResult?> cache)
    {
        if (cache.TryGetValue(masterLandFormId, out var cached))
        {
            return cached;
        }

        LandSubrecordParseResult? result = null;
        if (masterRecordsByFormId.TryGetValue(masterLandFormId, out var record)
            && record.Header.Signature == "LAND")
        {
            var recordBytes = CellGrupBuilder.ReconstructRecordBytes(record);
            if (recordBytes.Length > 24)
            {
                var dataSize = recordBytes.Length - 24;
                var data = new byte[dataSize];
                Buffer.BlockCopy(recordBytes, 24, data, 0, dataSize);
                result = LandSubrecordParser.Parse(data, dataSize, isBigEndian: false);
            }
        }

        cache[masterLandFormId] = result;
        return result;
    }

    private static string FormatParent(uint? formId) =>
        formId.HasValue ? $"0x{formId.Value:X8}" : "unknown";
}

public sealed record CellLandPlanningResult
{
    public required ImmutableDictionary<uint, CellLandDecision> DecisionsByCellSourceFormId { get; init; }
    public required ImmutableArray<PlanDiagnostic> Diagnostics { get; init; }
}
