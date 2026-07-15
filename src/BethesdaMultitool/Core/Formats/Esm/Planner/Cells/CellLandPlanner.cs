using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

public enum CellLandHeightSource
{
    CapturedHeightmap,
    CompleteRuntimeMesh,
}

/// <summary>The immutable terrain payload selected for one DMP-new exterior cell.</summary>
public sealed record CellLandDecision
{
    public required uint CellSourceFormId { get; init; }
    public required LandHeightmap Heightmap { get; init; }
    public LandVisualData? VisualData { get; init; }
    public required CellLandHeightSource HeightSource { get; init; }
}

/// <summary>Selects safe LAND sources before any LAND FormID is allocated.</summary>
public static class CellLandPlanner
{
    private const int LandVertexCount = 33 * 33;

    public static CellLandPlanningResult DecideAll(IReadOnlyList<CellCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var decisions = ImmutableDictionary.CreateBuilder<uint, CellLandDecision>();
        var diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();

        foreach (var entry in entries)
        {
            if (entry.Source != SourceKind.DmpNew || entry.DmpModel is not { } cell
                || cell.IsInterior || !cell.WorldspaceFormId.HasValue
                || cell.IsPersistentCell || cell.IsVirtual || cell.IsUnresolvedBucket)
            {
                continue;
            }

            LandHeightmap? heightmap = null;
            var source = CellLandHeightSource.CapturedHeightmap;
            var capturedHeightmap = IsUsable(cell.CapturedLandHeightmap)
                ? cell.CapturedLandHeightmap
                : cell.Heightmap is { ExactHeights: null } && IsUsable(cell.Heightmap)
                    ? cell.Heightmap
                    : null;
            if (capturedHeightmap is not null)
            {
                heightmap = capturedHeightmap;
            }
            else if (cell.RuntimeTerrainMesh is { } runtimeMesh)
            {
                var quality = runtimeMesh.DiagnoseQuality(
                    cell.GridX ?? 0, cell.GridY ?? 0, cell.FormId);
                if (string.Equals(quality.Classification, "Complete", StringComparison.Ordinal))
                {
                    try
                    {
                        heightmap = runtimeMesh.ToLandHeightmap();
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
                    diagnostics.Add(new PlanDiagnostic
                    {
                        Kind = PlanDiagnosticKind.Warning,
                        Phase = "CellLand",
                        Code = "land.runtime-mesh-not-complete",
                        RecordType = "CELL",
                        FormId = cell.FormId,
                        Message = $"Skipped LAND for DMP-new CELL 0x{cell.FormId:X8}: runtime terrain classification was {quality.Classification}.",
                    });
                }
            }

            if (heightmap is null)
            {
                continue;
            }

            byte[]? runtimeVertexColors = null;
            if (cell.RuntimeTerrainMesh is { } mesh)
            {
                runtimeVertexColors = mesh.ToLandVertexColorBytes();
            }

            decisions[cell.FormId] = new CellLandDecision
            {
                CellSourceFormId = cell.FormId,
                Heightmap = heightmap,
                VisualData = LandVisualData.MergeForEmission(
                    cell.LandVisualData, runtimeVertexColors, fallback: null),
                HeightSource = source,
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
}

public sealed record CellLandPlanningResult
{
    public required ImmutableDictionary<uint, CellLandDecision> DecisionsByCellSourceFormId { get; init; }
    public required ImmutableArray<PlanDiagnostic> Diagnostics { get; init; }
}
