using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class InfoReferenceWalkerTests
{
    [Fact]
    public void Quest_And_Speaker_Refs_Yielded_When_Set()
    {
        var info = new DialogueRecord
        {
            FormId = 0x000ABCDE,
            QuestFormId = 0x000ABCD0,
            SpeakerFormId = 0x000ABCD1,
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
            LinkFromTopics = [0x000A0003],
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
                new DialogueResultScript { ReferencedObjects = [0x000A0003] },
            ],
        };
        var walker = new InfoReferenceWalker();

        var refs = walker.Walk(info).ToList();

        Assert.Contains(refs, r => r.FieldPath == "ResultScripts[0].SCRO[0]" && r.FormId == 0x000A0001);
        Assert.Contains(refs, r => r.FieldPath == "ResultScripts[0].SCRO[1]" && r.FormId == 0x000A0002);
        Assert.Contains(refs, r => r.FieldPath == "ResultScripts[1].SCRO[0]" && r.FormId == 0x000A0003);
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
            PreviousInfo = 0,
        };
        var walker = new InfoReferenceWalker();

        var refs = walker.Walk(info).ToList();

        Assert.Empty(refs);
    }
}
