using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     End-to-end verification that a Morrowind (TES3) plugin is read correctly through the real loader.
///     TES3 keeps its dedicated <c>Tes3RecordParser</c> (it produces the typed Cell/Worldspace/Land viewer
///     collections the TES4 family's schema path can't), but it is now <b>on the schema registry</b>: every
///     record carries a <c>DecodedTree</c> from the registered Tes3Schema (so the Records tab renders via
///     the same SchemaRecordDecoder as Oblivion→FO76), and DIAL/INFO are surfaced for the Dialogue tab.
///     TES3 dialogue is positional (INFO follows its DIAL in file order) and string-keyed (the ONAM speaker
///     is an NPC editor id), which this exercises. Skipped when Morrowind.esm isn't installed.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class Tes3SchemaParseIntegrationTests
{
    private static string? ResolveMorrowindEsm()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "Morrowind.esm")))
        {
            return Path.Combine(root, "Morrowind.esm");
        }

        string[] candidates =
        [
            @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Morrowind.esm",
            @"D:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Morrowind.esm"
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    [Fact]
    public async Task Morrowind_Npcs_Are_SchemaDecoded_With_Rich_Blocks()
    {
        var esm = ResolveMorrowindEsm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Morrowind.esm not found (set BETHESDA_TEST_DATA_ROOT or install Morrowind).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, cancellationToken: TestContext.Current.CancellationToken);

        var npcs = result.Records.GenericRecords.Where(r => r.RecordType == "NPC_").ToList();
        Assert.True(npcs.Count > 1000,
            $"Expected Morrowind NPC_ records as GenericRecords; got {npcs.Count}.");

        // The registered Tes3Schema must drive a DecodedTree on each record (the registry migration).
        var withTree = npcs.Where(n => n.DecodedTree is { Count: > 0 }).ToList();
        Assert.True(withTree.Count > 1000, $"Expected schema DecodedTrees on NPC_ records; got {withTree.Count}.");

        // A representative NPC_ decodes the labeled TES3 fields the GUI presents (Race/Class are required).
        var sample = withTree.First(n => n.DecodedTree!.Any(node => node.Label == "Race"));
        Assert.Contains(sample.DecodedTree!, n => n.Label == "Race");
        Assert.Contains(sample.DecodedTree!, n => n.Label == "Class");
        Assert.False(string.IsNullOrEmpty(sample.EditorId), "NPC_ should have an EditorID.");
    }

    [Fact]
    public async Task Morrowind_Dialogue_Is_Surfaced_For_The_Dialogue_Tab()
    {
        var esm = ResolveMorrowindEsm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Morrowind.esm not found (set BETHESDA_TEST_DATA_ROOT or install Morrowind).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, cancellationToken: TestContext.Current.CancellationToken);

        // DIAL topics and INFO responses must be built so the Dialogue tab has data.
        Assert.True(result.Records.DialogTopics.Count > 1000,
            $"Expected Morrowind DIAL topics; got {result.Records.DialogTopics.Count}.");
        Assert.True(result.Records.Dialogues.Count > 10000,
            $"Expected Morrowind INFO records; got {result.Records.Dialogues.Count}.");

        // INFOs are linked to their topic positionally and carry the NAME response text.
        Assert.Contains(result.Records.Dialogues, d => d.TopicFormId is > 0);
        Assert.Contains(result.Records.Dialogues,
            d => d.Responses.Any(r => !string.IsNullOrEmpty(r.Text)));

        // Morrowind has no quests, so the assembled tree carries its topics as OrphanTopics.
        Assert.NotNull(result.Records.DialogueTree);
        Assert.NotEmpty(result.Records.DialogueTree!.OrphanTopics);

        // Speaker attribution resolves the ONAM/FNAM/RNAM editor-id strings to synthetic FormIDs.
        var withSpeaker = result.Records.Dialogues.Count(d => d.SpeakerFormId is > 0);
        Assert.True(withSpeaker > 5000,
            $"Expected many Morrowind INFOs to attribute a speaker via ONAM; got {withSpeaker}.");
    }
}
