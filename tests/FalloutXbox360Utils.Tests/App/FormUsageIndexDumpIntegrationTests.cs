using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.App;

[Collection(DumpSerialTestGroup.Name)]
public class FormUsageIndexDumpIntegrationTests(SampleFileFixture samples)
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task RuntimeReader_OnReleaseDump_ReadsDialogueConditionsFromStructs()
    {
        Assert.SkipWhen(samples.ReleaseDump is null, "Release memory dump not available");

        var dumpPath = samples.ReleaseDump!;
        var analyzer = new MinidumpAnalyzer();
        var analysisResult = await analyzer.AnalyzeAsync(
            dumpPath,
            includeMetadata: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(analysisResult.EsmRecords);
        Assert.NotNull(analysisResult.MinidumpInfo);

        var runtimeEntries = analysisResult.EsmRecords!.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x46 && entry.TesFormOffset.HasValue)
            .ToList();
        Assert.NotEmpty(runtimeEntries);

        var fileInfo = new FileInfo(dumpPath);
        using var mmf = MemoryMappedFile.CreateFromFile(
            dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo!,
            analysisResult.EsmRecords.RuntimeRefrFormEntries,
            analysisResult.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList());

        var runtimeInfos = runtimeEntries
            .Select(runtimeReader.ReadRuntimeDialogueInfo)
            .Where(info => info is not null)
            .Select(info => info!)
            .ToList();

        Assert.NotEmpty(runtimeInfos);
        Assert.Contains(runtimeInfos, info => info.Conditions.Count > 0);
        Assert.Contains(runtimeInfos, info => info.ConditionFunctions.Count > 0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Build_OnReleaseDump_IndexesDialogueScriptUses()
    {
        Assert.SkipWhen(samples.ReleaseDump is null, "Release memory dump not available");

        var dumpPath = samples.ReleaseDump!;
        var analyzer = new MinidumpAnalyzer();
        var analysisResult = await analyzer.AnalyzeAsync(
            dumpPath,
            includeMetadata: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(analysisResult.EsmRecords);

        var fileInfo = new FileInfo(dumpPath);
        using var mmf = MemoryMappedFile.CreateFromFile(
            dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords!,
            analysisResult.FormIdMap,
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo);

        var semantic = parser.ParseAll();

        Assert.NotEmpty(semantic.Dialogues);
        Assert.Contains(semantic.Dialogues, d => d.Conditions.Count > 0);
        Assert.Contains(semantic.Dialogues, d => d.ResultScripts.Count > 0);
        Assert.Contains(semantic.Dialogues, d => d.SpeakerFactionFormId is > 0 || d.SpeakerVoiceTypeFormId is > 0);

        var referencedFormId = semantic.Dialogues
            .SelectMany(d => d.ResultScripts)
            .SelectMany(s => s.ReferencedObjects)
            .FirstOrDefault();

        Assert.NotEqual(0u, referencedFormId);

        var usageIndex = FormUsageIndex.Build(semantic);
        Assert.True(usageIndex.GetUseCount(referencedFormId) > 0,
            $"Expected dump-derived result script reference 0x{referencedFormId:X8} to appear in the usage index.");
    }
}