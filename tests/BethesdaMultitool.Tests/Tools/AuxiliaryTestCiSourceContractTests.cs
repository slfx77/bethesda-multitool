using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Tools;

public sealed class AuxiliaryTestCiSourceContractTests
{
    [Fact]
    public void ReleaseSolutionDirectlyOwnsProjectsReferencedByTheMainTests()
    {
        var solution = SourceContract.ReadSource("BethesdaMultitool.slnx");

        Assert.Contains(
            "<Project Path=\"src/BethesdaMultitool/BethesdaMultitool.csproj\" />",
            solution,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Project Path=\"tools/EsmAnalyzer/EsmAnalyzer.csproj\" />",
            solution,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Project Path=\"tools/Shared/Shared.csproj\" />",
            solution,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CiRunsTestProjectsThatAreNotInTheSolution()
    {
        var workflow = SourceContract.ReadSource(".github", "workflows", "build-and-test.yml");

        Assert.Contains(
            "tools/EsmSchemaGen.Tests/EsmSchemaGen.Tests.csproj",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "src/DDXConv/DDXConv.Tests/DDXConv.Tests.csproj",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--report-xunit-trx-filename esm-schema-gen-test-results.trx",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--report-xunit-trx-filename ddxconv-test-results.trx",
            workflow,
            StringComparison.Ordinal);
    }
}