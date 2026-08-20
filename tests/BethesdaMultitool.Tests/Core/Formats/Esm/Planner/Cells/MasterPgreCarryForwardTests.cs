using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
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
///     Master PGRE (placed grenade — mines) carry-forward. PGRE joined the
///     <see cref="MasterRecordIndex" /> child gate on 2026-08-07: master files 174 of them,
///     and before the gate widened, every mine in a cell this converter overrides was
///     silently destroyed by the cell-ownership rule. These tests pin that an uncovered
///     master PGRE is re-emitted (both merge modes) and a covered one is left to the
///     override that covered it.
/// </summary>
public sealed class MasterPgreCarryForwardTests
{
    private const uint CellId = 0x000DADAF;
    private const uint MasterPgreId = 0x000A1001;
    private const uint MasterProjBaseId = 0x000A2001;

    [Theory]
    [InlineData(CellMergeMode.PersistentOnly)]
    [InlineData(CellMergeMode.LoadedReplacement)]
    public void Uncovered_Master_Pgre_Is_Carried_Into_Temporary_Bucket(CellMergeMode mode)
    {
        var (persistent, vwd, temporary) = RunApply(mode, false);

        Assert.Empty(persistent);
        Assert.Empty(vwd);
        var record = Assert.Single(temporary);
        Assert.Equal("PGRE", Encoding.ASCII.GetString(record, 0, 4));
        Assert.Equal(MasterPgreId, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12, 4)));
    }

    [Theory]
    [InlineData(CellMergeMode.PersistentOnly)]
    [InlineData(CellMergeMode.LoadedReplacement)]
    public void Covered_Master_Pgre_Is_Not_Carried(CellMergeMode mode)
    {
        var (persistent, vwd, temporary) = RunApply(mode, true);

        Assert.Empty(persistent);
        Assert.Empty(vwd);
        Assert.Empty(temporary);
    }

    private static (List<byte[]> Persistent, List<byte[]> Vwd, List<byte[]> Temporary) RunApply(
        CellMergeMode mode,
        bool covered)
    {
        var name = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(name, MasterProjBaseId);
        var masterPgre = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "PGRE", DataSize = 0, Flags = 0, FormId = MasterPgreId,
                Timestamp = 0, VcsInfo = 0, Version = 15
            },
            Offset = 0,
            Subrecords =
            [
                new ParsedSubrecord { Signature = "NAME", Data = name },
                new ParsedSubrecord { Signature = "DATA", Data = new byte[24] }
            ]
        };
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>
        {
            [MasterPgreId] = masterPgre,
            [MasterProjBaseId] = new()
            {
                Header = new MainRecordHeader
                {
                    Signature = "PROJ", DataSize = 0, Flags = 0, FormId = MasterProjBaseId,
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
                [MasterPgreId] = new(CellId, 9, "PGRE")
            },
            // RefToCell deliberately omits PGRE (see MasterRecordIndex.BuildChildRecordToCellIndex);
            // carry-forward must work from ChildLocations + RefsByCell alone.
            RefToCell = [],
            RefsByCell = new Dictionary<uint, List<uint>> { [CellId] = [MasterPgreId] },
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
            new PlannerXespParentClassifier(plan, masterByFormId, new HashSet<uint>()));
        var state = new CellEncodeState
        {
            CellFormId = CellId,
            Mode = mode,
            IsMasterAnchored = true,
            IsInterior = false,
            DropRenderCullingMarkers = false
        };
        if (covered)
        {
            state.CoveredMasterRefFormIds.Add(MasterPgreId);
        }

        var persistent = new List<byte[]>();
        var vwd = new List<byte[]>();
        var temporary = new List<byte[]>();
        MasterChildCarryForward.Apply(
            context, state, new CellRecord { FormId = CellId }, persistent, vwd, temporary);
        return (persistent, vwd, temporary);
    }
}