using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class CreatureReferenceWalkerTests
{
    [Fact]
    public void Creature_Specific_Pointers_Yield_Cscr_Lnam_Cnam_Pnam()
    {
        var crea = new CreatureRecord
        {
            FormId = 0x000ABCDE,
            InheritsSoundsFrom = 0x000A0001,
            DeathItemLootList = 0x000A0002,
            ImpactDataSet = 0x000A0003,
            BodyData = 0x000A0004,
        };
        var walker = new CreatureReferenceWalker();

        var refs = walker.Walk(crea).ToList();
        var byPath = refs.ToDictionary(r => r.FieldPath, r => r.FormId);

        Assert.Equal(0x000A0001u, byPath["CSCR"]);
        Assert.Equal(0x000A0002u, byPath["LNAM"]);
        Assert.Equal(0x000A0003u, byPath["CNAM"]);
        Assert.Equal(0x000A0004u, byPath["PNAM"]);
    }

    [Fact]
    public void Eitm_Yielded_Only_When_Set()
    {
        var crea = new CreatureRecord { FormId = 0x000ABCDE, EquippedItem = 0x000A0005 };
        var walker = new CreatureReferenceWalker();

        var refs = walker.Walk(crea).ToList();

        Assert.Contains(refs, r => r.FieldPath == "EITM" && r.FormId == 0x000A0005);
    }

    [Fact]
    public void Inventory_With_Coed_Owner_Yields_Both_Paths()
    {
        var crea = new CreatureRecord
        {
            FormId = 0x000ABCDE,
            Inventory = [new InventoryItem(0x000A0001, 2) { OwnerFormId = 0x000A0099 }],
        };
        var walker = new CreatureReferenceWalker();

        var refs = walker.Walk(crea).ToList();

        Assert.Contains(refs, r => r.FieldPath == "CNTO[0].Item" && r.FormId == 0x000A0001);
        Assert.Contains(refs, r => r.FieldPath == "COED[0].Owner" && r.FormId == 0x000A0099);
    }
}
