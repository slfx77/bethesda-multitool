using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.App;

public sealed class RuntimeParityDumpIntegrationTests
{
    private static readonly string SnippetDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "Dmp");

    private static readonly string[] AllSnippetNames =
        ["debug_dump", "release_dump", "xex4_dump", "xex44_dump", "memdebug_dump"];

    // --- Snippet-backed tests (RuntimeStructReader only) ---

    [Fact]
    public async Task RuntimeReader_OnDebugDump_ReadsWorldspaceAndCellParityData()
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, "debug_dump");
        var reader = snippet.CreateStructReader();

        var wrldEntries = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x41 && entry.TesFormOffset.HasValue)
            .ToList();
        Assert.NotEmpty(wrldEntries);

        var runtimeWorldspaces = wrldEntries
            .Select(reader.ReadRuntimeWorldspace)
            .Where(worldspace => worldspace is not null)
            .Select(worldspace => worldspace!)
            .ToList();

        Assert.NotEmpty(runtimeWorldspaces);
        Assert.Contains(runtimeWorldspaces,
            worldspace => worldspace.MapUsableWidth is > 0 || worldspace.MapUsableHeight is > 0);

        var worldspaceCellMaps = reader.ReadAllWorldspaceCellMaps(wrldEntries);
        Assert.NotEmpty(worldspaceCellMaps);

        Assert.Contains(
            worldspaceCellMaps.Values,
            world => world.Cells.Count > 0 || world.PersistentCellFormId is > 0);
    }

    [Fact]
    public async Task RuntimeReader_OnReleaseDump_ReadsListAndWorldObjectParityData()
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, "release_dump");
        var reader = snippet.CreateStructReader();

        var formLists = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x55 && entry.TesFormOffset.HasValue)
            .Select(reader.ReadRuntimeFormList)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(formLists);
        Assert.Contains(formLists, record => record.FormIds.Count > 0);

        var leveledLists = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType is 0x2C or 0x2D or 0x34 && entry.TesFormOffset.HasValue)
            .Select(reader.ReadRuntimeLeveledList)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(leveledLists);
        Assert.Contains(leveledLists, record => record.Entries.Count > 0);

        var activators = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x15 && entry.TesFormOffset.HasValue)
            .Select(reader.ReadRuntimeActivator)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(activators);
        Assert.Contains(activators, record => record.ModelPath != null || record.Script is > 0);

        var doors = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x1C && entry.TesFormOffset.HasValue)
            .Select(reader.ReadRuntimeDoor)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(doors);
        Assert.Contains(doors, record => record.ModelPath != null || record.OpenSoundFormId is > 0);

        var lights = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x1E && entry.TesFormOffset.HasValue)
            .Select(reader.ReadRuntimeLight)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(lights);
        Assert.Contains(lights, record => record.Radius > 0 || record.ModelPath != null);

        var statics = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x20 && entry.TesFormOffset.HasValue)
            .Select(reader.ReadRuntimeStatic)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(statics);
        Assert.Contains(statics, record => record.ModelPath != null || record.Bounds != null);

        var furniture = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x27 && entry.TesFormOffset.HasValue)
            .Select(reader.ReadRuntimeFurniture)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(furniture);
        Assert.Contains(furniture, record => record.ModelPath != null || record.MarkerFlags != 0);

        var runtimeRefrs = snippet.RuntimeRefrFormEntries
            .Where(entry => entry.TesFormOffset.HasValue)
            .Select(reader.ReadRuntimeRefr)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(runtimeRefrs);
        Assert.Contains(runtimeRefrs, record => record.PersistentCellFormId is > 0);
        Assert.Contains(runtimeRefrs, record => record.LinkedRefChildrenFormIds.Count > 0);
        Assert.Contains(runtimeRefrs, record => record.StartingPosition != null);
        Assert.Contains(runtimeRefrs, record => record.PackageStartLocation != null);
        Assert.Contains(runtimeRefrs, record => record.Radius is > 0);
        Assert.Contains(
            runtimeRefrs,
            record => record.LeveledCreatureOriginalBaseFormId is > 0 ||
                      record.LeveledCreatureTemplateFormId is > 0);
    }

    [Fact]
    public async Task RuntimeReader_OnReleaseBetaVariant_ReadsEncounterZoneParityData()
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, "xex4_dump");
        var reader = snippet.CreateStructReader();

        var runtimeRefrs = snippet.RuntimeRefrFormEntries
            .Where(entry => entry.TesFormOffset.HasValue)
            .Select(reader.ReadRuntimeRefr)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();

        Assert.NotEmpty(runtimeRefrs);
        Assert.Contains(runtimeRefrs, record => record.EncounterZoneFormId is > 0);
    }

    [Fact]
    public async Task RuntimeReader_OnReleaseBetaVariant_ReadsMerchantContainerParityData()
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, "xex4_dump");
        var reader = snippet.CreateStructReader();

        var runtimeRefrs = snippet.RuntimeRefrFormEntries
            .Where(entry => entry.TesFormOffset.HasValue)
            .Select(reader.ReadRuntimeRefr)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();

        Assert.NotEmpty(runtimeRefrs);
        Assert.Contains(runtimeRefrs, record => record.MerchantContainerFormId is > 0);
    }

    [Fact]
    public async Task RuntimeReader_OnAllDumps_DiagnosesConversationDataLinkPopulation()
    {
        var allDiagnostics = new List<(string DumpName, int ValidPtrs, int LinkFromHeads, int LinkFromDecodes,
            int LinkToHeads, int LinkToDecodes, int FollowUpHeads, int FollowUpDecodes)>();

        foreach (var snippetName in AllSnippetNames)
        {
            var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
            var runtimeReader = snippet.CreateStructReader();

            var topicEntries = snippet.RuntimeEditorIds
                .Where(entry => entry.FormType == 0x45 && entry.TesFormOffset.HasValue)
                .ToList();

            foreach (var topicEntry in topicEntries)
            {
                var links = runtimeReader.WalkTopicQuestInfoList(topicEntry);
                foreach (var link in links)
                {
                    foreach (var infoEntry in link.InfoEntries)
                    {
                        runtimeReader.ReadRuntimeDialogueInfoFromVA(infoEntry.VirtualAddress);
                    }
                }
            }

            var diag = runtimeReader.DialogueConversationDiagnostics;
            allDiagnostics.Add((
                snippetName,
                diag.ValidPointerCount,
                diag.LinkFromNonZeroHead, diag.LinkFromPositiveDecodes,
                diag.LinkToNonZeroHead, diag.LinkToPositiveDecodes,
                diag.FollowUpNonZeroHead, diag.FollowUpPositiveDecodes));
        }

        Assert.True(
            allDiagnostics.Any(d => d.ValidPtrs > 0),
            "Expected valid TESConversationData pointers in at least one dump.");
    }

    [Fact]
    public async Task RuntimeReader_OnAllDumps_ReadsPerkSkillStatForSpeechChallenges()
    {
        var totalSpeechChallengeInfos = 0;
        var totalWithPerkSkillStat = 0;

        foreach (var snippetName in AllSnippetNames)
        {
            var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
            var runtimeReader = snippet.CreateStructReader();

            var topicEntries = snippet.RuntimeEditorIds
                .Where(entry => entry.FormType == 0x45 && entry.TesFormOffset.HasValue)
                .ToList();

            foreach (var topicEntry in topicEntries)
            {
                var links = runtimeReader.WalkTopicQuestInfoList(topicEntry);
                foreach (var link in links)
                {
                    foreach (var infoEntry in link.InfoEntries)
                    {
                        var info = runtimeReader.ReadRuntimeDialogueInfoFromVA(infoEntry.VirtualAddress);
                        if (info is { Difficulty: > 0 })
                        {
                            totalSpeechChallengeInfos++;
                            if (info.PerkSkillStatFormId.HasValue)
                            {
                                totalWithPerkSkillStat++;
                            }
                        }
                    }
                }
            }
        }

        Assert.True(totalSpeechChallengeInfos > 0,
            "Expected at least one speech challenge INFO across dump families");

        Assert.True(totalWithPerkSkillStat > 0,
            $"Expected at least one speech challenge INFO with pPerkSkillStat. " +
            $"Found {totalSpeechChallengeInfos} speech challenges, {totalWithPerkSkillStat} with pPerkSkillStat.");
    }

    [Fact]
    public async Task RuntimeReader_OnAllDumps_ReadsProjectilePhysicsData()
    {
        var allResults = new List<(string DumpName, int TotalProj, int Decoded, int WithSpeed)>();

        foreach (var snippetName in AllSnippetNames)
        {
            var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
            var runtimeReader = snippet.CreateStructReader();

            var projEntries = snippet.RuntimeEditorIds
                .Where(entry => entry.FormType == 0x33 && entry.TesFormOffset.HasValue)
                .ToList();

            int decoded = 0, withSpeed = 0;

            foreach (var entry in projEntries)
            {
                var data = runtimeReader.ReadProjectilePhysics(entry.TesFormOffset!.Value, entry.FormId);
                if (data == null) continue;
                decoded++;
                if (data.Speed is not 0f) withSpeed++;
            }

            allResults.Add((snippetName, projEntries.Count, decoded, withSpeed));
        }

        Assert.True(allResults.Any(r => r.Decoded > 0),
            "Expected at least one dump to have decodable PROJ runtime structs");

        var totalDecoded = allResults.Sum(r => r.Decoded);
        var totalWithSpeed = allResults.Sum(r => r.WithSpeed);
        Assert.True(totalWithSpeed > totalDecoded / 2,
            $"Expected majority of decoded projectiles to have non-zero speed. " +
            $"Decoded: {totalDecoded}, With speed: {totalWithSpeed}");
    }
}