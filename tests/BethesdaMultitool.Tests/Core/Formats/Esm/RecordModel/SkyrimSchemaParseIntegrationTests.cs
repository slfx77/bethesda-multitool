using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.WorldData;
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
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public class SkyrimSchemaParseIntegrationTests
{
    [Fact]
    public async Task Dragonsreach_CellFlagsKeepInteriorLightingWhileShowingSky()
    {
        var esm = RealAssetPaths.Masters.Skyrim();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Skyrim.esm not found (set BETHESDA_TEST_DATA_ROOT or install Skyrim).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm, TestContext.Current.CancellationToken);
        var cell = Assert.Single(result.Records.Cells,
            candidate => candidate.FormId == 0x000165A3 && candidate.EditorId == "WhiterunDragonsreach");
        var sky = SkySceneContextResolver.Resolve(cell, null, null);

        Assert.Equal(0xA1u, cell.Flags);
        Assert.Equal(CellDataFlagSemantics.Creation, cell.DataFlagSemantics);
        Assert.True(cell.IsInterior);
        Assert.True(cell.ShowsSky);
        Assert.False(cell.UsesSkyLighting);
        Assert.False(cell.BehavesLikeExterior);
        Assert.True(sky.RendersExteriorSky);
        Assert.False(sky.BehavesLikeExterior);
        Assert.Equal(0x0006175Du, cell.LightingTemplateFormId);
        Assert.Equal(0x00036ED2u, cell.ImageSpaceFormId);
        Assert.NotNull(cell.LightingData);
        Assert.Equal(0x0040505Bu, Assert.IsType<uint>(cell.LightingData!["AmbientColor"]));
        Assert.Equal(6300f, Assert.IsType<float>(cell.LightingData["FogFar"]));
        Assert.Equal(7000f, Assert.IsType<float>(cell.LightingData["FogClipDistance"]));
        Assert.Equal(6u, cell.LightingTemplateInheritanceFlags);

        var template = Assert.Single(result.Records.LightingTemplates,
            candidate => candidate.FormId == 0x0006175D);
        Assert.NotNull(template.LightingData);
        Assert.Equal(0x004A4940u, Assert.IsType<uint>(template.LightingData!["DirectionalColor"]));
        Assert.Equal(0x00475358u, Assert.IsType<uint>(template.LightingData["FogColor"]));
        Assert.Equal(2000f, Assert.IsType<float>(template.LightingData["FogFar"]));
    }

    [Fact]
    public async Task Skyrim_Npcs_Are_SchemaDecoded_With_Rich_Blocks()
    {
        var esm = RealAssetPaths.Masters.Skyrim();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Skyrim.esm not found (set BETHESDA_TEST_DATA_ROOT or install Skyrim).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm, TestContext.Current.CancellationToken);

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
        var esm = RealAssetPaths.Masters.Skyrim();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "Skyrim.esm not found (set BETHESDA_TEST_DATA_ROOT or install Skyrim).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm, TestContext.Current.CancellationToken);

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