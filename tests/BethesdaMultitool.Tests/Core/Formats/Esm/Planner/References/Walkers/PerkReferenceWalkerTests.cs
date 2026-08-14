using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.References.Walkers;

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
                    Parameter1FormId = 0x000A0001
                },
                new PerkCondition
                {
                    FunctionIndex = 0x0E,
                    Parameter1 = 5 // ActorValue index — untyped, must NOT yield.
                },
                new PerkCondition
                {
                    FunctionIndex = 0x47,
                    Parameter1FormId = 0x000A0002,
                    Parameter2FormId = 0x000A0003
                }
            ],
            Entries =
            [
                new PerkEntry
                {
                    ConditionGroups =
                    [
                        new PerkConditionGroup
                        {
                            RunOn = -1,
                            Conditions =
                            [
                                new PerkCondition
                                {
                                    FunctionIndex = 0x1C1,
                                    Parameter1FormId = 0x000B0001,
                                    Parameter2FormId = 0x000B0002,
                                },
                                new PerkCondition
                                {
                                    FunctionIndex = 0x0E,
                                    Parameter1 = 6,
                                },
                            ],
                        },
                    ],
                },
            ]
        };
        var walker = new PerkReferenceWalker();

        var refs = walker.Walk(perk).ToList();

        Assert.Contains(refs, r => r.FieldPath == "CTDA[0].Parameter1" && r.FormId == 0x000A0001);
        Assert.DoesNotContain(refs, r => r.FieldPath == "CTDA[1].Parameter1");
        Assert.Contains(refs, r => r.FieldPath == "CTDA[2].Parameter1" && r.FormId == 0x000A0002);
        Assert.Contains(refs, r => r.FieldPath == "CTDA[2].Parameter2" && r.FormId == 0x000A0003);
        Assert.Contains(refs,
            r => r.FieldPath == "Entries[0].ConditionGroups[0].CTDA[0].Parameter1" &&
                 r.FormId == 0x000B0001);
        Assert.Contains(refs,
            r => r.FieldPath == "Entries[0].ConditionGroups[0].CTDA[0].Parameter2" &&
                 r.FormId == 0x000B0002);
        Assert.DoesNotContain(refs,
            r => r.FieldPath == "Entries[0].ConditionGroups[0].CTDA[1].Parameter1");
    }

    [Fact]
    public void No_Conditions_Yields_No_References()
    {
        var perk = new PerkRecord { FormId = 0x000ABCDE };
        var walker = new PerkReferenceWalker();

        Assert.Empty(walker.Walk(perk));
    }
}
