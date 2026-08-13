using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Optional placed-ref link sanitation. XEZN/XLKR/XOWN/XESP/XTEL all carry FormIDs the
///     engine looks up at cell-load time; when one dangles the engine logs "Unable to find
///     linked reference" / "Unable to find enable state parent" and discards the data anyway,
///     so the subrecord is omitted instead. Remap-via-alias-table comes first (same policy as
///     IDLE ANAM / CTDA params / PACK PLDT).
///     <para>
///     The policy itself moved to <c>PlacedRefLinkPlanner</c> in retirement Stage H5
///     (2026-08-12) — these run it plan-then-encode, which is what production does, so the
///     assertions still describe the bytes that reach the file.
///     </para>
/// </summary>
public class PlacedRefSanitizerTests
{
    private const uint CellId = 0x000DADAF;
    private const uint RefId = 0x01000100;

    [Fact]
    public void EncodeNew_skips_XLKR_when_linked_ref_dangles_and_no_remap()
    {
        var encoded = PlanAndEncode(MakePlaced(0x000DEAD1u), [0x00000001u]);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XLKR");
        Assert.Contains(encoded.Warnings, w => w.Contains("XLKR") && w.Contains("dangles"));
    }

    [Fact]
    public void EncodeNew_emits_XLKR_when_linked_ref_is_valid()
    {
        var encoded = PlanAndEncode(MakePlaced(0x000ED239u), [0x000ED239u]);

        var xlkr = Assert.Single(encoded.Subrecords, s => s.Signature == "XLKR");
        Assert.Equal(4, xlkr.Bytes.Length);
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(xlkr.Bytes));
    }

    [Fact]
    public void EncodeNew_remaps_XLKR_dangling_ref_via_alias_table()
    {
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = PlanAndEncode(MakePlaced(0x01999AAAu), [0x01000123u], remap);

        var xlkr = Assert.Single(encoded.Subrecords, s => s.Signature == "XLKR");
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(xlkr.Bytes));
    }

    [Fact]
    public void EncodeNew_degrades_XLKR_to_4_bytes_when_keyword_dangles_but_ref_valid()
    {
        // XLKR is normally 8 bytes when a keyword is supplied; when only the keyword dangles
        // we drop it and emit the 4-byte form so the linked-ref relationship survives.
        var encoded = PlanAndEncode(MakePlaced(0x000ED239u, 0x000DEAD1u), [0x000ED239u]);

        var xlkr = Assert.Single(encoded.Subrecords, s => s.Signature == "XLKR");
        Assert.Equal(4, xlkr.Bytes.Length);
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(xlkr.Bytes));
        Assert.Contains(encoded.Warnings, w => w.Contains("XLKR keyword") && w.Contains("degraded"));
    }

    [Fact]
    public void EncodeNew_skips_XESP_when_parent_dangles()
    {
        var encoded = PlanAndEncode(MakePlaced(enableParent: 0x000DEAD1u), []);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XESP");
        Assert.Contains(encoded.Warnings, w => w.Contains("XESP"));
    }

    [Fact]
    public void EncodeNew_skips_XOWN_when_owner_dangles()
    {
        var encoded = PlanAndEncode(MakePlaced(owner: 0x000DEAD1u), []);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XOWN");
        Assert.Contains(encoded.Warnings, w => w.Contains("XOWN"));
    }

    [Fact]
    public void EncodeNew_skips_XEZN_when_encounter_zone_dangles()
    {
        var encoded = PlanAndEncode(MakePlaced(encounterZone: 0x000DEAD1u), []);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XEZN");
        Assert.Contains(encoded.Warnings, w => w.Contains("XEZN"));
    }

    [Fact]
    public void EncodeNew_skips_XTEL_when_destination_door_dangles()
    {
        var encoded = PlanAndEncode(MakePlaced(door: 0x000DEAD1u), []);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XTEL");
        Assert.Contains(encoded.Warnings, w => w.Contains("XTEL"));
    }

    [Fact]
    public void EncodeNew_emits_all_subrecords_verbatim_when_no_plan_supplied()
    {
        // Shape-level callers (encoder unit tests) pass no plan; the captured values are then
        // emitted as-is, since only the planner is entitled to condemn a link.
        var placed = MakePlaced(0xDEADBEEFu, owner: 0xDEADBEEFu, enableParent: 0xDEADBEEFu);

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        Assert.Single(encoded.Subrecords, s => s.Signature == "XLKR");
        Assert.Single(encoded.Subrecords, s => s.Signature == "XOWN");
        Assert.Single(encoded.Subrecords, s => s.Signature == "XESP");
    }

    [Fact]
    public void EncodeNew_NAME_DATA_XSCL_emit_unconditionally_regardless_of_sanitizer()
    {
        // The required-always subrecords stay in place even when every optional one is dropped.
        var encoded = PlanAndEncode(MakePlaced(0x000DEAD1u), []);

        Assert.Single(encoded.Subrecords, s => s.Signature == "NAME");
        Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
    }

    /// <summary>
    ///     Runs the production sequence: plan the ref's links against <paramref name="valid" />,
    ///     then encode using the planned decisions. No door is ever registered, so any XTEL
    ///     under test resolves to "not a live door".
    /// </summary>
    private static EncodedRecord PlanAndEncode(
        PlacedReference placed,
        uint[] valid,
        Dictionary<uint, uint>? remap = null)
    {
        var child = new RecordPlan
        {
            Type = "REFR",
            Disposition = RecordDisposition.New,
            FormId = RefId,
            SourceFormId = RefId,
            Model = placed,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
        var cell = new CellPlan
        {
            CellFormId = CellId,
            CellRecordPlan = child with { Type = "CELL", FormId = CellId, Model = null },
            Context = new PcEsmCellContext { CellFormId = CellId, IsInterior = true },
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = [child],
            Emits = true,
            RefDecisions = ImmutableDictionary<uint, PlacedRefDecision>.Empty
        };

        var cells = PlacedRefLinkPlanner.Apply(
            ImmutableDictionary<uint, CellPlan>.Empty.Add(CellId, cell),
            new Dictionary<uint, ParsedMainRecord>(),
            remap ?? [],
            valid.ToImmutableHashSet(),
            ImmutableHashSet<uint>.Empty);

        var planned = cells[CellId].TemporaryChildren.Single();
        return RefrEncoder.EncodeNewPlacedReference(placed, new PlanReferenceLookup(planned));
    }

    private static PlacedReference MakePlaced(
        uint? linkedRef = null,
        uint? linkedKeyword = null,
        uint? owner = null,
        uint? enableParent = null,
        uint? encounterZone = null,
        uint? door = null)
    {
        return new PlacedReference
        {
            FormId = RefId,
            RecordType = "REFR",
            BaseFormId = 0x00019C5Fu, // arbitrary valid base
            X = 0, Y = 0, Z = 0, Scale = 1f,
            LinkedRefFormId = linkedRef,
            LinkedRefKeywordFormId = linkedKeyword,
            OwnerFormId = owner,
            EnableParentFormId = enableParent,
            EncounterZoneFormId = encounterZone,
            DestinationDoorFormId = door
        };
    }
}
