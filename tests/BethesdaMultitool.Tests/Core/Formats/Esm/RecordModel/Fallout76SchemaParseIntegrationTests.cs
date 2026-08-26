using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     End-to-end verification that a Fallout 76 (FO76) plugin is read correctly through the real loader:
///     the game is detected, dispatched to the schema-driven parser, every NPC_ comes back as a
///     <c>GenericEsmRecord</c> with a decoded field tree (rich blocks), and the localized DIAL/INFO
///     dialogue is surfaced for the Dialogue tab. FO76 ships its string tables <i>inside</i>
///     <c>SeventySix - Localization.ba2</c>, so these assertions also prove the loader resolves localized
///     names/text from a BA2 (not loose .STRINGS). FO76 reuses the FO4 dialogue extractor (identical INFO
///     layout). Skipped when SeventySix.esm isn't installed.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public class Fallout76SchemaParseIntegrationTests
{
    [Fact]
    public async Task Fallout76_Npcs_Are_SchemaDecoded_With_Rich_Blocks()
    {
        var esm = RealAssetPaths.Masters.SeventySix();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "SeventySix.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout 76).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        var npcs = result.Records.GenericRecords.Where(r => r.RecordType == "NPC_").ToList();
        Assert.True(npcs.Count > 1000,
            $"Expected the schema-driven parser to surface NPC_ records as GenericRecords; got {npcs.Count}. " +
            "If 0, the game was not detected as Fallout76 or the schema dispatch did not fire.");

        var withTree = npcs.Where(n => n.DecodedTree is { Count: > 0 }).ToList();
        Assert.True(withTree.Count > 1000, $"Expected decoded field trees on NPC_ records; got {withTree.Count}.");

        // A representative NPC_ must decode the rich, labeled blocks the GUI presents.
        var sample = withTree.First(n => n.DecodedTree!.Any(node => node.Label == "Configuration"));
        Assert.Contains(sample.DecodedTree!, n => n.Label == "Configuration"); // ACBS
        Assert.False(string.IsNullOrEmpty(sample.EditorId), "NPC_ should have an EditorID.");

        // Localized display names resolved from the BA2-hosted .STRINGS table, not raw ids.
        Assert.Contains(npcs, n => !string.IsNullOrEmpty(n.FullName) && n.FullName.All(c => c != '�'));
    }

    [Fact]
    public async Task Fallout76_Dialogue_Is_Surfaced_For_The_Dialogue_Tab()
    {
        var esm = RealAssetPaths.Masters.SeventySix();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "SeventySix.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout 76).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        // DIAL topics and INFO responses must be built game-aware so the Dialogue tab has data.
        Assert.True(result.Records.DialogTopics.Count > 1000,
            $"Expected FO76 DIAL topics; got {result.Records.DialogTopics.Count}.");
        Assert.True(result.Records.Dialogues.Count > 10000,
            $"Expected FO76 INFO records; got {result.Records.Dialogues.Count}.");

        // INFOs must link to their parent topic (GRUP-based) and carry localized response text (NAM1).
        Assert.Contains(result.Records.Dialogues, d => d.TopicFormId is > 0);
        Assert.Contains(result.Records.Dialogues,
            d => d.Responses.Any(r => !string.IsNullOrEmpty(r.Text)));

        // The Dialogue tab consumes the assembled tree; it must group topics under quests (DIAL QNAM).
        Assert.NotNull(result.Records.DialogueTree);
        Assert.NotEmpty(result.Records.DialogueTree!.QuestTrees);

        // Speaker attribution is direct in FO76 (INFO ANAM → NPC_), with CTDA as a fallback.
        var withSpeaker = result.Records.Dialogues.Count(d => d.SpeakerFormId is > 0);
        Assert.True(withSpeaker > 5000,
            $"Expected many FO76 INFOs to attribute an NPC speaker via ANAM/CTDA; got {withSpeaker}.");
    }
}