using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     End-to-end verification that a Fallout 4 (FO4) plugin is read correctly through the real loader:
///     the game is detected, dispatched to the schema-driven parser, every NPC_ comes back as a
///     <c>GenericEsmRecord</c> with a decoded field tree (rich blocks), and the localized DIAL/INFO
///     dialogue is surfaced for the Dialogue tab. FO4 shares Skyrim's localized framing but uses a
///     different INFO response struct (TRDA) and drops topic linking — these assertions prove the FO4
///     extractor + .STRINGS/.ILSTRINGS join still produce browsable records and grouped dialogue. Skipped
///     when Fallout4.esm isn't installed.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class Fallout4SchemaParseIntegrationTests
{
    private static string? ResolveFallout4Esm()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "Fallout4.esm")))
        {
            return Path.Combine(root, "Fallout4.esm");
        }

        string[] candidates =
        [
            @"E:\SteamLibrary\SteamApps\common\Fallout 4\Data\Fallout4.esm",
            @"D:\SteamLibrary\SteamApps\common\Fallout 4\Data\Fallout4.esm"
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    [Fact]
    public async Task Fallout4_Npcs_Are_SchemaDecoded_With_Rich_Blocks()
    {
        var esm = ResolveFallout4Esm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Fallout4.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout 4).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        var npcs = result.Records.GenericRecords.Where(r => r.RecordType == "NPC_").ToList();
        Assert.True(npcs.Count > 1000,
            $"Expected the schema-driven parser to surface NPC_ records as GenericRecords; got {npcs.Count}. " +
            "If 0, the game was not detected as Fallout4 or the schema dispatch did not fire.");

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
    public async Task Fallout4_Dialogue_Is_Surfaced_For_The_Dialogue_Tab()
    {
        var esm = ResolveFallout4Esm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Fallout4.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout 4).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        // DIAL topics and INFO responses must be built game-aware so the Dialogue tab has data.
        Assert.True(result.Records.DialogTopics.Count > 1000,
            $"Expected FO4 DIAL topics; got {result.Records.DialogTopics.Count}.");
        Assert.True(result.Records.Dialogues.Count > 10000,
            $"Expected FO4 INFO records; got {result.Records.Dialogues.Count}.");

        // INFOs must link to their parent topic (GRUP-based) and carry localized response text (NAM1).
        Assert.Contains(result.Records.Dialogues, d => d.TopicFormId is > 0);
        Assert.Contains(result.Records.Dialogues,
            d => d.Responses.Any(r => !string.IsNullOrEmpty(r.Text)));

        // The Dialogue tab consumes the assembled tree; it must group topics under quests (DIAL QNAM).
        Assert.NotNull(result.Records.DialogueTree);
        Assert.NotEmpty(result.Records.DialogueTree!.QuestTrees);

        // Speaker attribution is direct in FO4 (INFO ANAM → NPC_), with CTDA as a fallback. FO4 leans
        // heavily on scene/quest-alias dialogue, so coverage is lower than Skyrim's but still substantial.
        var withSpeaker = result.Records.Dialogues.Count(d => d.SpeakerFormId is > 0);
        Assert.True(withSpeaker > 5000,
            $"Expected many FO4 INFOs to attribute an NPC speaker via ANAM/CTDA; got {withSpeaker}.");
    }

    [Fact]
    public async Task Fallout4_Commonwealth_WaterAuthoring_PinsSanctuaryAndAdjacentCells()
    {
        // Exact retail FormIDs and their authored relationship cannot be replaced by a synthetic
        // fixture without ceasing to be the requested retail oracle. Keep this coverage inside the
        // grandfathered, sequential Bucket-B class and reuse its cache-owned full-master load.
        var esm = ResolveFallout4Esm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Fallout4.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout 4).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        var commonwealth = Assert.Single(result.Records.Worldspaces,
            worldspace => worldspace.FormId == 0x0000003C);
        Assert.Equal(450f, commonwealth.DefaultWaterHeight);
        Assert.False(commonwealth.WaterFromParentWorldspace);

        var sanctuary = Assert.Single(commonwealth.Cells,
            cell => cell.FormId == 0x0000DD60);
        Assert.Equal(-20, sanctuary.GridX);
        Assert.Equal(21, sanctuary.GridY);
        Assert.Equal((byte)0x02, sanctuary.Flags);
        Assert.True(sanctuary.HasWater);
        var sanctuaryWaterHeight = Assert.IsType<float>(sanctuary.WaterHeight);
        Assert.Equal(0x7F7FFFFFu,
            BitConverter.SingleToUInt32Bits(sanctuaryWaterHeight));
        Assert.True(WorldHeightNormalizer.IsNoWaterSentinel(sanctuaryWaterHeight));

        // The semantic parser canonicalizes every non-reportable XCLW to FLT_MAX. Inspect the
        // decompressed retail record as well so this discriminator proves the bytes were authored
        // as the canonical sentinel, rather than merely normalized to it after parsing.
        var rawCell = Assert.Single(result.RawResult.EsmRecords!.MainRecords,
            record => record.RecordType == "CELL" && record.FormId == sanctuary.FormId);
        Assert.False(rawCell.IsBigEndian);
        var storedPayload = new byte[checked((int)rawCell.DataSize)];
        var accessor = result.Accessor
            ?? throw new InvalidOperationException("Retail ESM load did not retain its memory-mapped accessor.");
        Assert.Equal(storedPayload.Length, accessor.ReadArray(
            rawCell.Offset + rawCell.HeaderSize, storedPayload, 0, storedPayload.Length));
        var rawPayload = rawCell.IsCompressed
            ? EsmParser.DecompressRecordData(storedPayload, rawCell.IsBigEndian)
              ?? throw new InvalidDataException("Retail CELL 0x0000DD60 could not be decompressed.")
            : storedPayload;
        var rawXclw = Assert.Single(EsmParser.ParseSubrecords(rawPayload, rawCell.IsBigEndian),
            subrecord => subrecord.Signature == "XCLW");
        Assert.Equal(4, rawXclw.Data.Length);
        Assert.Equal(0x7F7FFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(rawXclw.Data));

        Assert.Equal(0x00034519u, sanctuary.WaterFormId);
        Assert.Equal(450f, WorldRenderCache.ResolveEffectiveWaterHeight(
            sanctuary, commonwealth.DefaultWaterHeight, commonwealth.WaterFromParentWorldspace));

        var sanctuaryExt06 = Assert.Single(commonwealth.Cells,
            cell => cell.FormId == 0x0000DD5F);
        Assert.Equal(-19, sanctuaryExt06.GridX);
        Assert.Equal(21, sanctuaryExt06.GridY);
        Assert.Equal(7250f, Assert.IsType<float>(sanctuaryExt06.WaterHeight));

        var sanctuaryExt04 = Assert.Single(commonwealth.Cells,
            cell => cell.FormId == 0x0000DD81);
        Assert.Equal(-20, sanctuaryExt04.GridX);
        Assert.Equal(20, sanctuaryExt04.GridY);
        Assert.Equal(7250f, Assert.IsType<float>(sanctuaryExt04.WaterHeight));
    }
}
