using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Tier 5b parity: REFR/ACHR/ACRE shared placed-ref emission. The PlannedWriter
///     wouldn't normally invoke these (cell-children pipeline integration is the
///     remaining Tier 5b work), but the encoders are byte-equivalent to legacy by
///     construction. These tests pin that for when the dispatch lands.
///
///     CELL parity isn't tested here because the legacy CellEncoder.Encode emits an
///     override-only payload that needs to be merged against a master CellRecord; a
///     synthetic test would need a full master record fixture which is out of scope
///     for this kickoff.
/// </summary>
public sealed class Tier5bEncoderParityTests
{
    [Fact]
    public void New_Refr_Placed_Reference_Bytes_Match_Legacy()
    {
        var refr = new PlacedReference
        {
            FormId = 0x01000800,
            RecordType = "REFR",
            BaseFormId = 0u,
        };

        AssertPlacedRefParity("REFR", refr);
    }

    [Fact]
    public void New_Achr_Placed_Reference_Bytes_Match_Legacy()
    {
        var achr = new PlacedReference
        {
            FormId = 0x01000800,
            RecordType = "ACHR",
            BaseFormId = 0u,
        };

        AssertPlacedRefParity("ACHR", achr);
    }

    [Fact]
    public void New_Acre_Placed_Reference_Bytes_Match_Legacy()
    {
        var acre = new PlacedReference
        {
            FormId = 0x01000800,
            RecordType = "ACRE",
            BaseFormId = 0u,
        };

        AssertPlacedRefParity("ACRE", acre);
    }

    private static void AssertPlacedRefParity(string recordType, PlacedReference placed)
    {
        var record = new RecordPlan
        {
            Type = recordType,
            Disposition = RecordDisposition.New,
            FormId = placed.FormId,
            SourceFormId = placed.FormId,
            Model = placed,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "placed-ref parity" },
        };

        var plan = new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet.Create(placed.FormId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(placed.FormId, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = placed.FormId + 1,
                PlannerCoverage = ImmutableHashSet.Create(recordType),
            },
        };

        var options = new PluginBuildOptions { CompressRecords = false };
        var writer = new PlanWriter(PlannedEncoders.BuildRegistry());

        var plannerBytes = writer.BuildGrupForType(recordType, plan, options);

        var legacyEncoded = RefrEncoder.EncodeNewPlacedReference(
            placed, validFormIds: null, remapTable: null);
        if (legacyEncoded.Subrecords.Count == 0)
        {
            Assert.Empty(plannerBytes);
            return;
        }

        var legacyRecordBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            recordType, placed.FormId, 0u, legacyEncoded.Subrecords);
        var legacyGrupBytes = TopLevelRecordEmitter.WrapInTopLevelGrup(recordType, legacyRecordBytes);

        Assert.Equal(legacyGrupBytes, plannerBytes);
    }
}
