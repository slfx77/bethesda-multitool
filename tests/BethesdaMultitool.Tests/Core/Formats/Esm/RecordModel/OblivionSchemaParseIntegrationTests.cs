using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     End-to-end verification that an Oblivion (TES4) plugin is read correctly through the real loader:
///     the game is detected, dispatched to the schema-driven parser, and every NPC_ comes back as a
///     <c>GenericEsmRecord</c> carrying a decoded field tree with the rich blocks (Configuration / Stats).
///     This is the "reads properly in the GUI" guarantee at the data layer — the GUI consumes the same
///     <c>RecordCollection</c>. Skipped when Oblivion.esm isn't installed.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public class OblivionSchemaParseIntegrationTests
{
    [Fact]
    public async Task Oblivion_Npcs_Are_SchemaDecoded_With_Rich_Blocks()
    {
        var esm = RealAssetPaths.Masters.Oblivion();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Oblivion.esm not found (set BETHESDA_TEST_DATA_ROOT or install Oblivion).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        var npcs = result.Records.GenericRecords.Where(r => r.RecordType == "NPC_").ToList();
        Assert.True(npcs.Count > 1000,
            $"Expected the schema-driven parser to surface NPC_ records as GenericRecords; got {npcs.Count}. " +
            "If 0, the game was not detected as Oblivion or the schema dispatch did not fire.");

        var withTree = npcs.Where(n => n.DecodedTree is { Count: > 0 }).ToList();
        Assert.True(withTree.Count > 1000, $"Expected decoded field trees on NPC_ records; got {withTree.Count}.");

        // A representative NPC_ must decode the rich, labeled blocks the GUI presents.
        var sample = withTree.First(n => n.DecodedTree!.Any(node => node.Label == "Stats"));
        Assert.Contains(sample.DecodedTree!, n => n.Label == "Configuration");
        Assert.False(string.IsNullOrEmpty(sample.EditorId), "NPC_ should have an EditorID.");

        var stats = sample.DecodedTree!.First(n => n.Label == "Stats");
        Assert.Contains(stats.Children, c => c.Label == "Strength");
        Assert.Contains(stats.Children, c => c.Label == "Luck");
        Assert.Contains(stats.Children, c => c.Label == "Health");
    }

    [Fact]
    public async Task Oblivion_Dialogue_Is_Surfaced_For_The_Dialogue_Tab()
    {
        var esm = RealAssetPaths.Masters.Oblivion();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Oblivion.esm not found (set BETHESDA_TEST_DATA_ROOT or install Oblivion).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        // DIAL topics and INFO responses must be built game-aware so the Dialogue tab has data.
        Assert.True(result.Records.DialogTopics.Count > 1000,
            $"Expected Oblivion DIAL topics; got {result.Records.DialogTopics.Count}.");
        Assert.True(result.Records.Dialogues.Count > 10000,
            $"Expected Oblivion INFO records; got {result.Records.Dialogues.Count}.");

        // INFOs must link to their parent topic (GRUP-based) and carry response text.
        Assert.Contains(result.Records.Dialogues, d => d.TopicFormId is > 0);
        Assert.Contains(result.Records.Dialogues,
            d => d.Responses.Any(r => !string.IsNullOrEmpty(r.Text)));

        // The Dialogue tab consumes the assembled tree; it must group topics under quests.
        Assert.NotNull(result.Records.DialogueTree);
        Assert.NotEmpty(result.Records.DialogueTree!.QuestTrees);

        // Speaker attribution (CTDA GetIsID/GetInFaction/GetIsRace) powers the NPC-grouped view.
        var withSpeaker = result.Records.Dialogues.Count(d => d.SpeakerFormId is > 0);
        Assert.True(withSpeaker > 5000,
            $"Expected most Oblivion INFOs to attribute a speaker via CTDA; got {withSpeaker}.");
    }
}