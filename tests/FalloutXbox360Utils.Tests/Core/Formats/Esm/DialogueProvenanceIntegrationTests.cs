using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using FalloutXbox360Utils.Core.Minidump;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

public sealed class DialogueProvenanceIntegrationTests(SampleFileFixture samples)
{
    private readonly SampleFileFixture _samples = samples;

    [Fact]
    public async Task Provenance_OnXex44Dump_ResolvesVms42GoodbyeTopic()
    {
        Assert.SkipWhen(_samples.ReleaseDumpXex44 is null, "Release xex44 dump not available");

        var result = await WithParsedDumpAsync(_samples.ReleaseDumpXex44!, (parser, parsed, _) =>
        {
            var info = parsed.Dialogues.FirstOrDefault(dialogue => dialogue.FormId == 0x00146E1C);
            Assert.NotNull(info);

            var topic = parsed.DialogTopics.FirstOrDefault(dialogueTopic => dialogueTopic.FormId == 0x00147493);
            Assert.NotNull(topic);

            var inspector = new DialogueProvenanceInspector(parser._context, parsed.Dialogues);
            return (Info: info!, TopicReport: inspector.InspectTopic(topic!));
        });

        Assert.Contains(0x00147493u, result.Info.LinkToTopics);
        Assert.Equal("Goodbye.", result.TopicReport.DecodedText);
    }

    [Fact]
    public async Task Provenance_OnXex44Dump_ClassifiesMichelleBarterAsMissingTesFilePage()
    {
        Assert.SkipWhen(_samples.ReleaseDumpXex44 is null, "Release xex44 dump not available");

        var report = await WithParsedDumpAsync(_samples.ReleaseDumpXex44!, (parser, parsed, _) =>
        {
            var info = parsed.Dialogues.FirstOrDefault(dialogue => dialogue.FormId == 0x000E88EF);
            Assert.NotNull(info);

            var inspector = new DialogueProvenanceInspector(parser._context, parsed.Dialogues);
            return inspector.InspectInfo(info!);
        });

        Assert.True(report.Dialogue.TesFileOffset > 0);
        Assert.Equal(DialogueTesFileScriptRecoveryStatus.MappedPageMissing, report.ResultScriptRecovery.Status);
    }

    [Fact]
    public async Task ProtoEsm_MichelleBarterInfo_ContainsShowBarterMenuGroundTruth()
    {
        Assert.SkipWhen(_samples.Xbox360ProtoEsm is null, "Xbox 360 proto ESM not available");

        var dialogue = await WithParsedEsmAsync(_samples.Xbox360ProtoEsm!,
            parsed => { return parsed.Dialogues.FirstOrDefault(info => info.FormId == 0x000E88EF); });

        Assert.NotNull(dialogue);
        Assert.Contains(
            dialogue!.ResultScripts,
            script => (script.SourceText ?? script.DecompiledText ?? string.Empty)
                .Contains("Showbartermenu", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<T> WithParsedDumpAsync<T>(
        string dumpPath,
        Func<RecordParser, RecordCollection, AnalysisResult, T> projector)
    {
        var analyzer = new MinidumpAnalyzer();
        var analysis = await analyzer.AnalyzeAsync(dumpPath, null, true, false, CancellationToken.None);

        using var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, new FileInfo(dumpPath).Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysis.EsmRecords!,
            analysis.FormIdMap,
            accessor,
            new FileInfo(dumpPath).Length,
            analysis.MinidumpInfo);
        var parsed = parser.ParseAll();
        return projector(parser, parsed, analysis);
    }

    private static async Task<T> WithParsedEsmAsync<T>(
        string esmPath,
        Func<RecordCollection, T> projector)
    {
        var analysis = await EsmFileAnalyzer.AnalyzeAsync(esmPath, null, CancellationToken.None);

        using var mmf = MemoryMappedFile.CreateFromFile(esmPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, new FileInfo(esmPath).Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(analysis.EsmRecords!, analysis.FormIdMap, accessor, new FileInfo(esmPath).Length);
        var parsed = parser.ParseAll();
        return projector(parsed);
    }
}