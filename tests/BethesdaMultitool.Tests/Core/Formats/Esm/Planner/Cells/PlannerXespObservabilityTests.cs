using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class PlannerXespObservabilityTests
{
    [Fact]
    public void Override_Sanitation_Records_Skipped_Xesp()
    {
        const uint missingParent = 0xAA00DEAD;
        var xesp = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(xesp, missingParent);
        var stats = new ConversionPipelineStats();
        var context = MakeContext(MakeEmptyPlan(), stats);

        var sanitized = OverrideSubrecordSanitizer.Sanitize(
            [new EncodedSubrecord("XESP", xesp)], context,
            PlannedChild("REFR", Dangling("XESP", missingParent)));

        Assert.DoesNotContain(sanitized, subrecord => subrecord.Signature == "XESP");
        Assert.Equal(1, stats.PlannerXespObserved);
        Assert.Equal(0, stats.PlannerXespEmitted);
        Assert.Equal(1, stats.PlannerXespSkipped);
        var observation = Assert.Single(stats.PlannerXespObservations);
        Assert.False(observation.XespEmitted);
        Assert.Equal(missingParent, observation.SourceParentFormId);
        Assert.Equal(missingParent, observation.FinalParentFormId);
        Assert.Equal(PlannerXespParentStatus.Absent, observation.ParentStatus);
        Assert.Equal("absent-from-master-and-capture", observation.Reason);
    }

    [Theory]
    [InlineData(PlacedRefEmitVerdict.Emit, PlannerXespParentStatus.LiveEmitted, "planner-emit")]
    [InlineData(PlacedRefEmitVerdict.Drop, PlannerXespParentStatus.CapturedDropped,
        "refr.diagnostic-skip-new")]
    public void Captured_Parent_Uses_Planner_Verdict(
        PlacedRefEmitVerdict verdict,
        PlannerXespParentStatus expectedStatus,
        string expectedReason)
    {
        const uint sourceParent = 0xAA001234;
        const uint emittedParent = 0x01000802;
        var plan = MakePlanWithCapturedParent(sourceParent, emittedParent, verdict);
        var stats = new ConversionPipelineStats();
        var context = MakeContext(plan, stats);

        // The emitted=true/drop-verdict case captures the current phantom-valid-set shape:
        // observability must flag the parent even if sanitation still writes the XESP.
        PlannedPlacedRefEncoder.RecordEnableParentOutcome(sourceParent, true, context);

        var observation = Assert.Single(stats.PlannerXespObservations);
        Assert.True(observation.XespEmitted);
        Assert.Equal(sourceParent, observation.SourceParentFormId);
        Assert.Equal(emittedParent, observation.FinalParentFormId);
        Assert.Equal(expectedStatus, observation.ParentStatus);
        Assert.Equal(expectedReason, observation.Reason);
    }

    [Theory]
    [InlineData(0xFF001234u, PlannerXespParentStatus.RuntimeState, "runtime-formid")]
    [InlineData(0xAA001234u, PlannerXespParentStatus.Absent, "absent-from-master-and-capture")]
    public void Missing_Parent_Distinguishes_Runtime_From_Absent(
        uint sourceParent,
        PlannerXespParentStatus expectedStatus,
        string expectedReason)
    {
        var stats = new ConversionPipelineStats();
        var context = MakeContext(MakeEmptyPlan(), stats);

        PlannedPlacedRefEncoder.RecordEnableParentOutcome(sourceParent, false, context);

        var observation = Assert.Single(stats.PlannerXespObservations);
        Assert.False(observation.XespEmitted);
        Assert.Equal(expectedStatus, observation.ParentStatus);
        Assert.Equal(expectedReason, observation.Reason);
        Assert.Equal([sourceParent], stats.PlannerXespMissingParentFormIds);
    }

    [Theory]
    [InlineData("ACHR", "CREA", false)] // xex44 0x000834C0: captured NPC placement re-based to a creature
    [InlineData("ACHR", "NPC_", true)]
    [InlineData("ACRE", "CREA", true)]
    [InlineData("REFR", "NPC_", false)]
    public void Override_Name_With_Incompatible_Master_Base_Type_Is_Dropped(
        string placedType, string baseType, bool expectKept)
    {
        const uint baseFormId = 0x0008156E;
        var name = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(name, baseFormId);
        var context = MakeContext(MakeEmptyPlan(), new ConversionPipelineStats(),
            new Dictionary<uint, ParsedMainRecord>
            {
                [baseFormId] = new()
                {
                    Header = new MainRecordHeader
                    {
                        Signature = baseType, DataSize = 0, Flags = 0, FormId = baseFormId,
                        Timestamp = 0, VcsInfo = 0, Version = 15
                    },
                    Offset = 0
                }
            });

        var planned = PlanNameAgainstMaster(placedType, baseFormId, context);
        var sanitized = OverrideSubrecordSanitizer.Sanitize(
            [new EncodedSubrecord("NAME", name)], context, planned);

        if (expectKept)
        {
            Assert.Contains(sanitized, subrecord => subrecord.Signature == "NAME");
        }
        else
        {
            // Master's own NAME survives the merge instead.
            Assert.DoesNotContain(sanitized, subrecord => subrecord.Signature == "NAME");
        }
    }

    private static CellChildEncodeContext MakeContext(
        EmitPlan plan,
        ConversionPipelineStats stats,
        Dictionary<uint, ParsedMainRecord>? masterByFormId = null)
    {
        masterByFormId ??= [];
        var masterRefFormIds = new HashSet<uint>();
        var classifier = new PlannerXespParentClassifier(
            plan, masterByFormId, masterRefFormIds);
        return new CellChildEncodeContext(
            plan,
            masterByFormId,
            [.. plan.EmittedFormIds, .. masterByFormId.Keys],
            new PluginBuildOptions(),
            stats,
            null,
            masterRefFormIds,
            null,
            classifier);
    }

    private static EmitPlan MakePlanWithCapturedParent(
        uint sourceParent,
        uint emittedParent,
        PlacedRefEmitVerdict verdict)
    {
        const uint cellFormId = 0x000ABCDE;
        var parent = new PlacedReference
        {
            FormId = sourceParent,
            BaseFormId = 0x0000001F,
            RecordType = "REFR",
            IsPersistent = true
        };
        var parentPlan = MakeRecordPlan(
            "REFR", RecordDisposition.New, emittedParent, parent, sourceParent);
        var decision = verdict == PlacedRefEmitVerdict.Emit
            ? new PlacedRefDecision
            {
                Verdict = verdict,
                FinalBaseFormId = parent.BaseFormId,
                TargetGroupType = 8
            }
            : new PlacedRefDecision
            {
                Verdict = verdict,
                DropReason = "refr.diagnostic-skip-new"
            };
        var cellPlan = new CellPlan
        {
            CellFormId = cellFormId,
            CellRecordPlan = MakeRecordPlan(
                "CELL", RecordDisposition.Override, cellFormId, null),
            Context = new PcEsmCellContext
            {
                CellFormId = cellFormId,
                IsInterior = true
            },
            PersistentChildren = [parentPlan],
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
            Emits = true,
            RefDecisions = ImmutableDictionary<uint, PlacedRefDecision>.Empty
                .Add(emittedParent, decision)
        };

        return MakeEmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(cellFormId, cellPlan),
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(sourceParent, emittedParent),
            EmittedFormIds = ImmutableHashSet.Create(emittedParent)
        };
    }

    private static RecordPlan MakeRecordPlan(
        string type,
        RecordDisposition disposition,
        uint formId,
        object? model,
        uint? sourceFormId = null)
    {
        return new RecordPlan
        {
            Type = type,
            Disposition = disposition,
            FormId = formId,
            SourceFormId = sourceFormId,
            Model = model,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }

    private static EmitPlan MakeEmptyPlan()
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

    /// <summary>
    ///     Runs the real link planner over a one-child cell whose captured base is
    ///     <paramref name="baseFormId" />, so the NAME type rule under test is the production
    ///     one rather than a fixture restatement of it.
    /// </summary>
    private static RecordPlan PlanNameAgainstMaster(
        string placedType, uint baseFormId, CellChildEncodeContext context)
    {
        const uint cellFormId = 0x000DADAF;
        const uint refFormId = 0x0010F076;
        var child = PlannedChild(placedType) with
        {
            FormId = refFormId,
            Disposition = RecordDisposition.Override,
            Model = new PlacedReference
            {
                FormId = refFormId,
                RecordType = placedType,
                BaseFormId = baseFormId
            }
        };
        var cell = new CellPlan
        {
            CellFormId = cellFormId,
            CellRecordPlan = child with { Type = "CELL", FormId = cellFormId, Model = null },
            Context = new PcEsmCellContext { CellFormId = cellFormId, IsInterior = true },
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = [child],
            Emits = true,
            RefDecisions = ImmutableDictionary<uint, PlacedRefDecision>.Empty
        };

        // The placed record's own type drives the check, so master must carry it under that
        // signature (production reads it from the record the merge emits under).
        var master = context.MasterByFormId.ToDictionary(kv => kv.Key, kv => kv.Value);
        master[refFormId] = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = placedType, FormId = refFormId },
            Offset = 0
        };

        var cells = PlacedRefLinkPlanner.Apply(
            ImmutableDictionary<uint, CellPlan>.Empty.Add(cellFormId, cell),
            master,
            ImmutableDictionary<uint, uint>.Empty,
            ImmutableHashSet<uint>.Empty,
            ImmutableHashSet<uint>.Empty);

        return cells[cellFormId].TemporaryChildren.Single();
    }

    private static ResolvedRef Dangling(string signature, uint original)
    {
        return new ResolvedRef
        {
            FieldPath = FieldPath.IndexedMember(signature, 0, "Slot0"),
            OriginalFormId = original,
            Action = ResolvedRefAction.DropSubrecord,
            Reason = "refr.override-subrecord-dangling"
        };
    }

    private static RecordPlan PlannedChild(string type, params ResolvedRef[] references)
    {
        return new RecordPlan
        {
            Type = type,
            Disposition = RecordDisposition.Override,
            FormId = 0x0010F076,
            References = [.. references],
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }
}