using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Tier 7a PGRE parity. Pins that <see cref="PlannedPgreEncoder" /> routes to
///     <see cref="PgreEncoder" />'s primitives byte-for-byte and that the planner's
///     one-record GRUP framing matches the legacy <c>EncodeNew</c> → <c>BuildNewRecordBytes</c>
///     → <c>WrapInTopLevelGrup</c> path.
/// </summary>
public sealed class PlannedPgreEncoderParityTests
{
    private const uint TestFormId = 0x01000800u;
    private const uint TestBaseFormId = 0x000186F1u;

    [Fact]
    public void New_PGRE_Plan_Is_Byte_Equal_To_Legacy()
    {
        var model = new PlacedGrenadeRecord
        {
            FormId = TestFormId,
            EditorId = "TestPGRE",
            BaseFormId = TestBaseFormId,
            PositionX = 100.5f,
            PositionY = -200.25f,
            PositionZ = 50.75f,
        };

        var legacy = PgreEncoder.EncodeNew(model);

        PlannerTier1ParityHelper.AssertNewRecordParity("PGRE", TestFormId, model, legacy);
    }

    [Fact]
    public void Planner_Wrapper_Routes_Override_Disposition_To_Legacy_EncodeOverride()
    {
        var model = new PlacedGrenadeRecord
        {
            FormId = TestFormId,
            EditorId = "TestPGRE",
            BaseFormId = TestBaseFormId,
            PositionX = 10f,
            PositionY = 20f,
            PositionZ = 30f,
        };

        var record = new RecordPlan
        {
            Type = "PGRE",
            Disposition = RecordDisposition.Override,
            FormId = TestFormId,
            SourceFormId = TestFormId,
            Model = model,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test override" },
        };
        var emitPlan = new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet.Create(TestFormId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(TestFormId, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = TestFormId + 1,
                PlannerCoverage = ImmutableHashSet.Create("PGRE"),
            },
        };
        var refs = new PlanReferenceLookup(record, emitPlan);

        var wrapped = new PlannedPgreEncoder().Encode(model, record, refs);
        var legacy = PgreEncoder.EncodeOverride(model);

        Assert.Equal(legacy.Subrecords.Count, wrapped.Subrecords.Count);
        for (var i = 0; i < legacy.Subrecords.Count; i++)
        {
            Assert.Equal(legacy.Subrecords[i].Signature, wrapped.Subrecords[i].Signature);
            Assert.Equal(legacy.Subrecords[i].Bytes, wrapped.Subrecords[i].Bytes);
        }
    }

    [Fact]
    public void EncodeOverride_Skips_NAME_When_BaseFormId_Is_Zero()
    {
        var model = new PlacedGrenadeRecord
        {
            FormId = TestFormId,
            BaseFormId = 0u,
            PositionX = 0f,
            PositionY = 0f,
            PositionZ = 0f,
        };

        var encoded = PgreEncoder.EncodeOverride(model);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "NAME");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "DATA");
    }
}
