using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Phase 4 — RefrEncoder optional placed-ref subrecord sanitizer. XEZN/XLKR/XOWN/XESP/XTEL
///     all carry FormIDs that the engine looks up at cell-load time. When the FormID dangles
///     the engine logs "Unable to find linked reference" / "Unable to find enable state parent"
///     and removes the data anyway, so we skip emission at encode time. Remap-via-alias-table
///     comes first (same policy as IDLE ANAM / CTDA params / PACK PLDT).
/// </summary>
public class PlacedRefSanitizerTests
{
    [Fact]
    public void EncodeNew_skips_XLKR_when_linked_ref_dangles_and_no_remap()
    {
        var placed = MakePlaced(linkedRef: 0x000DEAD1u);
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed, valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "XLKR"));
        Assert.Contains(encoded.Warnings, w => w.Contains("XLKR") && w.Contains("dangles"));
    }

    [Fact]
    public void EncodeNew_emits_XLKR_when_linked_ref_is_valid()
    {
        var placed = MakePlaced(linkedRef: 0x000ED239u);
        var valid = new HashSet<uint> { 0x000ED239u };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed, valid);

        var xlkr = Assert.Single(encoded.Subrecords, s => s.Signature == "XLKR");
        Assert.Equal(4, xlkr.Bytes.Length);
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(xlkr.Bytes));
    }

    [Fact]
    public void EncodeNew_remaps_XLKR_dangling_ref_via_alias_table()
    {
        var placed = MakePlaced(linkedRef: 0x01999AAAu);
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed, valid, remap);

        var xlkr = Assert.Single(encoded.Subrecords, s => s.Signature == "XLKR");
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(xlkr.Bytes));
    }

    [Fact]
    public void EncodeNew_degrades_XLKR_to_4_bytes_when_keyword_dangles_but_ref_valid()
    {
        // XLKR is normally 8 bytes when a keyword is supplied; when only the keyword dangles
        // we drop it and emit the 4-byte form so the linked-ref relationship survives.
        var placed = MakePlaced(linkedRef: 0x000ED239u, linkedKeyword: 0x000DEAD1u);
        var valid = new HashSet<uint> { 0x000ED239u };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed, valid);

        var xlkr = Assert.Single(encoded.Subrecords, s => s.Signature == "XLKR");
        Assert.Equal(4, xlkr.Bytes.Length);
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(xlkr.Bytes));
        Assert.Contains(encoded.Warnings, w => w.Contains("XLKR keyword") && w.Contains("degraded"));
    }

    [Fact]
    public void EncodeNew_skips_XESP_when_parent_dangles()
    {
        var placed = MakePlaced(enableParent: 0x000DEAD1u);
        var valid = new HashSet<uint>();

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed, valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "XESP"));
        Assert.Contains(encoded.Warnings, w => w.Contains("XESP"));
    }

    [Fact]
    public void EncodeNew_skips_XOWN_when_owner_dangles()
    {
        var placed = MakePlaced(owner: 0x000DEAD1u);
        var valid = new HashSet<uint>();

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed, valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "XOWN"));
        Assert.Contains(encoded.Warnings, w => w.Contains("XOWN"));
    }

    [Fact]
    public void EncodeNew_skips_XEZN_when_encounter_zone_dangles()
    {
        var placed = MakePlaced(encounterZone: 0x000DEAD1u);
        var valid = new HashSet<uint>();

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed, valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "XEZN"));
        Assert.Contains(encoded.Warnings, w => w.Contains("XEZN"));
    }

    [Fact]
    public void EncodeNew_skips_XTEL_when_destination_door_dangles()
    {
        var placed = MakePlaced(door: 0x000DEAD1u);
        var valid = new HashSet<uint>();

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed, valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "XTEL"));
        Assert.Contains(encoded.Warnings, w => w.Contains("XTEL"));
    }

    [Fact]
    public void EncodeNew_emits_all_subrecords_verbatim_when_no_validFormIds_supplied()
    {
        // Backward-compat: tests and legacy callers without a validity set should keep the
        // old emit-everything-verbatim behavior.
        var placed = MakePlaced(linkedRef: 0xDEADBEEFu, owner: 0xDEADBEEFu, enableParent: 0xDEADBEEFu);

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        Assert.Single(encoded.Subrecords, s => s.Signature == "XLKR");
        Assert.Single(encoded.Subrecords, s => s.Signature == "XOWN");
        Assert.Single(encoded.Subrecords, s => s.Signature == "XESP");
    }

    [Fact]
    public void EncodeNew_NAME_DATA_XSCL_emit_unconditionally_regardless_of_sanitizer()
    {
        // The required-always subrecords stay in place even when every optional one is dropped.
        var placed = MakePlaced(linkedRef: 0x000DEAD1u);
        var valid = new HashSet<uint>();

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed, valid);

        Assert.Single(encoded.Subrecords, s => s.Signature == "NAME");
        Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
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
            FormId = 0x01000100,
            RecordType = "REFR",
            BaseFormId = 0x00019C5Fu,    // arbitrary valid base
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
