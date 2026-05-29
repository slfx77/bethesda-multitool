using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class PerkReferenceWalkerTests
{
    [Fact]
    public void Typed_Form_Id_Parameters_Yield_Per_Index_Paths()
    {
        var perk = new PerkRecord
        {
            FormId = 0x000ABCDE,
            Conditions =
            [
                new PerkCondition
                {
                    FunctionIndex = 0x1C1, // HasPerk
                    Parameter1FormId = 0x000A0001,
                },
                new PerkCondition
                {
                    FunctionIndex = 0x0E,
                    Parameter1 = 5, // ActorValue index — untyped, must NOT yield.
                },
                new PerkCondition
                {
                    FunctionIndex = 0x47,
                    Parameter1FormId = 0x000A0002,
                    Parameter2FormId = 0x000A0003,
                },
            ],
        };
        var walker = new PerkReferenceWalker();

        var refs = walker.Walk(perk).ToList();

        Assert.Contains(refs, r => r.FieldPath == "CTDA[0].Parameter1" && r.FormId == 0x000A0001);
        Assert.DoesNotContain(refs, r => r.FieldPath == "CTDA[1].Parameter1");
        Assert.Contains(refs, r => r.FieldPath == "CTDA[2].Parameter1" && r.FormId == 0x000A0002);
        Assert.Contains(refs, r => r.FieldPath == "CTDA[2].Parameter2" && r.FormId == 0x000A0003);
    }

    [Fact]
    public void No_Conditions_Yields_No_References()
    {
        var perk = new PerkRecord { FormId = 0x000ABCDE };
        var walker = new PerkReferenceWalker();

        Assert.Empty(walker.Walk(perk));
    }
}
