using System.Text.Json;
using BethesdaMultitool.Tests.CLI.Commands.Esm;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

public sealed class CliOutputRoutingExeTests
{
    [Theory]
    [InlineData("esm", "semdiff", "a.esm", "b.esm", "--format", "json", "--match", "bogus")]
    [InlineData("esm", "packages", "a.esm", "-f:json", "-l", "abc")]
    [InlineData("esm", "packages", "-f", "json")]
    public async Task JsonParseErrorsKeepHelpAndSuggestionsOffStdout(params string[] arguments)
    {
        var result = await CliExeRunner.RunAsync(arguments, TestContext.Current.CancellationToken);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardError));
    }

    [Fact]
    public async Task ColonJsonWithResourceStatsWritesExactlyOneDocumentAndStatsOnStderr()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var payload = new byte[1024 * 1024];
        PackagesCommandJsonExeSmokeTests.BuildSmokePlugin().Build().CopyTo(payload, 0);
        var dump = Path.Combine(directory.Path, "packages.dmp");
        File.WriteAllBytes(dump, new SyntheticMinidumpBuilder().AddRegion(0x82000000, payload).Build());

        var result = await CliExeRunner.RunAsync(
            ["esm", "packages", dump, "-f:json", "--resource-stats"],
            TestContext.Current.CancellationToken, workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 0, result.Describe());
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("bethesda-multitool/esm-packages", json.RootElement.GetProperty("schema").GetString());
        // The control prevents a vacuous pass on an input that registers no tracked work.
        Assert.Contains("Resource statistics", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorldCellInvalidBracketedFormIdPrintsTheInputLiterally()
    {
        var result = await CliExeRunner.RunAsync(["world", "cell", "missing.esm", "[x]"],
            TestContext.Current.CancellationToken);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.Contains("Invalid FormID: [x]", result.StandardError);
        Assert.DoesNotContain("Unbalanced", result.StandardError);
    }
}
