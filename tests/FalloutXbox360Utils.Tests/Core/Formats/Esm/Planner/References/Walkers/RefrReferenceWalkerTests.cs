using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class RefrReferenceWalkerTests
{
    [Fact]
    public void Always_Yields_Base_Form_Id_As_NAME()
    {
        var placed = new PlacedReference { FormId = 0xAA000001, BaseFormId = 0x000ABCDE };
        var walker = new RefrReferenceWalker();

        var refs = walker.Walk(placed).ToList();

        var nameRef = Assert.Single(refs.Where(r => r.FieldPath == "NAME"));
        Assert.Equal(0x000ABCDEu, nameRef.FormId);
    }

    [Fact]
    public void Skips_Optional_Refs_When_Null()
    {
        var placed = new PlacedReference { FormId = 0xAA000001, BaseFormId = 0x000ABCDE };
        var walker = new RefrReferenceWalker();

        var refs = walker.Walk(placed).ToList();

        Assert.Single(refs); // Only NAME.
        Assert.DoesNotContain(refs, r => r.FieldPath == "XOWN");
        Assert.DoesNotContain(refs, r => r.FieldPath == "XEZN");
    }

    [Fact]
    public void Emits_Optional_Refs_When_Set()
    {
        var placed = new PlacedReference
        {
            FormId = 0xAA000001,
            BaseFormId = 0x000ABCDE,
            OwnerFormId = 0x000ABCD0,
            EncounterZoneFormId = 0x000ABCD1,
            EnableParentFormId = 0x000ABCD2,
            DestinationDoorFormId = 0x000ABCD3,
            LockKeyFormId = 0x000ABCD4,
            LinkedRefFormId = 0x000ABCD5,
            LinkedRefKeywordFormId = 0x000ABCD6,
        };
        var walker = new RefrReferenceWalker();

        var refs = walker.Walk(placed).ToList();
        var byPath = refs.ToDictionary(r => r.FieldPath, r => r.FormId);

        Assert.Equal(0x000ABCD0u, byPath["XOWN"]);
        Assert.Equal(0x000ABCD1u, byPath["XEZN"]);
        Assert.Equal(0x000ABCD2u, byPath["XESP"]);
        Assert.Equal(0x000ABCD3u, byPath["XTEL.DestinationDoor"]);
        Assert.Equal(0x000ABCD4u, byPath["XLOC.LockKey"]);
        Assert.Equal(0x000ABCD5u, byPath["XLKR"]);
        Assert.Equal(0x000ABCD6u, byPath["XLKR.Keyword"]);
    }
}
