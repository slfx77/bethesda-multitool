using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     End-to-end verification that a Skyrim (TES5) plugin is read correctly through the real loader:
///     the game is detected, dispatched to the schema-driven parser, every NPC_ comes back as a
///     <c>GenericEsmRecord</c> with a decoded field tree (rich blocks), and the localized DIAL/INFO
///     dialogue is surfaced for the Dialogue tab. Skyrim is the first localized game on this path — its
///     names and response text live in external .STRINGS/.ILSTRINGS tables, so these assertions also
///     prove the loader joins those. Skipped when Skyrim.esm isn't installed.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class SkyrimSchemaParseIntegrationTests
{
    private static string? ResolveSkyrimEsm()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "Skyrim.esm")))
        {
            return Path.Combine(root, "Skyrim.esm");
        }

        // Prefer the original (LE) install — the build we hold symbols for — then Special Edition.
        string[] candidates =
        [
            @"E:\SteamLibrary\SteamApps\common\Skyrim\Data\Skyrim.esm",
            @"E:\SteamLibrary\SteamApps\common\Skyrim Special Edition\Data\Skyrim.esm"
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    [Fact]
    public async Task Skyrim_Npcs_Are_SchemaDecoded_With_Rich_Blocks()
    {
        var esm = ResolveSkyrimEsm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Skyrim.esm not found (set BETHESDA_TEST_DATA_ROOT or install Skyrim).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        var npcs = result.Records.GenericRecords.Where(r => r.RecordType == "NPC_").ToList();
        Assert.True(npcs.Count > 1000,
            $"Expected the schema-driven parser to surface NPC_ records as GenericRecords; got {npcs.Count}. " +
            "If 0, the game was not detected as Skyrim or the schema dispatch did not fire.");

        var withTree = npcs.Where(n => n.DecodedTree is { Count: > 0 }).ToList();
        Assert.True(withTree.Count > 1000, $"Expected decoded field trees on NPC_ records; got {withTree.Count}.");

        // A representative NPC_ must decode the rich, labeled blocks the GUI presents.
        var sample = withTree.First(n => n.DecodedTree!.Any(node => node.Label == "Configuration"));
        Assert.Contains(sample.DecodedTree!, n => n.Label == "Configuration"); // ACBS
        Assert.False(string.IsNullOrEmpty(sample.EditorId), "NPC_ should have an EditorID.");

        // Localized display names (external .STRINGS) must be resolved, not raw string-table ids.
        Assert.Contains(npcs, n => !string.IsNullOrEmpty(n.FullName) && n.FullName.All(c => c != '�'));
    }

    [Fact]
    public async Task Skyrim_Dialogue_Is_Surfaced_For_The_Dialogue_Tab()
    {
        var esm = ResolveSkyrimEsm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Skyrim.esm not found (set BETHESDA_TEST_DATA_ROOT or install Skyrim).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        // DIAL topics and INFO responses must be built game-aware so the Dialogue tab has data.
        Assert.True(result.Records.DialogTopics.Count > 1000,
            $"Expected Skyrim DIAL topics; got {result.Records.DialogTopics.Count}.");
        Assert.True(result.Records.Dialogues.Count > 10000,
            $"Expected Skyrim INFO records; got {result.Records.Dialogues.Count}.");

        // INFOs must link to their parent topic (GRUP-based) and carry localized response text.
        Assert.Contains(result.Records.Dialogues, d => d.TopicFormId is > 0);
        Assert.Contains(result.Records.Dialogues,
            d => d.Responses.Any(r => !string.IsNullOrEmpty(r.Text)));

        // The Dialogue tab consumes the assembled tree; it must group topics under quests (DIAL QNAM).
        Assert.NotNull(result.Records.DialogueTree);
        Assert.NotEmpty(result.Records.DialogueTree!.QuestTrees);

        // Speaker attribution is direct in Skyrim (INFO ANAM → NPC_), with CTDA as a fallback.
        var withSpeaker = result.Records.Dialogues.Count(d => d.SpeakerFormId is > 0);
        Assert.True(withSpeaker > 5000,
            $"Expected many Skyrim INFOs to attribute an NPC speaker via ANAM/CTDA; got {withSpeaker}.");
    }
}