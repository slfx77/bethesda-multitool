using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class NpcReferenceWalkerTests
{
    [Fact]
    public void Single_Pointer_Subrecords_Yielded_When_Set()
    {
        var npc = new NpcRecord
        {
            FormId = 0x000ABCDE,
            Race = 0x000ABCD0,
            Script = 0x000ABCD1,
            VoiceType = 0x000ABCD2,
            Template = 0x000ABCD3,
            CombatStyleFormId = 0x000ABCD4,
        };
        var walker = new NpcReferenceWalker();

        var refs = walker.Walk(npc).ToList();
        var byPath = refs.ToDictionary(r => r.FieldPath, r => r.FormId);

        Assert.Equal(0x000ABCD0u, byPath["RNAM"]);
        Assert.Equal(0x000ABCD1u, byPath["SCRI"]);
        Assert.Equal(0x000ABCD2u, byPath["VTCK"]);
        Assert.Equal(0x000ABCD3u, byPath["TPLT"]);
        Assert.Equal(0x000ABCD4u, byPath["ZNAM"]);
    }

    [Fact]
    public void Zero_And_Null_Optional_Pointers_Are_Skipped()
    {
        var npc = new NpcRecord
        {
            FormId = 0x000ABCDE,
            Race = 0,
            Script = null,
            Template = 0x000ABCD3,
        };
        var walker = new NpcReferenceWalker();

        var refs = walker.Walk(npc).ToList();

        Assert.DoesNotContain(refs, r => r.FieldPath == "RNAM");
        Assert.DoesNotContain(refs, r => r.FieldPath == "SCRI");
        Assert.Contains(refs, r => r.FieldPath == "TPLT" && r.FormId == 0x000ABCD3);
    }

    [Fact]
    public void Snam_Factions_Yield_Per_Index_Path()
    {
        var npc = new NpcRecord
        {
            FormId = 0x000ABCDE,
            Factions =
            [
                new FactionMembership(0x000A0001, 5),
                new FactionMembership(0x000A0002, 0),
            ],
        };
        var walker = new NpcReferenceWalker();

        var refs = walker.Walk(npc).ToList();

        Assert.Contains(refs, r => r.FieldPath == "SNAM[0].Faction" && r.FormId == 0x000A0001);
        Assert.Contains(refs, r => r.FieldPath == "SNAM[1].Faction" && r.FormId == 0x000A0002);
    }

    [Fact]
    public void Inventory_Yields_Cnto_Item_And_Optional_Coed_Owner()
    {
        var npc = new NpcRecord
        {
            FormId = 0x000ABCDE,
            Inventory =
            [
                new InventoryItem(0x000A0001, 1),
                new InventoryItem(0x000A0002, 3) { OwnerFormId = 0x000A0010 },
            ],
        };
        var walker = new NpcReferenceWalker();

        var refs = walker.Walk(npc).ToList();

        Assert.Contains(refs, r => r.FieldPath == "CNTO[0].Item" && r.FormId == 0x000A0001);
        Assert.DoesNotContain(refs, r => r.FieldPath == "COED[0].Owner");
        Assert.Contains(refs, r => r.FieldPath == "CNTO[1].Item" && r.FormId == 0x000A0002);
        Assert.Contains(refs, r => r.FieldPath == "COED[1].Owner" && r.FormId == 0x000A0010);
    }

    [Fact]
    public void Splo_Spells_Yield_Indexed_Paths()
    {
        var npc = new NpcRecord
        {
            FormId = 0x000ABCDE,
            Spells = [0x000A0001, 0x000A0002],
        };
        var walker = new NpcReferenceWalker();

        var refs = walker.Walk(npc).ToList();

        Assert.Contains(refs, r => r.FieldPath == "SPLO[0]" && r.FormId == 0x000A0001);
        Assert.Contains(refs, r => r.FieldPath == "SPLO[1]" && r.FormId == 0x000A0002);
    }

    [Fact]
    public void Head_Parts_Yield_Pnam_Indexed_Paths()
    {
        var npc = new NpcRecord
        {
            FormId = 0x000ABCDE,
            HeadPartFormIds = [0x000A0001, 0x000A0002],
        };
        var walker = new NpcReferenceWalker();

        var refs = walker.Walk(npc).ToList();

        Assert.Contains(refs, r => r.FieldPath == "PNAM[0]" && r.FormId == 0x000A0001);
        Assert.Contains(refs, r => r.FieldPath == "PNAM[1]" && r.FormId == 0x000A0002);
    }
}
