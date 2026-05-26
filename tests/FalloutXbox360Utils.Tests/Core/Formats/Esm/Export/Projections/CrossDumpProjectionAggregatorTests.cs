using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Semantic;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Phase 5 chronological-replay coverage. The planner's load-bearing invariant for the
///     streaming refactor is that per-source observations
///     (<see cref="WorldspaceObservation" />, <see cref="CellGroupObservation" />, …) are
///     replayed in <see cref="CrossDumpSourceProjection.BuildDateUtc" /> order, NOT in load
///     order. If we get this wrong, worldspace rename history collapses (the very fix this
///     refactor is trying to preserve) and the ESM-authority gate for cell groups flips.
/// </summary>
public class CrossDumpProjectionAggregatorTests
{
    [Fact]
    public void Worldspace_rename_chain_is_preserved_when_projections_are_passed_in_non_chronological_order()
    {
        // The worldspace's display name evolves across three builds. We deliberately pass
        // them to the aggregator in LATEST-FIRST order to prove the sort + chronological
        // replay are doing the work — load order alone would build "MidName → EarlyName"
        // (or worse, hide one name).
        const uint worldspaceFormId = 0x000ECAC5;
        const string editorId = "CampMcCarranWorld";

        var earlyProjection = BuildSingleCellProjection(
            filePath: "early.dmp",
            buildDate: new DateTime(2009, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            isDmp: true,
            cellFormId: 0x00010001,
            worldspaceFormId: worldspaceFormId,
            worldspaceEditorId: editorId,
            worldspaceFullName: "Camp McCarran Tarmac");

        var midProjection = BuildSingleCellProjection(
            filePath: "mid.dmp",
            buildDate: new DateTime(2009, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            isDmp: true,
            cellFormId: 0x00010001,
            worldspaceFormId: worldspaceFormId,
            worldspaceEditorId: editorId,
            worldspaceFullName: "Camp McCarran");

        var lateProjection = BuildSingleCellProjection(
            filePath: "late.esm",
            buildDate: new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            isDmp: false,
            cellFormId: 0x00010001,
            worldspaceFormId: worldspaceFormId,
            worldspaceEditorId: editorId,
            worldspaceFullName: "Camp McCarran");

        var virtualCanon = new Dictionary<CellCoordinateKey, RealCellCandidate>();
        // Pass in NON-chronological order to verify the aggregator sorts before replay.
        var projections = new List<CrossDumpSourceProjection> { lateProjection, midProjection, earlyProjection };

        var index = CrossDumpProjectionAggregator.AggregateFromProjections(
            projections, virtualCanon, allowedTypes: new HashSet<string> { "Cell" });

        // Build labels are emitted in chronological order.
        Assert.Equal(3, index.Dumps.Count);
        Assert.Equal("early", index.Dumps[0].ShortName);
        Assert.Equal("mid", index.Dumps[1].ShortName);
        Assert.Equal("late", index.Dumps[2].ShortName);

        // The cell's group label carries the full rename chain. The first observation
        // captured the OLD name ("Camp McCarran Tarmac"); the second/third both captured
        // the renamed value ("Camp McCarran"). The label should read:
        //   "Camp McCarran Tarmac → Camp McCarran (CampMcCarranWorld)"
        // If chronological replay were broken (e.g. observations replayed in load order),
        // the first observation would be "Camp McCarran" (from `lateProjection`) and the
        // dedup-against-last-entry would never append "Camp McCarran Tarmac".
        var cellGroups = Assert.Contains("Cell", (IDictionary<string, Dictionary<uint, string>>)index.RecordGroups);
        var label = Assert.Contains(0x00010001u, (IDictionary<uint, string>)cellGroups);
        Assert.Contains("Camp McCarran Tarmac", label);
        Assert.Contains("Camp McCarran", label);
        Assert.Contains("→", label);
        Assert.Contains("CampMcCarranWorld", label);

        // Sanity: cell ended up in StructuredRecords for all three dumps.
        var cellRecords = Assert.Contains("Cell", (IDictionary<string, Dictionary<uint, Dictionary<int, RecordReport>>>)
            index.StructuredRecords);
        var perDump = Assert.Contains(0x00010001u, (IDictionary<uint, Dictionary<int, RecordReport>>)cellRecords);
        Assert.Equal(3, perDump.Count);
    }

    [Fact]
    public void Esm_cell_group_authority_overrides_a_prior_dmp_group_in_chronological_order()
    {
        // Same FormID seen first in a DMP (which can misread WorldspaceFormId), then in an
        // ESM (authoritative). The ESM observation must overwrite the DMP-sourced group.
        const uint cellFormId = 0x00020001;

        var dmpProjection = BuildSingleCellProjection(
            filePath: "dmp.dmp",
            buildDate: new DateTime(2009, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            isDmp: true,
            cellFormId: cellFormId,
            worldspaceFormId: 0x000ECAC5,
            worldspaceEditorId: "CampMcCarranWorld",
            worldspaceFullName: "Camp McCarran");

        var esmProjection = BuildSingleCellProjection(
            filePath: "later.esm",
            buildDate: new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            isDmp: false,
            cellFormId: cellFormId,
            worldspaceFormId: 0x000DA726, // ESM says it actually lives in WastelandNV
            worldspaceEditorId: "WastelandNV",
            worldspaceFullName: "Mojave Wasteland");

        var virtualCanon = new Dictionary<CellCoordinateKey, RealCellCandidate>();
        var projections = new List<CrossDumpSourceProjection> { dmpProjection, esmProjection };

        var index = CrossDumpProjectionAggregator.AggregateFromProjections(
            projections, virtualCanon, allowedTypes: new HashSet<string> { "Cell" });

        var cellGroups = index.RecordGroups["Cell"];
        var label = cellGroups[cellFormId];

        // ESM authority gate: the label resolves to the ESM's worldspace, not the DMP's.
        Assert.Contains("WastelandNV", label);
        Assert.DoesNotContain("CampMcCarranWorld", label);
    }

    [Fact]
    public void ReleaseLateEnrichment_nulls_each_projection_LateEnrichment_in_place()
    {
        // Build two projections — each Project() call gives them a non-null
        // LateEnrichmentRecords. ReleaseLateEnrichment should null both out by replacing
        // the list entries with `projection with { LateEnrichment = null }`.
        var first = BuildSingleCellProjection(
            filePath: "first.dmp",
            buildDate: new DateTime(2009, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            isDmp: true,
            cellFormId: 0x1u,
            worldspaceFormId: 0x2u,
            worldspaceEditorId: "World1",
            worldspaceFullName: "World 1");
        var second = BuildSingleCellProjection(
            filePath: "second.dmp",
            buildDate: new DateTime(2009, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            isDmp: true,
            cellFormId: 0x3u,
            worldspaceFormId: 0x4u,
            worldspaceEditorId: "World2",
            worldspaceFullName: "World 2");

        var projections = new List<CrossDumpSourceProjection> { first, second };
        Assert.NotNull(projections[0].LateEnrichment);
        Assert.NotNull(projections[1].LateEnrichment);

        CrossDumpProjectionAggregator.ReleaseLateEnrichment(projections);

        Assert.Null(projections[0].LateEnrichment);
        Assert.Null(projections[1].LateEnrichment);

        // Other state must be preserved — the ReleaseLateEnrichment call only nulls one field.
        Assert.Equal("first.dmp", projections[0].FilePath);
        Assert.Equal("second.dmp", projections[1].FilePath);
        Assert.NotEmpty(projections[0].CellSkeletons);
        Assert.NotEmpty(projections[1].CellSkeletons);
    }

    // ---------- Helpers ----------

    private static CrossDumpSourceProjection BuildSingleCellProjection(
        string filePath,
        DateTime buildDate,
        bool isDmp,
        uint cellFormId,
        uint worldspaceFormId,
        string worldspaceEditorId,
        string worldspaceFullName)
    {
        var cell = new CellRecord
        {
            FormId = cellFormId,
            EditorId = $"Cell0x{cellFormId:X8}",
            WorldspaceFormId = worldspaceFormId,
            GridX = 0,
            GridY = 0,
            Flags = 0x00
        };
        var worldspace = new WorldspaceRecord
        {
            FormId = worldspaceFormId,
            EditorId = worldspaceEditorId,
            FullName = worldspaceFullName
        };

        var resolver = new FormIdResolver(
            new Dictionary<uint, string> { [worldspaceFormId] = worldspaceEditorId },
            new Dictionary<uint, string> { [worldspaceFormId] = worldspaceFullName },
            new Dictionary<uint, uint>());

        var records = new RecordCollection
        {
            Cells = [cell],
            Worldspaces = [worldspace]
        };

        var source = new SemanticSource
        {
            FilePath = filePath,
            FileType = isDmp ? AnalysisFileType.Minidump : AnalysisFileType.EsmFile,
            Records = records,
            Resolver = resolver
        };

        // Build the projection through the normal Projector and override the BuildDateUtc /
        // IsDmp / DateSource so we can put together a chronological scenario without touching
        // the file system.
        var raw = CrossDumpSourceProjector.Project(source);
        return raw with
        {
            BuildDateUtc = buildDate,
            DateSource = "synthetic",
            IsDmp = isDmp
        };
    }
}
