using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     PKID dangling-FormID sanitizer tests for NpcEncoder.EncodeNew. Dangling PKIDs
///     (PACK FormIDs not in master ∪ emitted) leave the NPC without an AI driver and the
///     engine falls through to a default idle — the leading suspect for the "every NPC
///     plays the crucified idle every few seconds" regression seen after we shipped new
///     NAVMs.
/// </summary>
public class NpcPkidSanitizerTests
{
    [Fact]
    public void EncodeNew_emits_PKID_when_package_FormID_is_in_validPackageFormIds()
    {
        var npc = MakeNpc([0x000ED239u]);
        var valid = new HashSet<uint> { 0x000ED239u };

        var encoded = NpcEncoder.EncodeNew(npc, validPackageFormIds: valid);

        var pkids = encoded.Subrecords.Where(s => s.Signature == "PKID").ToList();
        Assert.Single(pkids);
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(pkids[0].Bytes));
    }

    [Fact]
    public void EncodeNew_drops_PKID_when_package_FormID_is_dangling_and_no_remap()
    {
        var npc = MakeNpc([0x000CDA76u]); // From the live error log
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = NpcEncoder.EncodeNew(npc, validPackageFormIds: valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "PKID"));
        Assert.Contains(encoded.Warnings, w => w.Contains("PKID") && w.Contains("dropped"));
    }

    [Fact]
    public void EncodeNew_remaps_PKID_when_dangling_FormID_resolves_via_remapTable()
    {
        var npc = MakeNpc([0xDEADBEEFu]);
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0xDEADBEEFu] = 0x01000123u };

        var encoded = NpcEncoder.EncodeNew(npc,
            validPackageFormIds: valid, remapTable: remap);

        var pkids = encoded.Subrecords.Where(s => s.Signature == "PKID").ToList();
        Assert.Single(pkids);
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(pkids[0].Bytes));
        Assert.Contains(encoded.Warnings, w => w.Contains("remapped"));
    }

    [Fact]
    public void EncodeNew_skips_zero_PKID_entries_without_warning()
    {
        // 0u is a legitimate "no package here" placeholder. Don't warn or include.
        var npc = MakeNpc([0u, 0x000ED239u]);
        var valid = new HashSet<uint> { 0x000ED239u };

        var encoded = NpcEncoder.EncodeNew(npc, validPackageFormIds: valid);

        var pkids = encoded.Subrecords.Where(s => s.Signature == "PKID").ToList();
        Assert.Single(pkids);
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(pkids[0].Bytes));
        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("PKID") && w.Contains("dropped"));
    }

    [Fact]
    public void EncodeNew_filters_mixed_list_keeping_valid_and_remappable_only()
    {
        // Three packages: valid, dangling-no-remap, dangling-with-remap.
        var npc = MakeNpc([
            0x000ED239u, // valid
            0x000CDA76u, // dangling, no remap
            0xDEADBEEFu // dangling but remap available
        ]);
        var valid = new HashSet<uint> { 0x000ED239u, 0x01000456u };
        var remap = new Dictionary<uint, uint> { [0xDEADBEEFu] = 0x01000456u };

        var encoded = NpcEncoder.EncodeNew(npc,
            validPackageFormIds: valid, remapTable: remap);

        var pkids = encoded.Subrecords
            .Where(s => s.Signature == "PKID")
            .Select(s => BinaryPrimitives.ReadUInt32LittleEndian(s.Bytes))
            .ToList();
        Assert.Equal(2, pkids.Count);
        Assert.Equal(0x000ED239u, pkids[0]); // kept (valid)
        Assert.Equal(0x01000456u, pkids[1]); // remapped (was DEADBEEF)
        // 0x000CDA76u (the unmappable dangling one) was dropped.
    }

    [Fact]
    public void EncodeNew_emits_all_PKIDs_when_no_validPackageFormIds_is_supplied()
    {
        // Backward-compat: the existing override-mode call sites (and any test that
        // doesn't pass validPackageFormIds) should keep emitting every PKID verbatim.
        var npc = MakeNpc([0x000ED239u, 0x000CDA76u]);

        var encoded = NpcEncoder.EncodeNew(npc, validPackageFormIds: null);

        Assert.Equal(2, encoded.Subrecords.Count(s => s.Signature == "PKID"));
    }

    // ====================================================================================
    // Reference-field remap tests for INAM / VTCK / TPLT / RNAM / CNAM. These fields used
    // to emit their FormIDs verbatim; the planner regression guard found NPCs in proto DMPs
    // whose RNAM / CNAM pointed at phantom-master FormIDs (proto-allocated, master-prefix,
    // not actually in master), making the engine resolve to nonexistent records. The
    // FormIdReferenceResolver wraps remap-the-target-if-needed + drop-if-dangling so the
    // emitted record only references real records.
    // ====================================================================================

    [Fact]
    public void EncodeNew_emits_RNAM_verbatim_when_race_is_in_master()
    {
        // Typical case: master Race FormID. The resolver passes it through unchanged.
        var npc = MakeNpcWithRefs(0x00019C5Fu);
        var valid = new HashSet<uint> { 0x00019C5Fu };

        var encoded = NpcEncoder.EncodeNew(npc, validFormIds: valid);

        var rnams = encoded.Subrecords.Where(s => s.Signature == "RNAM").ToList();
        Assert.Single(rnams);
        Assert.Equal(0x00019C5Fu, BinaryPrimitives.ReadUInt32LittleEndian(rnams[0].Bytes));
    }

    [Fact]
    public void EncodeNew_remaps_RNAM_to_allocated_FormId_when_race_was_remapped()
    {
        // Proto-only race that we allocated a fresh FormID for. The resolver swaps the
        // source FormID for the allocated one.
        var npc = MakeNpcWithRefs(0xDEADBEEFu);
        var valid = new HashSet<uint> { 0x01001234u };
        var remap = new Dictionary<uint, uint> { [0xDEADBEEFu] = 0x01001234u };

        var encoded = NpcEncoder.EncodeNew(npc, validFormIds: valid, remapTable: remap);

        var rnams = encoded.Subrecords.Where(s => s.Signature == "RNAM").ToList();
        Assert.Single(rnams);
        Assert.Equal(0x01001234u, BinaryPrimitives.ReadUInt32LittleEndian(rnams[0].Bytes));
    }

    [Fact]
    public void EncodeNew_drops_RNAM_with_warning_when_race_is_phantom_master()
    {
        // Phantom-master case: race FormID looks like master (0x0010ABCD) but isn't in
        // master and isn't in the remap table. Emitting verbatim would point the engine at
        // a nonexistent record. The resolver returns null → subrecord dropped → warning.
        var npc = MakeNpcWithRefs(0x0010ABCDu);
        var valid = new HashSet<uint>();

        var encoded = NpcEncoder.EncodeNew(npc, validFormIds: valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "RNAM"));
        Assert.Contains(encoded.Warnings, w => w.Contains("RNAM") && w.Contains("dangles"));
    }

    [Fact]
    public void EncodeNew_drops_CNAM_with_warning_when_class_is_phantom_master()
    {
        var npc = MakeNpcWithRefs(@class: 0x0010ABCDu);
        var valid = new HashSet<uint>();

        var encoded = NpcEncoder.EncodeNew(npc, validFormIds: valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "CNAM"));
        Assert.Contains(encoded.Warnings, w => w.Contains("CNAM") && w.Contains("dangles"));
    }

    [Fact]
    public void EncodeNew_drops_VTCK_with_warning_when_voicetype_is_phantom_master()
    {
        var npc = MakeNpcWithRefs(voiceType: 0x0010ABCDu);
        var valid = new HashSet<uint>();

        var encoded = NpcEncoder.EncodeNew(npc, validFormIds: valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "VTCK"));
        Assert.Contains(encoded.Warnings, w => w.Contains("VTCK") && w.Contains("dangles"));
    }

    [Fact]
    public void EncodeNew_drops_INAM_with_warning_when_deathitem_is_phantom_master()
    {
        var npc = MakeNpcWithRefs(deathItem: 0x0010ABCDu);
        var valid = new HashSet<uint>();

        var encoded = NpcEncoder.EncodeNew(npc, validFormIds: valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "INAM"));
        Assert.Contains(encoded.Warnings, w => w.Contains("INAM") && w.Contains("dangles"));
    }

    [Fact]
    public void EncodeNew_emits_all_reference_fields_verbatim_when_no_validFormIds_supplied()
    {
        // Backward-compat: when neither validFormIds nor remapTable are supplied the
        // resolver passes everything through. Existing override-mode callers must not
        // regress.
        var npc = MakeNpcWithRefs(
            0x00019C5Fu, 0x00057E6Au, 0x000A0E11u,
            0x000ABCDEu);

        var encoded = NpcEncoder.EncodeNew(npc);

        Assert.Single(encoded.Subrecords.Where(s => s.Signature == "RNAM"));
        Assert.Single(encoded.Subrecords.Where(s => s.Signature == "CNAM"));
        Assert.Single(encoded.Subrecords.Where(s => s.Signature == "VTCK"));
        Assert.Single(encoded.Subrecords.Where(s => s.Signature == "INAM"));
    }

    private static NpcRecord MakeNpcWithRefs(
        uint? race = null,
        uint? @class = null,
        uint? voiceType = null,
        uint? deathItem = null,
        uint? template = null)
    {
        return new NpcRecord
        {
            FormId = 0x010008E0,
            EditorId = "NewNpc",
            FullName = "Test NPC",
            Race = race,
            Class = @class,
            VoiceType = voiceType,
            DeathItem = deathItem,
            Template = template,
            Stats = new ActorBaseSubrecord(
                0,
                0,
                0,
                1,
                1,
                1,
                100,
                0f,
                0,
                0,
                0,
                false)
        };
    }

    private static NpcRecord MakeNpc(uint[] packages)
    {
        return new NpcRecord
        {
            FormId = 0x010008E0,
            EditorId = "NewNpc",
            FullName = "Test NPC",
            Race = 0x00019C5Fu,
            Stats = new ActorBaseSubrecord(
                0,
                0,
                0,
                1,
                1,
                1,
                100,
                0f,
                0,
                0,
                0,
                false),
            Packages = packages.ToList()
        };
    }
}