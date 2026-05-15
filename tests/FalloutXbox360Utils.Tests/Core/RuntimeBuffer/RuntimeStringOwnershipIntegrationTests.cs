using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.RuntimeBuffer;

[Collection(SequentialIntegrationGroup.Name)]
public sealed class RuntimeStringOwnershipIntegrationTests(SampleFileFixture samples)
{
    private readonly SampleFileFixture _samples = samples;

    [Fact]
    public async Task Xex44Dump_StringOwnershipSummary_FlowsThroughExtractionReporter()
    {
        Assert.SkipWhen(_samples.ReleaseDumpXex44 is null, "Release xex44 dump not available");

        var dumpPath = _samples.ReleaseDumpXex44!;
        var analyzer = new MinidumpAnalyzer();
        var analysis = await analyzer.AnalyzeAsync(
            dumpPath,
            null,
            true,
            false,
            TestContext.Current.CancellationToken);

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

            var ownershipSummary =
                await File.ReadAllTextAsync(ownershipSummaryPath, TestContext.Current.CancellationToken);
            Assert.Contains("Runtime String Ownership Summary", ownershipSummary);
            Assert.DoesNotContain("Analyzed String Hits: 0", ownershipSummary);

            Assert.True(
                File.Exists(Path.Combine(esmDir, "string_unknown_owners.csv")) ||
                File.Exists(Path.Combine(esmDir, "string_unreferenced.csv")),
                "Expected at least one non-owned string ownership CSV.");
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
