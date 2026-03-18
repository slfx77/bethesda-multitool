using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

[Collection(DumpSerialTestGroup.Name)]
public sealed class RuntimeWorldCellDumpRegressionTests(SampleFileFixture samples)
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Probe_OnDebugDump_IsHighConfidence()
    {
        Assert.SkipWhen(samples.DebugDump is null, "Debug memory dump not available");

        var result = await ProbeDumpAsync(samples.DebugDump!);

        Assert.NotNull(result.Probe);
        Assert.True(result.Probe!.IsHighConfidence);
        Assert.True(result.WorldCellMapCount > 0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Probe_OnReleaseDump_ReturnsCellBackedCandidate()
    {
        Assert.SkipWhen(samples.ReleaseDump is null, "Release memory dump not available");

        var result = await ProbeDumpAsync(samples.ReleaseDump!);

        AssertReleaseBetaFamilyProbe(result);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Probe_OnReleaseDumpXex4_ReturnsCellBackedCandidate()
    {
        Assert.SkipWhen(samples.ReleaseDumpXex4 is null, "Release xex4 memory dump not available");

        var result = await ProbeDumpAsync(samples.ReleaseDumpXex4!);

        AssertReleaseBetaFamilyProbe(result);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Probe_OnReleaseDumpXex44_ReturnsCellBackedCandidate()
    {
        Assert.SkipWhen(samples.ReleaseDumpXex44 is null, "Release xex44 memory dump not available");

        var result = await ProbeDumpAsync(samples.ReleaseDumpXex44!);

        AssertReleaseBetaFamilyProbe(result);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Probe_OnMemDebugDump_IsHighConfidence()
    {
        var memDebugDump = SampleFileFixture.FindSamplePath(@"Sample\MemoryDump\Fallout_Release_MemDebug.xex.dmp");
        Assert.SkipWhen(memDebugDump is null, "MemDebug memory dump not available");

        var result = await ProbeDumpAsync(memDebugDump!);

        Assert.NotNull(result.Probe);
        Assert.True(result.Probe!.IsHighConfidence);
        Assert.True(result.WorldCellMapCount > 0);
    }

    private static void AssertReleaseBetaFamilyProbe(
        (RuntimeWorldCellLayoutProbeResult? Probe, int WorldCellMapCount, int CellEntryCount, int RuntimeCellSignalCount
            ) result)
    {
        Assert.NotNull(result.Probe);
        Assert.True(result.Probe!.WinnerScore > 0);
        Assert.True(result.Probe.SampleCount > 0);
        Assert.True(result.CellEntryCount > 0);
        Assert.True(result.RuntimeCellSignalCount > 0);
    }

    private static async
        Task<(RuntimeWorldCellLayoutProbeResult? Probe, int WorldCellMapCount, int CellEntryCount, int
            RuntimeCellSignalCount)> ProbeDumpAsync(string dumpPath)
    {
        var fileInfo = new FileInfo(dumpPath);
        var analyzer = new MinidumpAnalyzer();
        var analysisResult = await analyzer.AnalyzeAsync(
            dumpPath,
            includeMetadata: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(analysisResult.EsmRecords);
        Assert.NotNull(analysisResult.MinidumpInfo);

        using var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var worldEntries = analysisResult.EsmRecords!.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x41 && entry.TesFormOffset.HasValue)
            .ToList();
        var cellEntries = analysisResult.EsmRecords.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x39 && entry.TesFormOffset.HasValue)
            .ToList();

        Assert.True(worldEntries.Count > 0 || cellEntries.Count > 0, "Expected runtime WRLD or CELL entries.");

        var reader = RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo!,
            analysisResult.EsmRecords.RuntimeRefrFormEntries,
            null,
            worldEntries,
            cellEntries);

        var worldCellMaps = reader.ReadAllWorldspaceCellMaps(worldEntries);
        var runtimeCellSignalCount = cellEntries
            .Select(reader.ReadRuntimeCell)
            .Where(cell => cell is not null)
            .Count(cell => cell!.WorldspaceFormId is > 0 || cell.WaterHeight is not null || cell.Flags != 0);

        return (reader.WorldCellLayoutProbe, worldCellMaps.Count, cellEntries.Count, runtimeCellSignalCount);
    }
}