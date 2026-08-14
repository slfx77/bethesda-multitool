using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class InfoReferenceWalkerTests
{
    [Fact]
    public void Quest_And_Speaker_Refs_Yielded_When_Set()
    {
        var info = new DialogueRecord
        {
            FormId = 0x000ABCDE,
            QuestFormId = 0x000ABCD0,
            SpeakerFormId = 0x000ABCD1
        };
        var walker = new InfoReferenceWalker();

        var refs = walker.Walk(info).ToList();

        Assert.Contains(refs, r => r.FieldPath == "QSTI" && r.FormId == 0x000ABCD0);
        Assert.Contains(refs, r => r.FieldPath == "ANAM" && r.FormId == 0x000ABCD1);
    }

    [Fact]
    public void Tclt_And_Tclf_Lists_Yield_Indexed_Paths()
    {
        var info = new DialogueRecord
        {
            FormId = 0x000ABCDE,
            LinkToTopics = [0x000A0001, 0x000A0002],
            LinkFromTopics = [0x000A0003]
        };
        var walker = new InfoReferenceWalker();

        var refs = walker.Walk(info).ToList();

        Assert.Contains(refs, r => r.FieldPath == "TCLT[0]" && r.FormId == 0x000A0001);
        Assert.Contains(refs, r => r.FieldPath == "TCLT[1]" && r.FormId == 0x000A0002);
        Assert.Contains(refs, r => r.FieldPath == "TCLF[0]" && r.FormId == 0x000A0003);
    }

    [Fact]
    public void Result_Script_Scro_Yields_Per_Block_And_Per_Index()
    {
        var info = new DialogueRecord
        {
            FormId = 0x000ABCDE,
            ResultScripts =
            [
                new DialogueResultScript { ReferencedObjects = [0x000A0001, 0x000A0002] },
                new DialogueResultScript { ReferencedObjects = [0x000A0003] }
            ]
        };
        var walker = new InfoReferenceWalker();

        var refs = walker.Walk(info).ToList();

        Assert.Contains(refs, r => r.FieldPath == "ResultScripts[0].SCRO[0]" && r.FormId == 0x000A0001);
        Assert.Contains(refs, r => r.FieldPath == "ResultScripts[0].SCRO[1]" && r.FormId == 0x000A0002);
        Assert.Contains(refs, r => r.FieldPath == "ResultScripts[1].SCRO[0]" && r.FormId == 0x000A0003);
    }

    [Fact]
    public void Conditions_Yield_Only_Semantic_Fnv_Reference()
    {
        var info = new DialogueRecord
        {
            Conditions =
            [
                new DialogueCondition { RunOn = 2, Reference = 0x000A0001 },
                new DialogueCondition { RunOn = 4, Reference = 0x000A0002 },
                new DialogueCondition { FunctionIndex = 0x011D, RunOn = 2, Reference = 0x000A0003 }
            ]
        };

        var refs = new InfoReferenceWalker().Walk(info).ToList();

        var reference = Assert.Single(refs);
        Assert.Equal("CTDA[0].Reference", reference.FieldPath);
        Assert.Equal(0x000A0001u, reference.FormId);
    }

    [Fact]
    public void Empty_Optional_Fields_Yield_Nothing()
    {
        var info = new DialogueRecord { FormId = 0x000ABCDE };
        var walker = new InfoReferenceWalker();

        var refs = walker.Walk(info).ToList();

        Assert.Empty(refs);
    }

    [Fact]
    public void Zero_Form_Ids_Are_Filtered()
    {
        var info = new DialogueRecord
        {
            FormId = 0x000ABCDE,
            QuestFormId = 0,
            SpeakerFormId = 0,
            PreviousInfo = 0
        };
        var walker = new InfoReferenceWalker();

        var refs = walker.Walk(info).ToList();

        Assert.Empty(refs);
    }
}
