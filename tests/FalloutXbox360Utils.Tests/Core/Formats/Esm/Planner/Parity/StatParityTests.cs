using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Tier 1 byte-exact parity: the bytes <see cref="PlanWriter" /> emits for a STAT GRUP
///     must match what the legacy primitives produce for the same record. Both pipelines
///     ultimately call <c>PluginRecordByteBuilder.BuildNewRecordBytes</c> +
///     <c>TopLevelRecordEmitter.WrapInTopLevelGrup</c>, so the byte streams should be
///     identical by construction. This test pins that invariant.
/// </summary>
public sealed class StatParityTests
{
    [Fact]
    public void New_Stat_GRUP_Bytes_Match_Legacy_Primitives()
    {
        var stat = new StaticRecord
        {
            FormId = 0x01000800,
            EditorId = "TestStaticEdid",
            ModelPath = "test/models/teststatic.nif",
            Bounds = null,
            TextureHashData = null,
        };

        var plan = BuildPlanForOneNewStat(stat);
        var options = new PluginBuildOptions
        {
            MasterFileName = "FalloutNV.esm",
            CompressRecords = false,
        };

        var writer = new PlanWriter(PlannedEncoders.BuildRegistry());

        var plannerBytes = writer.BuildGrupForType("STAT", plan, options);

        // Reference: encode the same record via the legacy primitives directly. This is
        // exactly what the legacy `BuildGrupForType` would produce for one new STAT.
        var legacyEncoded =
            FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World.StatEncoder.EncodeNew(stat);
        var legacyRecordBytes =
            FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output.PluginRecordByteBuilder.BuildNewRecordBytes(
                "STAT", stat.FormId, 0u, legacyEncoded.Subrecords);
        var legacyGrupBytes =
            FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output.TopLevelRecordEmitter.WrapInTopLevelGrup(
                "STAT", legacyRecordBytes);

        Assert.Equal(legacyGrupBytes, plannerBytes);
    }

    [Fact]
    public void Empty_Stat_Plan_Emits_No_GRUP()
    {
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
                PlannerCoverage = ImmutableHashSet<string>.Empty,
            },
        };

        var writer = new PlanWriter(PlannedEncoders.BuildRegistry());
        var bytes = writer.BuildGrupForType("STAT", plan, new PluginBuildOptions());

        Assert.Empty(bytes);
    }

    [Fact]
    public void KeepMaster_Stat_Plan_Emits_No_GRUP()
    {
        // A KeepMaster record stays in the master ESM — the plugin emits nothing for it.
        var plan = new EmitPlan
        {
            Records =
            [
                new RecordPlan
                {
                    Type = "STAT",
                    Disposition = RecordDisposition.KeepMaster,
                    FormId = 0x000ABCDE,
                    References = ImmutableArray<ResolvedRef>.Empty,
                    ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                    Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
                },
            ],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = [0x000ABCDEu],
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(0x000ABCDEu, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x800,
                PlannerCoverage = ImmutableHashSet.Create("STAT"),
            },
        };

        var writer = new PlanWriter(PlannedEncoders.BuildRegistry());
        var bytes = writer.BuildGrupForType("STAT", plan, new PluginBuildOptions());

        Assert.Empty(bytes);
    }

    private static EmitPlan BuildPlanForOneNewStat(StaticRecord stat)
    {
        var record = new RecordPlan
        {
            Type = "STAT",
            Disposition = RecordDisposition.New,
            FormId = stat.FormId,
            SourceFormId = stat.FormId,
            Model = stat,
            Master = null,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test new STAT" },
        };

        return new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet.Create(stat.FormId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(stat.FormId, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = stat.FormId + 1,
                PlannerCoverage = ImmutableHashSet.Create("STAT"),
            },
        };
    }
}
