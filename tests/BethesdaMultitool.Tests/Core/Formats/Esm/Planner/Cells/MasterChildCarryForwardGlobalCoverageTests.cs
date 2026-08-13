using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Cross-cell coverage in the carry-forward/tombstone pass: a master temp ref the plan
///     emits in ANOTHER cell (cross-cell move) must be neither carried forward nor
///     tombstoned in its home cell — the per-cell covered set alone can't see the move.
/// </summary>
public sealed class MasterChildCarryForwardGlobalCoverageTests
{
    private const uint CellId = 0x000DADAF;
    private const uint MasterTempRefId = 0x000A1001;
    private const uint MasterStatBaseId = 0x000A2001;

    [Fact]
    public void Globally_Emitted_Ref_Is_Neither_Carried_Nor_Tombstoned_In_Home_Cell()
    {
        var (persistent, vwd, temporary) = RunApply(
            ImmutableHashSet.Create(MasterTempRefId));

        Assert.Empty(persistent);
        Assert.Empty(vwd);
        Assert.Empty(temporary);
    }

    [Fact]
    public void Uncovered_Ref_Still_Produces_A_Record_Without_Global_Coverage()
    {
        var (persistent, vwd, temporary) = RunApply(
            ImmutableHashSet<uint>.Empty);

        // Baseline sanity: without the global view the home cell acts on the ref
        // (carry-forward or removal — either produces at least one record).
        Assert.True(persistent.Count + vwd.Count + temporary.Count > 0);
    }

    private static (List<byte[]> Persistent, List<byte[]> Vwd, List<byte[]> Temporary) RunApply(
        IReadOnlySet<uint> globallyEmitted)
    {
        var name = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(name, MasterStatBaseId);
        var data = new byte[24];
        var masterRef = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR", DataSize = 0, Flags = 0, FormId = MasterTempRefId,
                Timestamp = 0, VcsInfo = 0, Version = 15
            },
            Offset = 0,
            Subrecords =
            [
                new ParsedSubrecord { Signature = "NAME", Data = name },
                new ParsedSubrecord { Signature = "DATA", Data = data }
            ]
        };
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>
        {
            [MasterTempRefId] = masterRef,
            [MasterStatBaseId] = new()
            {
                Header = new MainRecordHeader
                {
                    Signature = "STAT", DataSize = 0, Flags = 0, FormId = MasterStatBaseId,
                    Timestamp = 0, VcsInfo = 0, Version = 15
                },
                Offset = 0
            }
        };
        var masterIndex = new MasterRecordIndex
        {
            Records = masterByFormId.Values.ToList(),
            RecordsByFormId = masterByFormId,
            FormIds = [.. masterByFormId.Keys],
            FormIdsByType = [],
            EditorIdToFormIdByType = [],
            StemToFormIdsByType = [],
            ChildLocations = new Dictionary<uint, MasterChildLocation>
            {
                [MasterTempRefId] = new(CellId, 9, "REFR")
            },
            RefToCell = new Dictionary<uint, uint> { [MasterTempRefId] = CellId },
            RefsByCell = new Dictionary<uint, List<uint>> { [CellId] = [MasterTempRefId] },
            NavmsByCell = [],
            LandsByCell = [],
            CellContexts = []
        };
        var plan = new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet<uint>.Empty,
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x800,
                PlannerCoverage = ImmutableHashSet<string>.Empty
            }
        };
        var context = new CellChildEncodeContext(
            plan, masterByFormId, [], new PluginBuildOptions(), null, masterIndex,
            new HashSet<uint>(), null,
            new PlannerXespParentClassifier(plan, masterByFormId, new HashSet<uint>()))
        {
            GloballyEmittedMasterRefs = globallyEmitted
        };
        var state = new CellEncodeState
        {
            CellFormId = CellId,
            Mode = CellMergeMode.LoadedReplacement,
            IsMasterAnchored = true,
            IsInterior = false,
            DropRenderCullingMarkers = false
        };

        var persistent = new List<byte[]>();
        var vwd = new List<byte[]>();
        var temporary = new List<byte[]>();
        MasterChildCarryForward.Apply(
            context, state, new CellRecord { FormId = CellId }, persistent, vwd, temporary);
        return (persistent, vwd, temporary);
    }
}