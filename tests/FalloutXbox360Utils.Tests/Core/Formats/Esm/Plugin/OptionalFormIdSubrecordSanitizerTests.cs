using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Sanitizer tests covering optional FormID-bearing subrecords: CNTO inventory entries,
///     LVLO leveled-list entries, WEAP ammo (NAM0) and projectile (DNAM), NPC_ SCRI/SPLO/CNTO.
///     All use the shared <see cref="FormIdReferenceResolver" /> remap-then-validity policy:
///     remap via the runtime→emitted alias table when possible, drop the entry/subrecord
///     when the target FormID has neither a remap nor membership in the master ∪ emitted set.
/// </summary>
public class OptionalFormIdSubrecordSanitizerTests
{
    // ---------- ContEncoder ----------

    [Fact]
    public void ContEncodeNew_drops_CNTO_with_dangling_item_FormID()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x01000100,
            EditorId = "TestCont",
            FullName = "Test Container",
            Flags = 0,
            Weight = 1f,
            Contents =
            [
                new InventoryItem(0x00000001u, 1),
                new InventoryItem(0x000DEAD1u, 1)
            ]
        };
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = ContEncoder.EncodeNew(cont, valid);

        var cntos = encoded.Subrecords.Where(s => s.Signature == "CNTO").ToList();
        Assert.Single(cntos);
        Assert.Equal(0x00000001u, BinaryPrimitives.ReadUInt32LittleEndian(cntos[0].Bytes));
        Assert.Contains(encoded.Warnings, w => w.Contains("CNTO") && w.Contains("dangling"));
    }

    [Fact]
    public void ContEncodeNew_skips_SCRI_when_script_dangles()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x01000100, EditorId = "T", Flags = 0, Weight = 1f,
            Script = 0x000DEAD1u
        };
        var valid = new HashSet<uint>();

        var encoded = ContEncoder.EncodeNew(cont, valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "SCRI"));
        Assert.Contains(encoded.Warnings, w => w.Contains("SCRI"));
    }

    [Fact]
    public void ContEncodeNew_remaps_CNTO_via_alias_table()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x01000100, EditorId = "T", Flags = 0, Weight = 1f,
            Contents = [new InventoryItem(0x01999AAAu, 1)]
        };
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = ContEncoder.EncodeNew(cont, valid, remap);

        var cnto = Assert.Single(encoded.Subrecords, s => s.Signature == "CNTO");
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(cnto.Bytes));
    }

    // ---------- LvliEncoder ----------

    [Fact]
    public void LvliEncodeNew_drops_LVLO_entries_with_dangling_FormIDs()
    {
        var lvli = new LeveledListRecord
        {
            FormId = 0x01000200, EditorId = "TestLvli", ListType = "LVLI", ChanceNone = 0,
            Entries =
            [
                new LeveledEntry(1, 0x00000001u, 1),
                new LeveledEntry(1, 0x000DEAD1u, 1),
                new LeveledEntry(1, 0x00000001u, 2)
            ]
        };
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = LvliEncoder.EncodeNew(lvli, valid);

        var lvlos = encoded.Subrecords.Where(s => s.Signature == "LVLO").ToList();
        Assert.Equal(2, lvlos.Count);
        Assert.Contains(encoded.Warnings, w => w.Contains("LVLO"));
    }

    [Fact]
    public void LvliEncodeNew_keeps_LVLO_when_FormID_valid()
    {
        var lvli = new LeveledListRecord
        {
            FormId = 0x01000200, EditorId = "T", ListType = "LVLI",
            Entries = [new LeveledEntry(1, 0x00000001u, 1)]
        };
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = LvliEncoder.EncodeNew(lvli, valid);

        var lvlo = Assert.Single(encoded.Subrecords, s => s.Signature == "LVLO");
        // LVLO bytes 4-7 hold FormID
        Assert.Equal(0x00000001u, BinaryPrimitives.ReadUInt32LittleEndian(lvlo.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void LvliEncodeNew_emits_LVLO_verbatim_when_no_validFormIds_supplied()
    {
        var lvli = new LeveledListRecord
        {
            FormId = 0x01000200, EditorId = "T", ListType = "LVLI",
            Entries = [new LeveledEntry(1, 0xDEADBEEFu, 1)]
        };

        var encoded = LvliEncoder.EncodeNew(lvli);

        var lvlo = Assert.Single(encoded.Subrecords, s => s.Signature == "LVLO");
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(lvlo.Bytes.AsSpan(4, 4)));
    }

    // ---------- WeapEncoder ----------

    [Fact]
    public void WeapEncodeNew_skips_NAM0_when_ammo_dangles()
    {
        var weap = MakeWeap(ammo: 0x000DEAD1u);
        var valid = new HashSet<uint>();

        var encoded = WeapEncoder.EncodeNew(weap, valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "NAM0"));
        Assert.Contains(encoded.Warnings, w => w.Contains("NAM0"));
    }

    [Fact]
    public void WeapEncodeNew_zeros_DNAM_projectile_when_dangling()
    {
        var weap = MakeWeap(projectile: 0x000DEAD1u);
        var valid = new HashSet<uint>();

        var encoded = WeapEncoder.EncodeNew(weap, valid);

        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM");
        // DNAM projectile is at bytes 36-39
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(dnam.Bytes.AsSpan(36, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("projectile") && w.Contains("zeroed"));
    }

    [Fact]
    public void WeapEncodeNew_remaps_DNAM_projectile_via_alias_table()
    {
        var weap = MakeWeap(projectile: 0x01999AAAu);
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = WeapEncoder.EncodeNew(weap, valid, remap);

        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM");
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(dnam.Bytes.AsSpan(36, 4)));
    }

    [Fact]
    public void WeapEncodeNew_emits_NAM0_when_ammo_valid()
    {
        var weap = MakeWeap(ammo: 0x000ED239u);
        var valid = new HashSet<uint> { 0x000ED239u };

        var encoded = WeapEncoder.EncodeNew(weap, valid);

        var nam0 = Assert.Single(encoded.Subrecords, s => s.Signature == "NAM0");
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(nam0.Bytes));
    }

    // ---------- NpcEncoder Phase 8a SCRI / CNTO ----------

    [Fact]
    public void NpcEncodeNew_drops_dangling_CNTO_inventory()
    {
        var npc = MakeNpc(inventory:
        [
            new InventoryItem(0x00000001u, 1),
            new InventoryItem(0x000DEAD1u, 1)
        ]);
        var validFormIds = new HashSet<uint> { 0x00000001u };

        var encoded = NpcEncoder.EncodeNew(npc, validFormIds: validFormIds);

        Assert.Single(encoded.Subrecords, s => s.Signature == "CNTO");
        Assert.Contains(encoded.Warnings, w => w.Contains("CNTO"));
    }

    [Fact]
    public void NpcEncodeNew_skips_SCRI_when_script_dangles()
    {
        var npc = MakeNpc(script: 0x000DEAD1u);
        var validFormIds = new HashSet<uint>();

        var encoded = NpcEncoder.EncodeNew(npc, validFormIds: validFormIds);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "SCRI"));
        Assert.Contains(encoded.Warnings, w => w.Contains("SCRI"));
    }

    [Fact]
    public void NpcEncodeNew_skips_SPLO_with_dangling_spell()
    {
        var npc = MakeNpc(spells: [0x000ED239u, 0x000DEAD1u]);
        var validFormIds = new HashSet<uint> { 0x000ED239u };

        var encoded = NpcEncoder.EncodeNew(npc, validFormIds: validFormIds);

        var splos = encoded.Subrecords.Where(s => s.Signature == "SPLO").ToList();
        Assert.Single(splos);
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(splos[0].Bytes));
    }

    // ---------- Helpers ----------

    private static WeaponRecord MakeWeap(uint? ammo = null, uint? projectile = null)
    {
        return new WeaponRecord
        {
            FormId = 0x01000300,
            EditorId = "TestWeap",
            FullName = "Test Weapon",
            ModelPath = "Weapons/Test.NIF",
            Damage = 10,
            Speed = 1f,
            Reach = 1f,
            ClipSize = 10,
            AmmoFormId = ammo,
            ProjectileFormId = projectile,
            EquipmentType = EquipmentType.None
        };
    }

    private static NpcRecord MakeNpc(
        List<InventoryItem>? inventory = null,
        uint? script = null,
        uint[]? spells = null)
    {
        return new NpcRecord
        {
            FormId = 0x01000400,
            EditorId = "TestNpc",
            FullName = "Test",
            Race = 0x00019C5Fu,
            Inventory = inventory ?? [],
            Script = script,
            Spells = spells?.ToList() ?? []
        };
    }
}
