using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     The writer must honor planner-settled cell decisions (<see cref="CellPlan.Mode" /> +
///     <see cref="CellPlan.DropRenderCullingMarkers" />) and only compute them itself when the
///     plan predates the mode-planning stage (Mode null). Regression guard for the
///     writer→planner decision migration: if the writer silently reverts to recomputing,
///     these planned-value-wins cases fail.
/// </summary>
public sealed class CellDecisionFallbackTests
{
    private const uint CellId = 0x000ABCDE; // interior, master-anchored
    private const uint MasterStatBaseId = 0x000A2001; // valid master base for new refs
    private const uint NewRefId = 0x01000901; // plugin-range placed-ref FormID
    private const uint PortalMarkerBase = 0x20; // engine render-culling base (PortalMarker)
    private const uint MarkerRefId = 0x01000902;

    [Fact]
    public void Planned_PersistentOnly_Mode_Wins_Over_Writer_Fallback()
    {
        // Planned PersistentOnly drops the non-persistent NEW ref. The writer-side
        // fallback would classify Skip (empty master ref set), which does NOT gate new
        // refs — so a drop here proves the planned Mode was consumed.
        var (_, stats) = BuildSection(CellMergeMode.PersistentOnly, false);

        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("cell.persistent-only-nonpersistent-ref"));
    }

    [Fact]
    public void Null_Mode_Falls_Back_To_Writer_Computation_And_Emits()
    {
        var (section, stats) = BuildSection(null, false);

        Assert.Equal(0, stats.DropReasonCounts.GetValueOrDefault("cell.persistent-only-nonpersistent-ref"));
        Assert.NotNull(FindRecord(section, NewRefId));
    }

    [Fact]
    public void Planned_Marker_Drop_Wins_Over_Writer_Fallback()
    {
        // A planned marker-drop policy kills the engine-base (0x20) marker placement; the
        // writer fallback carries no marker policy for this fixture and would emit it.
        var (section, stats) = BuildSection(
            CellMergeMode.LoadedReplacement, true, true);

        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("cell.render-culling-marker-dropped"));
        Assert.Null(FindRecord(section, MarkerRefId));
    }

    [Fact]
    public void Null_Mode_Fallback_Emits_Marker_Placement()
    {
        var (section, stats) = BuildSection(null, false, true);

        Assert.Equal(0, stats.DropReasonCounts.GetValueOrDefault("cell.render-culling-marker-dropped"));
        Assert.NotNull(FindRecord(section, MarkerRefId));
    }

    // ---- fixture plumbing ----------------------------------------------------------

    private static (byte[]? Section, ConversionPipelineStats Stats) BuildSection(
        CellMergeMode? mode, bool dropMarkers, bool includeMarkerRef = false)
    {
        var keeper = new PlacedReference
        {
            FormId = NewRefId, BaseFormId = MasterStatBaseId, RecordType = "REFR", IsPersistent = false
        };
        var children = ImmutableArray.CreateBuilder<RecordPlan>();
        children.Add(MakeChildPlan("REFR", NewRefId, keeper));
        var placed = new List<PlacedReference> { keeper };

        if (includeMarkerRef)
        {
            var marker = new PlacedReference
            {
                FormId = MarkerRefId, BaseFormId = PortalMarkerBase, RecordType = "REFR", IsPersistent = false
            };
            children.Add(MakeChildPlan("REFR", MarkerRefId, marker));
            placed.Add(marker);
        }

        var dmpCell = new CellRecord { FormId = CellId, PlacedObjects = placed };
        var masterCell = MakeMasterRecord("CELL", CellId) with
        {
            Subrecords =
            [
                new ParsedSubrecord { Signature = "EDID", Data = "Cell\0"u8.ToArray() },
                new ParsedSubrecord { Signature = "DATA", Data = [0x01] } // interior flag
            ]
        };
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>
        {
            [CellId] = masterCell,
            [MasterStatBaseId] = MakeMasterRecord("STAT", MasterStatBaseId)
        };
        var masterIndex = new MasterRecordIndex
        {
            Records = masterByFormId.Values.ToList(),
            RecordsByFormId = masterByFormId,
            FormIds = [.. masterByFormId.Keys],
            FormIdsByType = [],
            EditorIdToFormIdByType = [],
            StemToFormIdsByType = [],
            ChildLocations = [],
            RefToCell = [],
            RefsByCell = [],
            NavmsByCell = [],
            LandsByCell = [],
            CellContexts = []
        };

        var cellPlan = new CellPlan
        {
            CellFormId = CellId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.Override,
                FormId = CellId,
                Model = dmpCell,
                Master = masterCell,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = new PcEsmCellContext
            {
                CellFormId = CellId,
                IsInterior = true,
                BlockGroupType = 2,
                SubblockGroupType = 3,
                BlockLabel = [1, 0, 0, 0],
                SubblockLabel = [2, 0, 0, 0]
            },
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = children.ToImmutable(),
            Mode = mode,
            DropRenderCullingMarkers = dropMarkers
        };

        var plan = new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet<uint>.Empty,
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata { NextObjectId = 0x800, PlannerCoverage = ImmutableHashSet<string>.Empty },
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(CellId, cellPlan)
        };

        var stats = new ConversionPipelineStats();
        var section = PlanCellSectionBuilder
            .BuildCellSectionCore(plan, masterByFormId, new PluginBuildOptions(), stats, masterIndex)
            .SectionBytes;
        return (section, stats);
    }

    private static RecordPlan MakeChildPlan(string type, uint formId, PlacedReference model)
    {
        return new RecordPlan
        {
            Type = type,
            Disposition = RecordDisposition.New,
            FormId = formId,
            Model = model,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }

    private static ParsedMainRecord MakeMasterRecord(string signature, uint formId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature, DataSize = 0, Flags = 0, FormId = formId,
                Timestamp = 0, VcsInfo = 0, Version = 15
            },
            Offset = 0
        };
    }

    private static int? FindRecord(byte[]? section, uint formId)
    {
        if (section is null) return null;
        var pos = 0;
        while (pos + 24 <= section.Length)
        {
            var sig = Encoding.ASCII.GetString(section, pos, 4);
            if (sig == "GRUP")
            {
                pos += 24;
                continue;
            }

            var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(pos + 4, 4));
            var recFormId = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(pos + 12, 4));
            if (sig != "CELL" && recFormId == formId) return pos;
            pos += 24 + dataSize;
        }

        return null;
    }
}