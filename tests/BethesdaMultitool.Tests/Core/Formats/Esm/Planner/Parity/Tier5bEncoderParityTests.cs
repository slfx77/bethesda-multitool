using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Tier 5b parity: REFR/ACHR/ACRE shared placed-ref emission. The PlannedWriter
///     wouldn't normally invoke these (cell-children pipeline integration is the
///     remaining Tier 5b work), but the encoders are byte-equivalent to legacy by
///     construction. These tests pin that for when the dispatch lands.
///     CELL parity isn't tested here because the legacy CellEncoder.Encode emits an
///     override-only payload that needs to be merged against a master CellRecord; a
///     synthetic test would need a full master record fixture which is out of scope
///     for this kickoff.
/// </summary>
public sealed class Tier5bEncoderParityTests
{
    /// <summary>
    ///     The three placed-reference signatures share one encoder, so the record type is the only
    ///     thing that varies between them.
    /// </summary>
    [Theory]
    [InlineData("REFR")]
    [InlineData("ACHR")]
    [InlineData("ACRE")]
    public void PlannedPlacedReference_MatchesLegacyBytes(string recordType)
    {
        var placed = new PlacedReference
        {
            FormId = 0x01000800,
            RecordType = recordType,
            BaseFormId = 0u
        };

        AssertPlacedRefParity(recordType, placed);
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
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "placed-ref parity" }
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
                PlannerCoverage = ImmutableHashSet.Create(recordType)
            }
        };

        var options = new PluginBuildOptions { CompressRecords = false };
        var writer = new PlanWriter(PlannedEncoders.BuildRegistry());

        var plannerBytes = writer.BuildGrupForType(recordType, plan, options);

        var legacyEncoded = RefrEncoder.EncodeNewPlacedReference(
            placed);
        Assert.NotEmpty(legacyEncoded.Subrecords);

        var legacyRecordBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            recordType, placed.FormId, 0u, legacyEncoded.Subrecords);
        var legacyGrupBytes = TopLevelRecordEmitter.WrapInTopLevelGrup(recordType, legacyRecordBytes);

        Assert.Equal(legacyGrupBytes, plannerBytes);
    }
}