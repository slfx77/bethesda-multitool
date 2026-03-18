using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.App;

[Collection(DumpSerialTestGroup.Name)]
public sealed class RuntimeParityDumpIntegrationTests(SampleFileFixture samples)
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task RuntimeReader_OnDebugDump_ReadsWorldspaceAndCellParityData()
    {
        Assert.SkipWhen(samples.DebugDump is null, "Debug memory dump not available");

        var analysisResult = await AnalyzeDumpAsync(samples.DebugDump!);
        Assert.NotNull(analysisResult.EsmRecords);
        Assert.NotNull(analysisResult.MinidumpInfo);

        var wrldEntries = analysisResult.EsmRecords!.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x41 && entry.TesFormOffset.HasValue)
            .ToList();
        Assert.NotEmpty(wrldEntries);

        using var mmf =
            MemoryMappedFile.CreateFromFile(samples.DebugDump!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor =
            mmf.CreateViewAccessor(0, new FileInfo(samples.DebugDump!).Length, MemoryMappedFileAccess.Read);
        var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            new FileInfo(samples.DebugDump!).Length,
            analysisResult.MinidumpInfo!,
            analysisResult.EsmRecords.RuntimeRefrFormEntries,
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList(),
            wrldEntries,
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList());

        var runtimeWorldspaces = wrldEntries
            .Select(runtimeReader.ReadRuntimeWorldspace)
            .Where(worldspace => worldspace is not null)
            .Select(worldspace => worldspace!)
            .ToList();

        Assert.NotEmpty(runtimeWorldspaces);
        Assert.Contains(runtimeWorldspaces,
            worldspace => worldspace.MapUsableWidth is > 0 || worldspace.MapUsableHeight is > 0);

        var worldspaceCellMaps = runtimeReader.ReadAllWorldspaceCellMaps(wrldEntries);
        Assert.NotEmpty(worldspaceCellMaps);

        Assert.Contains(
            worldspaceCellMaps.Values,
            world => world.Cells.Count > 0 || world.PersistentCellFormId is > 0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task RuntimeReader_OnReleaseDump_ReadsListAndWorldObjectParityData()
    {
        Assert.SkipWhen(samples.ReleaseDump is null, "Release memory dump not available");

        var analysisResult = await AnalyzeDumpAsync(samples.ReleaseDump!);
        Assert.NotNull(analysisResult.EsmRecords);
        Assert.NotNull(analysisResult.MinidumpInfo);

        using var mmf = MemoryMappedFile.CreateFromFile(samples.ReleaseDump!, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor =
            mmf.CreateViewAccessor(0, new FileInfo(samples.ReleaseDump!).Length, MemoryMappedFileAccess.Read);
        var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            new FileInfo(samples.ReleaseDump!).Length,
            analysisResult.MinidumpInfo!,
            analysisResult.EsmRecords!.RuntimeRefrFormEntries,
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList(),
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x41).ToList(),
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList());

        var formLists = analysisResult.EsmRecords.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x55 && entry.TesFormOffset.HasValue)
            .Select(runtimeReader.ReadRuntimeFormList)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(formLists);
        Assert.Contains(formLists, record => record.FormIds.Count > 0);

        var leveledLists = analysisResult.EsmRecords.RuntimeEditorIds
            .Where(entry => entry.FormType is 0x2C or 0x2D or 0x34 && entry.TesFormOffset.HasValue)
            .Select(runtimeReader.ReadRuntimeLeveledList)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(leveledLists);
        Assert.Contains(leveledLists, record => record.Entries.Count > 0);

        var activators = analysisResult.EsmRecords.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x15 && entry.TesFormOffset.HasValue)
            .Select(runtimeReader.ReadRuntimeActivator)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(activators);
        Assert.Contains(activators, record => record.ModelPath != null || record.Script is > 0);

        var doors = analysisResult.EsmRecords.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x1C && entry.TesFormOffset.HasValue)
            .Select(runtimeReader.ReadRuntimeDoor)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(doors);
        Assert.Contains(doors, record => record.ModelPath != null || record.OpenSoundFormId is > 0);

        var lights = analysisResult.EsmRecords.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x1E && entry.TesFormOffset.HasValue)
            .Select(runtimeReader.ReadRuntimeLight)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(lights);
        Assert.Contains(lights, record => record.Radius > 0 || record.ModelPath != null);

        var statics = analysisResult.EsmRecords.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x20 && entry.TesFormOffset.HasValue)
            .Select(runtimeReader.ReadRuntimeStatic)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(statics);
        Assert.Contains(statics, record => record.ModelPath != null || record.Bounds != null);

        var furniture = analysisResult.EsmRecords.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x27 && entry.TesFormOffset.HasValue)
            .Select(runtimeReader.ReadRuntimeFurniture)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();
        Assert.NotEmpty(furniture);
        Assert.Contains(furniture, record => record.ModelPath != null || record.MarkerFlags != 0);

        var runtimeRefrs = analysisResult.EsmRecords.RuntimeRefrFormEntries
            .Where(entry => entry.TesFormOffset.HasValue)
            .Select(runtimeReader.ReadRuntimeRefr)
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
    [Trait("Category", "Slow")]
    public async Task RuntimeReader_OnSampleDump_ReadsQuestObjectiveParityData()
    {
        var candidateDumps = new[]
            {
                samples.DebugDump,
                samples.ReleaseDumpXex4,
                samples.ReleaseDumpXex44,
                samples.ReleaseDump
            }
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(candidateDumps.Count == 0, "Quest-objective-positive sample dump not available");

        foreach (var dumpPath in candidateDumps)
        {
            var analysisResult = await AnalyzeDumpAsync(dumpPath!);
            Assert.NotNull(analysisResult.EsmRecords);
            Assert.NotNull(analysisResult.MinidumpInfo);

            var fileInfo = new FileInfo(dumpPath!);
            using var mmf =
                MemoryMappedFile.CreateFromFile(dumpPath!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
            var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo!,
                analysisResult.EsmRecords!.RuntimeRefrFormEntries,
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x41).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList());

            var runtimeQuests = analysisResult.EsmRecords.RuntimeEditorIds
                .Where(entry => entry.FormType == 0x47 && entry.TesFormOffset.HasValue)
                .Select(runtimeReader.ReadRuntimeQuest)
                .Where(record => record is not null)
                .Select(record => record!)
                .ToList();

            var runtimeQuestsWithObjectives = runtimeQuests
                .Where(record => record.Objectives.Count > 0 &&
                                 record.Objectives.Any(objective => !string.IsNullOrEmpty(objective.DisplayText)))
                .ToList();

            if (runtimeQuestsWithObjectives.Count == 0)
            {
                continue;
            }

            var parser = new RecordParser(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo);
            var semanticQuests = parser.ParseQuests();
            var runtimeQuestIds = runtimeQuestsWithObjectives
                .Select(record => record.FormId)
                .ToHashSet();

            Assert.Contains(
                semanticQuests,
                quest => runtimeQuestIds.Contains(quest.FormId) &&
                         quest.Objectives.Count > 0 &&
                         quest.Objectives.Any(objective => !string.IsNullOrEmpty(objective.DisplayText)));
            return;
        }

        Assert.Fail(
            $"Expected runtime quest objective data in at least one sample dump. Checked: {string.Join(", ", candidateDumps.Select(Path.GetFileName))}");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task RuntimeReader_OnSampleDump_ReadsQuestStageParityData()
    {
        var candidateDumps = new[]
            {
                samples.DebugDump,
                samples.ReleaseDumpXex4,
                samples.ReleaseDumpXex44,
                samples.ReleaseDump
            }
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(candidateDumps.Count == 0, "Quest-stage-positive sample dump not available");

        foreach (var dumpPath in candidateDumps)
        {
            var analysisResult = await AnalyzeDumpAsync(dumpPath!);
            Assert.NotNull(analysisResult.EsmRecords);
            Assert.NotNull(analysisResult.MinidumpInfo);

            var fileInfo = new FileInfo(dumpPath!);
            using var mmf =
                MemoryMappedFile.CreateFromFile(dumpPath!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
            var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo!,
                analysisResult.EsmRecords!.RuntimeRefrFormEntries,
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x41).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList());

            var runtimeQuests = analysisResult.EsmRecords.RuntimeEditorIds
                .Where(entry => entry.FormType == 0x47 && entry.TesFormOffset.HasValue)
                .Select(runtimeReader.ReadRuntimeQuest)
                .Where(record => record is not null)
                .Select(record => record!)
                .ToList();

            var runtimeQuestsWithStages = runtimeQuests
                .Where(record => record.Stages.Count > 0)
                .ToList();

            if (runtimeQuestsWithStages.Count == 0)
            {
                continue;
            }

            var parser = new RecordParser(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo);
            var semanticQuests = parser.ParseQuests();
            var runtimeQuestIds = runtimeQuestsWithStages
                .Select(record => record.FormId)
                .ToHashSet();

            Assert.Contains(
                semanticQuests,
                quest => runtimeQuestIds.Contains(quest.FormId) &&
                         quest.Stages.Count > 0);
            return;
        }

        Assert.Fail(
            $"Expected runtime quest stage data in at least one sample dump. Checked: {string.Join(", ", candidateDumps.Select(Path.GetFileName))}");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task RuntimeReader_OnSampleDump_ReadsDialogTopicParityData()
    {
        var candidateDumps = new[]
            {
                samples.DebugDump,
                samples.ReleaseDumpXex4,
                samples.ReleaseDumpXex44,
                samples.ReleaseDump
            }
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(candidateDumps.Count == 0, "Dialog-topic-positive sample dump not available");

        foreach (var dumpPath in candidateDumps)
        {
            var analysisResult = await AnalyzeDumpAsync(dumpPath!);
            Assert.NotNull(analysisResult.EsmRecords);
            Assert.NotNull(analysisResult.MinidumpInfo);

            var fileInfo = new FileInfo(dumpPath!);
            using var mmf =
                MemoryMappedFile.CreateFromFile(dumpPath!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
            var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo!,
                analysisResult.EsmRecords!.RuntimeRefrFormEntries,
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x41).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList());

            var runtimeTopicSignals = analysisResult.EsmRecords.RuntimeEditorIds
                .Where(entry => entry.FormType == 0x45 && entry.TesFormOffset.HasValue)
                .Select(entry => new
                {
                    Entry = entry,
                    Topic = runtimeReader.ReadRuntimeDialogTopic(entry),
                    DerivedResponseCount =
                        runtimeReader.WalkTopicQuestInfoList(entry).Sum(link => link.InfoEntries.Count)
                })
                .Where(signal => signal.Topic is not null)
                .ToList();

            var rawCountTopicIds = runtimeTopicSignals
                .Where(signal => signal.Topic!.TopicCount > 0)
                .Select(signal => signal.Entry.FormId)
                .ToHashSet();
            var derivedCountTopicIds = runtimeTopicSignals
                .Where(signal => signal.Topic!.TopicCount == 0 && signal.DerivedResponseCount > 0)
                .Select(signal => signal.Entry.FormId)
                .ToHashSet();

            if (rawCountTopicIds.Count == 0 && derivedCountTopicIds.Count == 0)
            {
                continue;
            }

            var parser = new RecordParser(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo);
            var semanticResult = parser.ParseAll();

            var rawCountTopics = semanticResult.DialogTopics
                .Where(topic => rawCountTopicIds.Contains(topic.FormId) && topic.ResponseCount > 0)
                .ToList();
            if (rawCountTopics.Count > 0)
            {
                return;
            }

            var derivedCountTopics = semanticResult.DialogTopics
                .Where(topic =>
                    derivedCountTopicIds.Contains(topic.FormId) &&
                    topic.ResponseCount > 0 &&
                    topic.QuestFormId > 0)
                .ToList();
            if (derivedCountTopics.Count > 0)
            {
                return;
            }
        }

        Assert.Fail(
            $"Expected runtime-backed dialog topic response/linkage data in at least one sample dump. Checked: {string.Join(", ", candidateDumps.Select(Path.GetFileName))}");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ParseAll_OnSampleDump_ProducesRuntimeDialogueFollowUpParity()
    {
        var memDebugDump = SampleFileFixture.FindSamplePath(@"Sample\MemoryDump\Fallout_Release_MemDebug.xex.dmp");
        var candidateDumps = new[]
            {
                memDebugDump
            }
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(candidateDumps.Count == 0, "INFO-follow-up-positive sample dump not available");

        foreach (var dumpPath in candidateDumps)
        {
            var analysisResult = await AnalyzeDumpAsync(dumpPath!);
            Assert.NotNull(analysisResult.EsmRecords);
            Assert.NotNull(analysisResult.MinidumpInfo);

            var fileInfo = new FileInfo(dumpPath!);
            using var mmf =
                MemoryMappedFile.CreateFromFile(dumpPath!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
            var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo!,
                analysisResult.EsmRecords!.RuntimeRefrFormEntries,
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x41).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList());

            var runtimeInfosWithFollowUps = analysisResult.EsmRecords.RuntimeEditorIds
                .Where(entry => entry.FormType == 0x45 && entry.TesFormOffset.HasValue)
                .SelectMany(entry => runtimeReader.WalkTopicQuestInfoList(entry))
                .SelectMany(link => link.InfoEntries)
                .GroupBy(infoEntry => infoEntry.FormId)
                .Select(group => runtimeReader.ReadRuntimeDialogueInfoFromVA(group.First().VirtualAddress))
                .Where(record => record is not null)
                .Select(record => record!)
                .Where(record => record.FollowUpInfoFormIds.Count > 0)
                .ToList();

            if (runtimeInfosWithFollowUps.Count == 0)
            {
                continue;
            }

            var parser = new RecordParser(
                analysisResult.EsmRecords!,
                analysisResult.FormIdMap,
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo);
            var semanticResult = parser.ParseAll();
            var runtimeByFormId = runtimeInfosWithFollowUps.ToDictionary(record => record.FormId);

            Assert.Contains(
                semanticResult.Dialogues,
                dialogue => runtimeByFormId.TryGetValue(dialogue.FormId, out var runtimeInfo) &&
                            dialogue.FollowUpInfos.SequenceEqual(runtimeInfo.FollowUpInfoFormIds));
            return;
        }

        Assert.Fail(
            $"Expected runtime INFO follow-up data in at least one sample dump. Checked: {string.Join(", ", candidateDumps.Select(Path.GetFileName))}");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task RuntimeReader_OnReleaseBetaVariant_ReadsEncounterZoneParityData()
    {
        var releaseEncounterDump = samples.ReleaseDumpXex4 ?? samples.ReleaseDumpXex44;
        Assert.SkipWhen(releaseEncounterDump is null,
            "Encounter-zone-positive release-beta dump variant not available");

        var analysisResult = await AnalyzeDumpAsync(releaseEncounterDump!);
        Assert.NotNull(analysisResult.EsmRecords);
        Assert.NotNull(analysisResult.MinidumpInfo);

        using var mmf = MemoryMappedFile.CreateFromFile(releaseEncounterDump!, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor =
            mmf.CreateViewAccessor(0, new FileInfo(releaseEncounterDump!).Length, MemoryMappedFileAccess.Read);
        var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            new FileInfo(releaseEncounterDump!).Length,
            analysisResult.MinidumpInfo!,
            analysisResult.EsmRecords!.RuntimeRefrFormEntries,
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList(),
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x41).ToList(),
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList());

        var runtimeRefrs = analysisResult.EsmRecords.RuntimeRefrFormEntries
            .Where(entry => entry.TesFormOffset.HasValue)
            .Select(runtimeReader.ReadRuntimeRefr)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();

        Assert.NotEmpty(runtimeRefrs);
        Assert.Contains(runtimeRefrs, record => record.EncounterZoneFormId is > 0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task RuntimeReader_OnReleaseBetaVariant_ReadsMerchantContainerParityData()
    {
        var merchantDump = samples.ReleaseDumpXex4 ?? samples.ReleaseDumpXex44 ?? samples.DebugDump;
        Assert.SkipWhen(merchantDump is null,
            "Merchant-container-positive dump variant not available");

        var analysisResult = await AnalyzeDumpAsync(merchantDump!);
        Assert.NotNull(analysisResult.EsmRecords);
        Assert.NotNull(analysisResult.MinidumpInfo);

        using var mmf =
            MemoryMappedFile.CreateFromFile(merchantDump!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, new FileInfo(merchantDump!).Length, MemoryMappedFileAccess.Read);
        var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            new FileInfo(merchantDump!).Length,
            analysisResult.MinidumpInfo!,
            analysisResult.EsmRecords!.RuntimeRefrFormEntries,
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList(),
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x41).ToList(),
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList());

        var runtimeRefrs = analysisResult.EsmRecords.RuntimeRefrFormEntries
            .Where(entry => entry.TesFormOffset.HasValue)
            .Select(runtimeReader.ReadRuntimeRefr)
            .Where(record => record is not null)
            .Select(record => record!)
            .ToList();

        Assert.NotEmpty(runtimeRefrs);
        Assert.Contains(runtimeRefrs, record => record.MerchantContainerFormId is > 0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ParseAll_OnDebugDump_ProducesTypedWorldAndDistributionParity()
    {
        Assert.SkipWhen(samples.DebugDump is null, "Debug memory dump not available");

        var analysisResult = await AnalyzeDumpAsync(samples.DebugDump!);
        Assert.NotNull(analysisResult.EsmRecords);

        var fileInfo = new FileInfo(samples.DebugDump!);
        using var mmf =
            MemoryMappedFile.CreateFromFile(samples.DebugDump!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords!,
            analysisResult.FormIdMap,
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo);

        var semantic = parser.ParseAll();

        Assert.Contains(semantic.Worldspaces,
            worldspace => worldspace.MapUsableWidth is > 0 || worldspace.MapUsableHeight is > 0);
        Assert.Contains(semantic.Cells,
            cell => cell.GridX.HasValue && cell.GridY.HasValue && cell.WorldspaceFormId is > 0);
        Assert.Contains(semantic.FormLists, record => record.FormIds.Count > 0);
        Assert.Contains(semantic.LeveledLists, record => record.Entries.Count > 0);
        Assert.Contains(semantic.Activators, record => record.ModelPath != null || record.Script is > 0);
        Assert.Contains(semantic.Doors, record => record.ModelPath != null || record.OpenSoundFormId is > 0);
        Assert.Contains(semantic.Lights, record => record.Radius > 0 || record.ModelPath != null);
        Assert.Contains(semantic.Statics, record => record.ModelPath != null || record.Bounds != null);
        Assert.Contains(semantic.Furniture, record => record.ModelPath != null || record.MarkerFlags != 0);

        var referencedFormId = semantic.FormLists
            .SelectMany(record => record.FormIds)
            .Concat(semantic.LeveledLists.SelectMany(record => record.Entries.Select(entry => entry.FormId)))
            .FirstOrDefault();

        Assert.NotEqual(0u, referencedFormId);

        var usageIndex = FormUsageIndex.Build(semantic);
        Assert.True(usageIndex.GetUseCount(referencedFormId) > 0,
            $"Expected runtime list reference 0x{referencedFormId:X8} to appear in the usage index.");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ParseAll_OnReleaseDump_ProducesPlacedReferenceParity()
    {
        Assert.SkipWhen(samples.ReleaseDump is null, "Release memory dump not available");

        var analysisResult = await AnalyzeDumpAsync(samples.ReleaseDump!);
        Assert.NotNull(analysisResult.EsmRecords);

        var fileInfo = new FileInfo(samples.ReleaseDump!);
        using var mmf = MemoryMappedFile.CreateFromFile(samples.ReleaseDump!, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords!,
            analysisResult.FormIdMap,
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo);

        var semantic = parser.ParseAll();
        var placedObjects = semantic.Cells
            .SelectMany(cell => cell.PlacedObjects)
            .ToList();

        Assert.NotEmpty(placedObjects);
        Assert.Contains(
            placedObjects,
            placedObject => placedObject.OwnerFormId is > 0 ||
                            placedObject.EnableParentFormId is > 0 ||
                            placedObject.LinkedRefFormId is > 0 ||
                            placedObject.DestinationDoorFormId is > 0);
        Assert.Contains(
            placedObjects,
            placedObject => placedObject.LockLevel.HasValue ||
                            placedObject.LockKeyFormId is > 0 ||
                            placedObject.LockFlags.HasValue);
        Assert.Contains(
            placedObjects,
            placedObject => placedObject.StartingPosition != null ||
                            placedObject.PackageStartLocation != null);
        Assert.Contains(placedObjects, placedObject => placedObject.Radius is > 0);
        Assert.Contains(
            placedObjects,
            placedObject => placedObject.LeveledCreatureOriginalBaseFormId is > 0 ||
                            placedObject.LeveledCreatureTemplateFormId is > 0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ParseAll_OnReleaseBetaVariant_ProducesEncounterZonePlacedReferenceParity()
    {
        var releaseEncounterDump = samples.ReleaseDumpXex4 ?? samples.ReleaseDumpXex44;
        Assert.SkipWhen(releaseEncounterDump is null,
            "Encounter-zone-positive release-beta dump variant not available");

        var analysisResult = await AnalyzeDumpAsync(releaseEncounterDump!);
        Assert.NotNull(analysisResult.EsmRecords);

        var fileInfo = new FileInfo(releaseEncounterDump!);
        using var mmf = MemoryMappedFile.CreateFromFile(releaseEncounterDump!, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords!,
            analysisResult.FormIdMap,
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo);

        var semantic = parser.ParseAll();
        var placedObjects = semantic.Cells
            .SelectMany(cell => cell.PlacedObjects)
            .ToList();

        Assert.NotEmpty(placedObjects);
        Assert.Contains(placedObjects, placedObject => placedObject.EncounterZoneFormId is > 0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ParseAll_OnReleaseBetaVariant_ProducesMerchantContainerPlacedReferenceParity()
    {
        var merchantDump = samples.ReleaseDumpXex4 ?? samples.ReleaseDumpXex44 ?? samples.DebugDump;
        Assert.SkipWhen(merchantDump is null,
            "Merchant-container-positive dump variant not available");

        var analysisResult = await AnalyzeDumpAsync(merchantDump!);
        Assert.NotNull(analysisResult.EsmRecords);

        var fileInfo = new FileInfo(merchantDump!);
        using var mmf =
            MemoryMappedFile.CreateFromFile(merchantDump!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords!,
            analysisResult.FormIdMap,
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo);

        var semantic = parser.ParseAll();
        var placedObjects = semantic.Cells
            .SelectMany(cell => cell.PlacedObjects)
            .ToList();

        Assert.NotEmpty(placedObjects);
        Assert.Contains(placedObjects, placedObject => placedObject.MerchantContainerFormId is > 0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ParseAll_OnReleaseBetaVariant_ProducesRuntimeOnlyNonVirtualParentCellAssignments()
    {
        var parentSignalDump = samples.ReleaseDumpXex4 ??
                               samples.ReleaseDumpXex44 ?? samples.ReleaseDump ?? samples.DebugDump;
        Assert.SkipWhen(parentSignalDump is null,
            "Parent-cell-positive dump variant not available");

        var analysisResult = await AnalyzeDumpAsync(parentSignalDump!);
        Assert.NotNull(analysisResult.EsmRecords);

        var fileInfo = new FileInfo(parentSignalDump!);
        using var mmf =
            MemoryMappedFile.CreateFromFile(parentSignalDump!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords!,
            analysisResult.FormIdMap,
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo);

        var semantic = parser.ParseAll();
        var esmCellFormIds = analysisResult.EsmRecords!.MainRecords
            .Where(record => record.RecordType == "CELL")
            .Select(record => record.FormId)
            .ToHashSet();

        var runtimeOnlyParentCells = semantic.Cells
            .Where(cell => !cell.IsVirtual && !esmCellFormIds.Contains(cell.FormId))
            .Where(cell => cell.PlacedObjects.Any(placedObject => placedObject.AssignmentSource == "ParentCell"))
            .ToList();
        var virtualParentAssignments = semantic.Cells
            .Where(cell => cell.IsVirtual)
            .SelectMany(cell => cell.PlacedObjects)
            .Count(placedObject => placedObject.AssignmentSource == "Virtual" &&
                                   placedObject.PersistentCellFormId is > 0);

        Assert.True(
            runtimeOnlyParentCells.Count > 0,
            $"Expected runtime-only non-virtual parent-cell assignments in {Path.GetFileName(parentSignalDump)}. " +
            $"Observed {virtualParentAssignments} virtual refs still carrying persistent-cell signals.");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ParseAll_OnDebugDump_ProducesRuntimeOnlyWorldspaceStubsBackedByCells()
    {
        var worldspaceStubDump = samples.DebugDump ?? samples.ReleaseDumpXex4 ?? samples.ReleaseDump;
        Assert.SkipWhen(worldspaceStubDump is null,
            "Worldspace-stub-positive dump variant not available");

        var analysisResult = await AnalyzeDumpAsync(worldspaceStubDump!);
        Assert.NotNull(analysisResult.EsmRecords);

        var fileInfo = new FileInfo(worldspaceStubDump!);
        using var mmf =
            MemoryMappedFile.CreateFromFile(worldspaceStubDump!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords!,
            analysisResult.FormIdMap,
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo);

        var semantic = parser.ParseAll();
        var esmWorldFormIds = analysisResult.EsmRecords!.MainRecords
            .Where(record => record.RecordType == "WRLD")
            .Select(record => record.FormId)
            .ToHashSet();

        var runtimeOnlyWorldspaces = semantic.Worldspaces
            .Where(worldspace => !esmWorldFormIds.Contains(worldspace.FormId))
            .Where(worldspace => worldspace.Cells.Any(cell => !cell.IsVirtual))
            .ToList();
        var derivedExtentWorldspaces = runtimeOnlyWorldspaces
            .Where(worldspace => worldspace.MapNWCellX.HasValue || worldspace.BoundsMinX.HasValue)
            .ToList();

        Assert.True(
            runtimeOnlyWorldspaces.Count > 0,
            $"Expected at least one runtime-only worldspace stub backed by real cells in {Path.GetFileName(worldspaceStubDump)}.");
        Assert.True(
            derivedExtentWorldspaces.Count > 0,
            $"Expected at least one runtime-only worldspace stub with derived extents in {Path.GetFileName(worldspaceStubDump)}.");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task RuntimeReader_OnAllDumps_DiagnosesConversationDataLinkPopulation()
    {
        var candidateDumps = new[]
            {
                samples.DebugDump,
                samples.ReleaseDump,
                samples.ReleaseDumpXex4,
                samples.ReleaseDumpXex44,
                SampleFileFixture.FindSamplePath(@"Sample\MemoryDump\Fallout_Release_MemDebug.xex.dmp")
            }
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(candidateDumps.Count == 0, "No memory dumps available");

        var allDiagnostics = new List<(string DumpName, int ValidPtrs, int LinkFromHeads, int LinkFromDecodes,
            int LinkToHeads, int LinkToDecodes, int FollowUpHeads, int FollowUpDecodes)>();

        foreach (var dumpPath in candidateDumps)
        {
            var analysisResult = await AnalyzeDumpAsync(dumpPath!);
            Assert.NotNull(analysisResult.EsmRecords);
            Assert.NotNull(analysisResult.MinidumpInfo);

            var fileInfo = new FileInfo(dumpPath!);
            using var mmf =
                MemoryMappedFile.CreateFromFile(dumpPath!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
            var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo!,
                analysisResult.EsmRecords!.RuntimeRefrFormEntries,
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x41).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList());

            // Walk DIAL topics → QUEST_INFO → read all TESTopicInfo structs
            var topicEntries = analysisResult.EsmRecords.RuntimeEditorIds
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
                Path.GetFileName(dumpPath!),
                diag.ValidPointerCount,
                diag.LinkFromNonZeroHead, diag.LinkFromPositiveDecodes,
                diag.LinkToNonZeroHead, diag.LinkToPositiveDecodes,
                diag.FollowUpNonZeroHead, diag.FollowUpPositiveDecodes));
        }

        // Write diagnostic output to TestResults for post-hoc inspection
        var diagLines = allDiagnostics.Select(d =>
            $"{d.DumpName}: ConvDataPtrs={d.ValidPtrs}, " +
            $"LinkFrom(heads={d.LinkFromHeads}, decodes={d.LinkFromDecodes}), " +
            $"LinkTo(heads={d.LinkToHeads}, decodes={d.LinkToDecodes}), " +
            $"FollowUp(heads={d.FollowUpHeads}, decodes={d.FollowUpDecodes})").ToList();
        var diagPath = Path.Combine(AppContext.BaseDirectory, "TestResults", "conversation_data_diagnostics.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(diagPath)!);
        await File.WriteAllLinesAsync(diagPath, diagLines);

        // Verify: at least one dump had valid TESConversationData pointers
        Assert.True(
            allDiagnostics.Any(d => d.ValidPtrs > 0),
            $"Expected valid TESConversationData pointers in at least one dump. " +
            $"Diagnostics:\n{string.Join("\n", diagLines)}");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task RuntimeReader_OnAllDumps_ReadsPerkSkillStatForSpeechChallenges()
    {
        var candidateDumps = new[]
            {
                samples.DebugDump,
                samples.ReleaseDump,
                samples.ReleaseDumpXex4,
                samples.ReleaseDumpXex44,
                SampleFileFixture.FindSamplePath(@"Sample\MemoryDump\Fallout_Release_MemDebug.xex.dmp")
            }
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(candidateDumps.Count == 0, "No memory dumps available");

        var totalSpeechChallengeInfos = 0;
        var totalWithPerkSkillStat = 0;

        foreach (var dumpPath in candidateDumps)
        {
            var analysisResult = await AnalyzeDumpAsync(dumpPath!);
            Assert.NotNull(analysisResult.EsmRecords);
            Assert.NotNull(analysisResult.MinidumpInfo);

            var fileInfo = new FileInfo(dumpPath!);
            using var mmf =
                MemoryMappedFile.CreateFromFile(dumpPath!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
            var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo!,
                analysisResult.EsmRecords!.RuntimeRefrFormEntries,
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x41).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList());

            // Walk DIAL topics → QUEST_INFO → read TESTopicInfo with speech challenges
            var topicEntries = analysisResult.EsmRecords.RuntimeEditorIds
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

        // Write pPerkSkillStat diagnostic alongside conversation data diagnostics
        var perkDiagPath = Path.Combine(AppContext.BaseDirectory, "TestResults", "perk_skill_stat_diagnostics.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(perkDiagPath)!);
        await File.WriteAllTextAsync(perkDiagPath,
            $"Total speech challenge INFOs: {totalSpeechChallengeInfos}\n" +
            $"Total with pPerkSkillStat: {totalWithPerkSkillStat}\n");

        // Expect speech challenges to exist in at least one dump
        Assert.True(totalSpeechChallengeInfos > 0,
            "Expected at least one speech challenge INFO across dump families");

        // Expect a meaningful proportion to have valid pPerkSkillStat pointers
        Assert.True(totalWithPerkSkillStat > 0,
            $"Expected at least one speech challenge INFO with pPerkSkillStat. " +
            $"Found {totalSpeechChallengeInfos} speech challenges, {totalWithPerkSkillStat} with pPerkSkillStat.");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task RuntimeReader_OnAllDumps_ReadsProjectilePhysicsData()
    {
        var candidateDumps = new[]
            {
                samples.DebugDump,
                samples.ReleaseDump,
                samples.ReleaseDumpXex4,
                samples.ReleaseDumpXex44,
                SampleFileFixture.FindSamplePath(@"Sample\MemoryDump\Fallout_Release_MemDebug.xex.dmp")
            }
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(candidateDumps.Count == 0, "No memory dumps available");

        var allResults = new List<(string DumpName, int TotalProj, int Decoded, int WithSpeed, int WithExplosion,
            int WithLight, int WithCountdownSound, int WithDefaultWeapon, int WithRotation, int WithBounce)>();

        foreach (var dumpPath in candidateDumps)
        {
            var analysisResult = await AnalyzeDumpAsync(dumpPath!);
            Assert.NotNull(analysisResult.EsmRecords);
            Assert.NotNull(analysisResult.MinidumpInfo);

            var fileInfo = new FileInfo(dumpPath!);
            using var mmf =
                MemoryMappedFile.CreateFromFile(dumpPath!, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
            var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo!,
                analysisResult.EsmRecords!.RuntimeRefrFormEntries,
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x41).ToList(),
                analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList());

            var projEntries = analysisResult.EsmRecords.RuntimeEditorIds
                .Where(entry => entry.FormType == 0x33 && entry.TesFormOffset.HasValue)
                .ToList();

            int decoded = 0, withSpeed = 0, withExplosion = 0, withLight = 0;
            int withCountdownSound = 0, withDefaultWeapon = 0, withRotation = 0, withBounce = 0;

            foreach (var entry in projEntries)
            {
                var data = runtimeReader.ReadProjectilePhysics(entry.TesFormOffset!.Value, entry.FormId);
                if (data == null) continue;
                decoded++;
                if (data.Speed != 0) withSpeed++;
                if (data.ExplosionFormId.HasValue) withExplosion++;
                if (data.LightFormId.HasValue) withLight++;
                if (data.CountdownSoundFormId.HasValue) withCountdownSound++;
                if (data.DefaultWeaponSourceFormId.HasValue) withDefaultWeapon++;
                if (data.RotationX != 0 || data.RotationY != 0 || data.RotationZ != 0) withRotation++;
                if (data.BounceMultiplier != 0) withBounce++;
            }

            allResults.Add((Path.GetFileName(dumpPath!), projEntries.Count, decoded, withSpeed, withExplosion,
                withLight, withCountdownSound, withDefaultWeapon, withRotation, withBounce));
        }

        // Write diagnostics
        var diagPath = Path.Combine(AppContext.BaseDirectory, "TestResults", "proj_runtime_diagnostics.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(diagPath)!);
        var lines = new List<string>
        {
            "DumpFile | Total | Decoded | Speed | Explosion | Light | Countdown | DefWeapon | Rotation | Bounce"
        };
        foreach (var r in allResults)
        {
            lines.Add($"{r.DumpName} | {r.TotalProj} | {r.Decoded} | {r.WithSpeed} | {r.WithExplosion} | " +
                      $"{r.WithLight} | {r.WithCountdownSound} | {r.WithDefaultWeapon} | {r.WithRotation} | {r.WithBounce}");
        }

        await File.WriteAllTextAsync(diagPath, string.Join("\n", lines));

        // At least one dump should have decodable projectiles
        Assert.True(allResults.Any(r => r.Decoded > 0),
            "Expected at least one dump to have decodable PROJ runtime structs");

        // Most decoded projectiles should have non-zero speed (basic physics validation)
        var totalDecoded = allResults.Sum(r => r.Decoded);
        var totalWithSpeed = allResults.Sum(r => r.WithSpeed);
        Assert.True(totalWithSpeed > totalDecoded / 2,
            $"Expected majority of decoded projectiles to have non-zero speed. " +
            $"Decoded: {totalDecoded}, With speed: {totalWithSpeed}");
    }

    private static async Task<AnalysisResult> AnalyzeDumpAsync(string dumpPath)
    {
        var analyzer = new MinidumpAnalyzer();
        return await analyzer.AnalyzeAsync(
            dumpPath,
            includeMetadata: true,
            cancellationToken: TestContext.Current.CancellationToken);
    }
}