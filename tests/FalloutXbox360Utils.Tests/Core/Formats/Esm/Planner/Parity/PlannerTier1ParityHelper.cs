using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Shared helper for Tier 1 byte-exact parity tests. Each ported encoder feeds the
///     same synthetic model into both pipelines and asserts the resulting GRUP bytes
///     match. The legacy path is replicated by calling the legacy <c>EncodeNew</c>
///     directly + the byte-emission primitives.
/// </summary>
internal static class PlannerTier1ParityHelper
{
    public static void AssertNewRecordParity(
        string recordType,
        uint formId,
        object model,
        EncodedRecord legacyEncoded)
    {
        var record = new RecordPlan
        {
            Type = recordType,
            Disposition = RecordDisposition.New,
            FormId = formId,
            SourceFormId = formId,
            Model = model,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test new record" },
        };

        var plan = new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet.Create(formId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(formId, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = formId + 1,
                PlannerCoverage = ImmutableHashSet.Create(recordType),
            },
        };

        var options = new PluginBuildOptions { CompressRecords = false };
        var writer = new PlanWriter(PlannedEncoders.BuildRegistry());

        var plannerBytes = writer.BuildGrupForType(recordType, plan, options);

        var legacyRecordBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            recordType, formId, 0u, legacyEncoded.Subrecords);
        var legacyGrupBytes = TopLevelRecordEmitter.WrapInTopLevelGrup(recordType, legacyRecordBytes);

        Assert.Equal(legacyGrupBytes, plannerBytes);
    }
}
