using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.AssetPacking;

public class DialogueAudioPrefixMatchTests
{
    [Fact]
    public void BuildPrefixIndex_BucketsBindingsByVoiceTopicPrefixResp()
    {
        var bindings = new[]
        {
            Make(0x0100352C, "VDialogueUlyssesUlyssesTopic000", "MaleUniqueUlysses", 1, "VDialogueUlysses", "Text A"),
            Make(0x0100352D, "VDialogueUlyssesUlyssesTopic001", "MaleUniqueUlysses", 1, "VDialogueUlysses", "Text B"),
            Make(0x0100352E, "VDialogueUlyssesOtherTopic",      "MaleUniqueUlysses", 1, "VDialogueUlysses", "Text C")
        };

        var index = DialogueAudioCsvAssetCollector.BuildAudioBindingPrefixIndex(bindings);

        // All three collapse to the same 15-char prefix `vdialogueulysse`.
        var key = ("maleuniqueulysses", "vdialogueulysse", (byte)1);
        Assert.True(index.TryGetValue(key, out var bucket));
        Assert.Equal(3, bucket!.Count);
    }

    [Fact]
    public void PickBestPrefixCandidate_ExactTextWins()
    {
        var candidates = new[]
        {
            Make(0xAA, "T", "V", 1, "Q", "I see what you did there."),
            Make(0xBB, "T", "V", 1, "Q", "Nothing to see here.")
        };

        var picked = DialogueAudioCsvAssetCollector.PickBestPrefixCandidate(
            candidates, "Nothing to see here.");
        Assert.NotNull(picked);
        Assert.Equal(0xBBu, picked!.AllocatedInfoFormId);
    }

    [Fact]
    public void PickBestPrefixCandidate_PunctuationDriftStillMatches()
    {
        var candidates = new[]
        {
            Make(0xAA, "T", "V", 1, "Q", "Watch yourself, then.")
        };

        // CSV row has the same text but with double-dot ellipsis from a different era;
        // normalization strips punctuation so it should still match.
        var picked = DialogueAudioCsvAssetCollector.PickBestPrefixCandidate(
            candidates, "Watch yourself ... then.");
        Assert.NotNull(picked);
        Assert.Equal(0xAAu, picked!.AllocatedInfoFormId);
    }

    [Fact]
    public void PickBestPrefixCandidate_NoTextOverlap_ReturnsNull()
    {
        var candidates = new[]
        {
            Make(0xAA, "T", "V", 1, "Q", "First line of dialogue here."),
            Make(0xBB, "T", "V", 1, "Q", "Second line of dialogue here.")
        };

        var picked = DialogueAudioCsvAssetCollector.PickBestPrefixCandidate(
            candidates, "Completely different sentence.");
        Assert.Null(picked);
    }

    [Fact]
    public void PickBestPrefixCandidate_SingleCandidate_BypassesTextCheck()
    {
        var candidates = new[]
        {
            Make(0xAA, "T", "V", 1, "Q", "Something")
        };

        // Even with garbage CSV text, a unique bucket should match — there's only one
        // possible target, so disambiguation is unnecessary.
        var picked = DialogueAudioCsvAssetCollector.PickBestPrefixCandidate(candidates, "x");
        Assert.NotNull(picked);
        Assert.Equal(0xAAu, picked!.AllocatedInfoFormId);
    }

    private static EmittedDialogueAudioBinding Make(
        uint fid, string topic, string voice, byte resp, string quest, string text)
        => new()
        {
            AllocatedInfoFormId = fid,
            ParentDialEditorId = topic,
            VoiceTypeEditorId = voice,
            ResponseNumber = resp,
            QuestEditorId = quest,
            ResponseText = text
        };
}
