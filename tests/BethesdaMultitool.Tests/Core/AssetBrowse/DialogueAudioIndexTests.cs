using BethesdaMultitool.Core.Media.Audio.Dialogue;
using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Verifies original voice identity, ambiguous candidates, source provenance, and LIP companionship.</summary>
public sealed class DialogueAudioIndexTests
{
    /// <summary>Verifies equal local INFO IDs in different plugins never cross-match.</summary>
    [Fact]
    public void KeepsPluginIdentityWhenLocalFormIdsCollide()
    {
        var index = DialogueAudioIndex.Create([
            Entry("FalloutNV.esm", "MaleAdult01", "quest_topic_00001234_1.wav", "voices.bsa"),
            Entry("Other.esp", "MaleAdult01", "quest_topic_00001234_1.wav", "mod.bsa")], TestContext.Current.CancellationToken);
        var result = index.Resolve(new DialogueAudioIdentity("FALLOUTNV.ESM", 0x03001234, 1, "maleadult01"));
        Assert.Equal(DialogueAudioMatchState.Resolved, result.State);
        Assert.Equal("voices.bsa", Assert.Single(result.Candidates).Audio.Source);
    }

    /// <summary>Verifies voice, response, and provenance filters narrow identities without choosing a stem.</summary>
    [Fact]
    public void ReportsAmbiguityUntilVoiceAndProvenanceAreExplicit()
    {
        var index = DialogueAudioIndex.Create([
            Entry("Fallout3.esm", "Male", "topic_00001234_1.wav", "retail.bsa"),
            Entry("Fallout3.esm", "Male", "topic_00001234_1.wav", "prototype.bsa"),
            Entry("Fallout3.esm", "Female", "topic_00001234_1.wav", "retail.bsa"),
            Entry("Fallout3.esm", "Male", "topic_00001234_2.wav", "retail.bsa")], TestContext.Current.CancellationToken);
        Assert.Equal(3, index.Resolve(new DialogueAudioIdentity("Fallout3.esm", 0x1234, 1)).Candidates.Count);
        Assert.Equal(DialogueAudioMatchState.Ambiguous,
            index.Resolve(new DialogueAudioIdentity("Fallout3.esm", 0x1234, 1, "Male")).State);
        var exact = index.Resolve(new DialogueAudioIdentity("Fallout3.esm", 0x1234, 1, "Male", "prototype.bsa"));
        Assert.Equal("prototype.bsa", Assert.Single(exact.Candidates).Audio.Source);
        Assert.Equal(DialogueAudioMatchState.Missing,
            index.Resolve(new DialogueAudioIdentity("Fallout3.esm", 0x1234, 3)).State);
    }

    /// <summary>Verifies only same-stem LIP files accompany an audio entry and retain their own provenance.</summary>
    [Fact]
    public void PreservesLipCompanionsWithoutConflatingFilenameStems()
    {
        var audio = Entry("FalloutNV.esm", "Male", "quest_topic_00001234_1.mp3", "audio.bsa");
        var index = DialogueAudioIndex.Create([audio, audio,
            Entry("FalloutNV.esm", "Male", "quest_topic_00001234_1.lip", "lip.bsa"),
            Entry("FalloutNV.esm", "Male", "different_topic_00001234_1.lip", "other.bsa")], TestContext.Current.CancellationToken);
        var candidate = Assert.Single(index.Resolve(new DialogueAudioIdentity("FalloutNV.esm", 0x1234, 1)).Candidates);
        Assert.Equal("lip.bsa", Assert.Single(candidate.LipCompanions).Source);
    }

    /// <summary>Verifies malformed filenames and unrelated container formats do not become voice matches.</summary>
    [Fact]
    public void RejectsMalformedNamesAndObservesCancellationForEmptyInput()
    {
        var index = DialogueAudioIndex.Create([
            Entry("FalloutNV.esm", "Male", "topic_1234_1.wav", "voices.bsa"),
            Entry("FalloutNV.esm", "Male", "topic_00001234_256.wav", "voices.bsa"),
            Entry("FalloutNV.esm", "Male", "topic_00001234_1.fuz", "voices.bsa")], TestContext.Current.CancellationToken);
        Assert.Equal(DialogueAudioMatchState.Missing,
            index.Resolve(new DialogueAudioIdentity("FalloutNV.esm", 0x1234, 1)).State);
        Assert.Throws<OperationCanceledException>(() => DialogueAudioIndex.Create([], new CancellationToken(true)));
    }

    /// <summary>Constructs a source-relative synthetic voice entry without reading private payloads.</summary>
    private static GameFileEntry Entry(string plugin, string voice, string file, string source)
        => new($"sound/voice/{plugin}/{voice}/{file}", 12, source);
}
