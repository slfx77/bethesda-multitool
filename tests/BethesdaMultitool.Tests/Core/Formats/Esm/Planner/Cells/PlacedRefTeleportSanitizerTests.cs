using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     XTEL destination validity. FormID existence is not enough — prototype and retail data
///     reuse the same REFR identity with different base types, so a teleport whose target
///     resolves to a STAT would hand the engine a door it cannot open.
///     <para>
///         Owned by <c>PlacedRefLinkPlanner</c> since retirement Stage H5 (2026-08-12). It was
///         previously a post-encode writer pass (<c>PlacedRefTeleportSanitizer</c>) that re-derived
///         the answer from master records while serializing; the decision is now settled at plan
///         time and the writer only obeys it.
///     </para>
/// </summary>
public sealed class PlacedRefTeleportSanitizerTests
{
    private const uint CellId = 0x000DADAF;
    private const uint SourceRef = 0x01000801;
    private const uint TargetRef = 0x0010F076;
    private const uint TargetBase = 0x0004E7F4;

    [Fact]
    public void Existing_Stat_Base_Refr_Is_Not_A_Valid_Xtel_Target()
    {
        var resolved = PlanXtelTo("STAT");

        Assert.Equal(ResolvedRefAction.DropSubrecord, resolved.Action);
        Assert.Equal("refr.xtel-target-not-door", resolved.Reason);
    }

    [Fact]
    public void Existing_Door_Base_Refr_Remains_A_Valid_Xtel_Target()
    {
        var resolved = PlanXtelTo("DOOR");

        Assert.Equal(ResolvedRefAction.Resolved, resolved.Action);
        Assert.Equal(TargetRef, resolved.FinalFormId);
    }

    /// <summary>
    ///     Plans one new placed ref teleporting to <see cref="TargetRef" />, whose master base
    ///     is <paramref name="baseSignature" />, and returns the planned XTEL decision.
    /// </summary>
    private static ResolvedRef PlanXtelTo(string baseSignature)
    {
        var master = new Dictionary<uint, ParsedMainRecord>
        {
            [TargetRef] = Record("REFR", TargetRef, TargetBase),
            [TargetBase] = Record(baseSignature, TargetBase)
        };

        var child = new RecordPlan
        {
            Type = "REFR",
            Disposition = RecordDisposition.New,
            FormId = SourceRef,
            SourceFormId = SourceRef,
            Model = new PlacedReference
            {
                FormId = SourceRef,
                RecordType = "REFR",
                BaseFormId = TargetBase,
                DestinationDoorFormId = TargetRef
            },
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };

        var cell = new CellPlan
        {
            CellFormId = CellId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.Override,
                FormId = CellId,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = new PcEsmCellContext { CellFormId = CellId, IsInterior = true },
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = [child],
            Emits = true,
            RefDecisions = ImmutableDictionary<uint, PlacedRefDecision>.Empty
        };

        var plan = new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet.Create(SourceRef),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(CellId, cell),
            Meta = new PlanMetadata
            {
                NextObjectId = 0x800,
                PlannerCoverage = ImmutableHashSet<string>.Empty
            }
        };

        var doorLinks = NavmDoorLinkPlanner.Build(plan, master);
        var cells = PlacedRefLinkPlanner.Apply(
            plan.CellsByFormId, master, plan.SourceToEmittedFormId, plan.EmittedFormIds,
            doorLinks.ValidDoorRefFormIds);

        var planned = cells[CellId].TemporaryChildren.Single();
        return planned.References.Single(r => r.FieldPath == FieldPath.Subrecord("XTEL"));
    }

    private static ParsedMainRecord Record(string signature, uint formId, uint? name = null)
    {
        var subrecords = new List<ParsedSubrecord>();
        if (name is { } baseFormId)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, baseFormId);
            subrecords.Add(new ParsedSubrecord { Signature = "NAME", Data = bytes });
        }

        subrecords.Add(new ParsedSubrecord
        {
            Signature = "EDID",
            Data = Encoding.ASCII.GetBytes($"Test{signature}\0")
        });

        return new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = signature, FormId = formId },
            Subrecords = subrecords
        };
    }
}