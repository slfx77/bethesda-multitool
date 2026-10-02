using System.Xml.Linq;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Tools;

public sealed class AuxiliaryTestCiSourceContractTests
{
    [Fact]
    public void ReleaseSolutionDirectlyOwnsProjectsReferencedByTheMainTests()
    {
        var solution = XDocument.Parse(SourceContract.ReadSource("BethesdaMultitool.slnx"));
        var projects = solution.Descendants("Project")
            .Select(project => (string?)project.Attribute("Path"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("src/BethesdaMultitool/BethesdaMultitool.csproj", projects);
        Assert.Contains("tools/EsmAnalyzer/EsmAnalyzer.csproj", projects);
        Assert.Contains("tools/Shared/Shared.csproj", projects);
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

    [Fact]
    public void SolutionMapsEveryReferencedProjectToTheSameX64Configuration()
    {
        var solution = XDocument.Parse(SourceContract.ReadSource("BethesdaMultitool.slnx"));
        var projects = solution.Descendants("Project").ToDictionary(
            project => ResolveProjectPath(SourceContract.RepoRoot, (string)project.Attribute("Path")!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (path, project) in projects)
        {
            Assert.Contains(project.Elements("Platform"),
                platform => (string?)platform.Attribute("Project") == "x64");
            var document = XDocument.Parse(SourceContract.ReadSourceFile(path));
            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var referencedPath = ResolveProjectPath(Path.GetDirectoryName(path)!,
                    (string)reference.Attribute("Include")!);
                Assert.True(projects.ContainsKey(referencedPath),
                    $"{Path.GetRelativePath(SourceContract.RepoRoot, referencedPath)} is outside the solution. " +
                    "MSBuild clears inherited Configuration/Platform for missing solution projects, " +
                    "silently building Debug/AnyCPU dependencies in a Release/x64 build.");
            }
        }
    }

    private static string ResolveProjectPath(string directory, string path) =>
        Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), directory);
}
