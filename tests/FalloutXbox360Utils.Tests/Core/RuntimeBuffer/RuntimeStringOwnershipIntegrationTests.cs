using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.RuntimeBuffer;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.RuntimeBuffer;

public sealed class RuntimeStringOwnershipIntegrationTests(SampleFileFixture samples)
{
    private readonly SampleFileFixture _samples = samples;

    [Fact]
    public async Task Xex44Dump_StringOwnershipReports_AreNonEmptyAndFlowThroughExtractionReporter()
    {
        Assert.SkipWhen(_samples.ReleaseDumpXex44 is null, "Release xex44 dump not available");

        var dumpPath = _samples.ReleaseDumpXex44!;
        var analyzer = new MinidumpAnalyzer();
        var analysis = await analyzer.AnalyzeAsync(dumpPath, null, true, false, CancellationToken.None);

        using var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, new FileInfo(dumpPath).Length, MemoryMappedFileAccess.Read);

        var stringData = RuntimeStringReportHelper.Extract(analysis, accessor);
        Assert.NotNull(stringData);
        Assert.NotEmpty(stringData!.OwnershipAnalysis.AllHits);
        Assert.True(
            stringData.OwnershipAnalysis.ReferencedOwnerUnknownHits.Count > 0 ||
            stringData.OwnershipAnalysis.UnreferencedHits.Count > 0);

        var extractDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(extractDir);

        try
        {
            var outputs = await MinidumpExtractionReporter.GenerateEsmOutputsAsync(
                analysis,
                dumpPath,
                extractDir,
                null);

            Assert.True(outputs.reportGenerated);

            var esmDir = Path.Combine(extractDir, "esm_data");
            var ownershipSummaryPath = Path.Combine(esmDir, "string_ownership_summary.txt");

            Assert.True(File.Exists(ownershipSummaryPath));

            var ownershipSummary = await File.ReadAllTextAsync(ownershipSummaryPath);
            Assert.Contains("Runtime String Ownership Summary", ownershipSummary);
        }
        finally
        {
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
            }
        }
    }
}
