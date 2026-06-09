using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.AssetPacking;

public class EngineVoicePathBuilderTests
{
    [Fact]
    public void Truncate_BothShort_PassThrough()
    {
        var (q, t) = EngineVoicePathBuilder.TruncateStemsForEngine("radionewvegas", "rnvnewsintro");
        Assert.Equal("radionewvegas", q);
        Assert.Equal("rnvnewsintro", t);
    }

    [Fact]
    public void Truncate_LongTopic_TopicCappedAt15_QuestGetsRemainder()
    {
        var (q, t) = EngineVoicePathBuilder.TruncateStemsForEngine(
            "VDialogueUlysses", "VDialogueUlyssesUlyssesTopic000");
        Assert.Equal("VDialogueUlysse", t);
        Assert.Equal("VDialogueU", q);
        Assert.Equal(26, q.Length + 1 + t.Length);
    }

    [Fact]
    public void Truncate_LongQuestShortTopic_QuestUsesMostOfBudget()
    {
        var (q, t) = EngineVoicePathBuilder.TruncateStemsForEngine(
            "VeryLongQuestEditorIdName", "Hi");
        Assert.Equal("Hi", t);
        // budget for quest = 26 - 1 - 2 = 23
        Assert.Equal(23, q.Length);
        Assert.Equal("VeryLongQuestEditorIdNa", q);
    }

    [Fact]
    public void Build_AppliesLowercaseAndTruncation()
    {
        var path = EngineVoicePathBuilder.Build(
            "v48-xex43.esp",
            "MaleUniqueUlysses",
            "VDialogueUlysses",
            "VDialogueUlyssesUlyssesTopic000",
            0x0100352C,
            1,
            ".ogg");

        Assert.Equal(
            "sound\\voice\\v48-xex43.esp\\maleuniqueulysses\\vdialogueu_vdialogueulysse_0000352c_1.ogg",
            path);
    }

    [Fact]
    public void Build_MasksHighByteOfFormIdToBottom24()
    {
        // Engine path embeds the bottom 24 bits of the FormID; load-order byte is stripped.
        var path = EngineVoicePathBuilder.Build(
            "x.esp",
            "v",
            "Q",
            "T",
            0xFF123456u,
            2,
            ".wav");

        Assert.Contains("00123456_2.wav", path);
    }
}