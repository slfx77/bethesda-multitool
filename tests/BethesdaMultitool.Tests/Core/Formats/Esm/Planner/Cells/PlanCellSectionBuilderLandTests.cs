using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class PlanCellSectionBuilderLandTests
{
    [Fact]
    public void DmpNew_Land_Record_Is_Serialized_Inside_Cell_Section()
    {
        const uint worldspaceSource = 0x0010B96F;
        const uint worldspaceEmitted = 0x01000800;
        const uint cellEmitted = 0x01000801;
        const uint landEmitted = 0x01000802;

        var cellModel = new CellRecord
        {
            FormId = cellEmitted,
            WorldspaceFormId = worldspaceSource,
            GridX = 0,
            GridY = 0
        };
        var land = new CellLandDecision
        {
            CellSourceFormId = 0x0010B901,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 100f,
                HeightDeltas = Enumerable.Repeat((sbyte)1, 33 * 33).ToArray()
            },
            HeightSource = CellLandHeightSource.CapturedHeightmap
        };
        var context = new PcEsmCellContext
        {
            CellFormId = cellEmitted,
            IsInterior = false,
            WorldspaceFormId = worldspaceSource,
            BlockGroupType = 4,
            SubblockGroupType = 5,
            BlockLabel = [0, 0, 0, 0],
            SubblockLabel = [0, 0, 0, 0]
        };
        var cell = new CellPlan
        {
            CellFormId = cellEmitted,
            CellRecordPlan = Record("CELL", cellEmitted, cellModel),
            Context = context,
            PersistentChildren = [],
            VwdChildren = [],
            TemporaryChildren = [Record("LAND", landEmitted, land)],
            ParentWorldspaceFormId = worldspaceSource
        };
        var worldspace = new WorldspacePlan
        {
            WorldspaceFormId = worldspaceEmitted,
            WorldspaceRecordPlan = Record(
                "WRLD", worldspaceEmitted,
                new WorldspaceRecord { FormId = worldspaceSource, EditorId = "TheStripWorld" },
                worldspaceSource),
            CellFormIds = [cellEmitted]
        };
        var plan = EmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(cellEmitted, cell),
            WorldspacesByFormId = ImmutableDictionary<uint, WorldspacePlan>.Empty
                .Add(worldspaceSource, worldspace),
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(worldspaceSource, worldspaceEmitted),
            EmittedFormIds = ImmutableHashSet.Create(worldspaceEmitted, cellEmitted, landEmitted),
            LandByCellSourceFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(land.CellSourceFormId, landEmitted)
        };

        var bytes = PlanCellSectionBuilder.BuildCellSection(
            CellPlanTestHarness.Settle(plan, new Dictionary<uint, ParsedMainRecord>()), new Dictionary<uint, ParsedMainRecord>(),
            new PluginBuildOptions { CompressRecords = false });

        Assert.NotNull(bytes);
        var landOffset = FindSignature(bytes!, "LAND");
        Assert.True(landOffset >= 0);
        Assert.Equal(landEmitted, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(landOffset + 12, 4)));
    }

    private static int FindSignature(byte[] bytes, string signature)
    {
        var expected = Encoding.ASCII.GetBytes(signature);
        return bytes.AsSpan().IndexOf(expected);
    }

    private static RecordPlan Record(string type, uint formId, object model, uint? source = null)
    {
        return new RecordPlan
        {
            Type = type,
            Disposition = RecordDisposition.New,
            FormId = formId,
            SourceFormId = source,
            Model = model,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }

    private static EmitPlan EmptyPlan()
    {
        return new EmitPlan
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
    }
}