using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class TerminalReferenceWalkerTests
{
    [Fact]
    public void Top_Level_References_Yield_Canonical_Subrecord_Paths()
    {
        var terminal = new TerminalRecord
        {
            FormId = 0x000ABCDE,
            ScriptFormId = 0x000A0001,
            SoundLoopFormId = 0x000A0002,
            PasswordNoteFormId = 0x000A0003
        };

        var refs = new TerminalReferenceWalker().Walk(terminal).ToList();

        Assert.Collection(
            refs,
            reference => AssertReference(reference, "SCRI", 0x000A0001),
            reference => AssertReference(reference, "SNAM", 0x000A0002),
            reference => AssertReference(reference, "PNAM", 0x000A0003));
    }

    [Fact]
    public void Menu_References_Include_Links_Conditions_And_Ordered_Scro_But_Not_Scrv()
    {
        var terminal = new TerminalRecord
        {
            FormId = 0x000ABCDE,
            MenuItems =
            [
                new TerminalMenuItem
                {
                    DisplayNoteFormId = 0x000A0010,
                    SubTerminal = 0x000A0011,
                    ReferencedObjects = [0x000A0012, 0x80000005, 0x000A0013],
                    Conditions =
                    [
                        new DialogueCondition { Reference = 0x000A0014 },
                        new DialogueCondition { Reference = 0 }
                    ]
                },
                new TerminalMenuItem
                {
                    ReferencedObjects = [0x80000006, 0x000A0015],
                    Conditions = [new DialogueCondition { Reference = 0x000A0016 }]
                }
            ]
        };

        var refs = new TerminalReferenceWalker().Walk(terminal).ToList();

        Assert.Collection(
            refs,
            reference => AssertReference(reference, "MenuItems[0].INAM", 0x000A0010),
            reference => AssertReference(reference, "MenuItems[0].TNAM", 0x000A0011),
            reference => AssertReference(reference, "MenuItems[0].SCRO[0]", 0x000A0012),
            reference => AssertReference(reference, "MenuItems[0].SCRO[2]", 0x000A0013),
            reference => AssertReference(reference, "MenuItems[0].CTDA[0].Reference", 0x000A0014),
            reference => AssertReference(reference, "MenuItems[1].SCRO[1]", 0x000A0015),
            reference => AssertReference(reference, "MenuItems[1].CTDA[0].Reference", 0x000A0016));
        Assert.DoesNotContain(refs, reference => (reference.FormId & 0x80000000u) != 0);
        Assert.DoesNotContain(refs, reference => reference.FieldPath == "MenuItems[0].CTDA[1].Reference");
    }

    [Fact]
    public void Menu_Conditions_Expose_Quest_And_Script_Variable_Form_Parameters()
    {
        var terminal = new TerminalRecord
        {
            FormId = 0x000ABCDE,
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            FunctionIndex = 79, // GetQuestVariable
                            Parameter1 = 0x000A0020,
                            Parameter2 = 7,
                        },
                        new DialogueCondition
                        {
                            FunctionIndex = 53, // GetScriptVariable
                            Parameter1 = 0x000A0021,
                            Parameter2 = 8,
                        }
                    ]
                }
            ]
        };

        var refs = new TerminalReferenceWalker().Walk(terminal).ToList();

        Assert.Collection(
            refs,
            reference => AssertReference(
                reference,
                "MenuItems[0].CTDA[0].Parameter1",
                0x000A0020),
            reference => AssertReference(
                reference,
                "MenuItems[0].CTDA[1].Parameter1",
                0x000A0021));
        Assert.DoesNotContain(refs, reference => reference.FieldPath.EndsWith(".Parameter2"));
    }

    [Fact]
    public void Empty_Or_Non_Terminal_Model_Yields_No_References()
    {
        var walker = new TerminalReferenceWalker();

        Assert.Empty(walker.Walk(new TerminalRecord { FormId = 0x000ABCDE }));
        Assert.Empty(walker.Walk(new object()));
    }

    private static void AssertReference(RawReference reference, string fieldPath, uint formId)
    {
        Assert.Equal(fieldPath, reference.FieldPath);
        Assert.Equal(formId, reference.FormId);
    }
}
