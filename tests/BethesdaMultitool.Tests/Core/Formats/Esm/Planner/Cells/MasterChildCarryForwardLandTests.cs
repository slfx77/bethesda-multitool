using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Master-LAND fallback in the planner cell writer: an exterior master-cell override
///     keeps its terrain by copying master LAND verbatim ONLY when no planned LAND
///     (captured-terrain override or new record) already occupies the temporary prefix —
///     otherwise the cell would carry two LAND records.
/// </summary>
public sealed class MasterChildCarryForwardLandTests
{
    private const uint CellId = 0x000DDF1C;
    private const uint MasterLandId = 0x000ABC01;

    [Fact]
    public void AppendMasterLandFallback_Skips_When_Planned_Land_Present()
    {
        var prefix = new List<byte[]> { new byte[] { 1, 2, 3 } };

        MasterChildCarryForward.AppendMasterLandFallback(MakeContext(), MakeState(), prefix);

        var kept = Assert.Single(prefix);
        Assert.Equal(new byte[] { 1, 2, 3 }, kept);
    }

    [Fact]
    public void AppendMasterLandFallback_Copies_Master_Land_When_No_Planned_Land()
    {
        var prefix = new List<byte[]>();

        MasterChildCarryForward.AppendMasterLandFallback(MakeContext(), MakeState(), prefix);

        var record = Assert.Single(prefix);
        Assert.Equal("LAND", Encoding.ASCII.GetString(record, 0, 4));
        Assert.Equal(MasterLandId, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12, 4)));
    }

    private static CellChildEncodeContext MakeContext()
    {
        var masterLand = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "LAND",
                DataSize = 0,
                Flags = 0,
                FormId = MasterLandId,
                Timestamp = 0,
                VcsInfo = 0,
                Version = 15
            },
            Offset = 0,
            Subrecords = [new ParsedSubrecord { Signature = "DATA", Data = [0, 0, 0, 0] }]
        };
        var masterByFormId = new Dictionary<uint, ParsedMainRecord> { [MasterLandId] = masterLand };
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
            LandsByCell = new Dictionary<uint, List<uint>> { [CellId] = [MasterLandId] },
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

        return new CellChildEncodeContext(
            plan, masterByFormId, [], new PluginBuildOptions(), null, masterIndex,
            new HashSet<uint>(), null,
            new Dictionary<uint, PlannerXespParentClassifier.Resolution>());
    }

    private static CellEncodeState MakeState()
    {
        return new CellEncodeState
        {
            CellFormId = CellId,
            Mode = CellMergeMode.PersistentOnly,
            IsMasterAnchored = true,
            IsInterior = false,
            DropRenderCullingMarkers = false
        };
    }
}