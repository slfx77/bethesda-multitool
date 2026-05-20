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
        var npc = MakeNpc(packages: [0x000ED239u]);
        var valid = new HashSet<uint> { 0x000ED239u };

        var encoded = NpcEncoder.EncodeNew(npc, validPackageFormIds: valid);

        var pkids = encoded.Subrecords.Where(s => s.Signature == "PKID").ToList();
        Assert.Single(pkids);
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(pkids[0].Bytes));
    }

    [Fact]
    public void EncodeNew_drops_PKID_when_package_FormID_is_dangling_and_no_remap()
    {
        var npc = MakeNpc(packages: [0x000CDA76u]);   // From the live error log
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = NpcEncoder.EncodeNew(npc, validPackageFormIds: valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "PKID"));
        Assert.Contains(encoded.Warnings, w => w.Contains("PKID") && w.Contains("dropped"));
    }

    [Fact]
    public void EncodeNew_remaps_PKID_when_dangling_FormID_resolves_via_remapTable()
    {
        var npc = MakeNpc(packages: [0xDEADBEEFu]);
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
        var npc = MakeNpc(packages: [0u, 0x000ED239u]);
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
        var npc = MakeNpc(packages:
        [
            0x000ED239u,    // valid
            0x000CDA76u,    // dangling, no remap
            0xDEADBEEFu     // dangling but remap available
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
        Assert.Equal(0x000ED239u, pkids[0]);     // kept (valid)
        Assert.Equal(0x01000456u, pkids[1]);     // remapped (was DEADBEEF)
        // 0x000CDA76u (the unmappable dangling one) was dropped.
    }

    [Fact]
    public void EncodeNew_emits_all_PKIDs_when_no_validPackageFormIds_is_supplied()
    {
        // Backward-compat: the existing override-mode call sites (and any test that
        // doesn't pass validPackageFormIds) should keep emitting every PKID verbatim.
        var npc = MakeNpc(packages: [0x000ED239u, 0x000CDA76u]);

        var encoded = NpcEncoder.EncodeNew(npc, validPackageFormIds: null);

        Assert.Equal(2, encoded.Subrecords.Count(s => s.Signature == "PKID"));
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
                Flags: 0,
                FatigueBase: 0,
                BarterGold: 0,
                Level: 1,
                CalcMin: 1,
                CalcMax: 1,
                SpeedMultiplier: 100,
                KarmaAlignment: 0f,
                DispositionBase: 0,
                TemplateFlags: 0,
                Offset: 0,
                IsBigEndian: false),
            Packages = packages.ToList()
        };
    }
}
