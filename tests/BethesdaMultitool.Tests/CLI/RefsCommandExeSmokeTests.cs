using System.Text.Json;
using BethesdaMultitool.Tests.Core.Formats.Esm.Xref;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

public sealed class RefsCommandExeSmokeTests
{
    [Fact]
    public async Task JsonReferencesAndBoundedPathsAreOneShippedDocumentWithoutPlainFlag()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(directory, ReferenceQueryTests.TerminalChain());
        var result = await CliExeRunner.RunAsync(["refs", path, "0x00009000", "--format:json", "--root",
            "RootTerminal", "--max-depth", "1"], TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.Describe());
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Single(json.RootElement.GetProperty("inbound").EnumerateArray());
        Assert.Single(json.RootElement.GetProperty("outbound").EnumerateArray());
        var graph = json.RootElement.GetProperty("staticReferencePaths");
        Assert.Equal(2, graph.GetProperty("nodes").GetArrayLength());
        Assert.Equal(1, graph.GetProperty("depthBoundaries").GetInt32());
    }

    [Fact]
    public async Task ZeroReferencesReportsCoverageAndDoesNotClaimUnused()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(directory, ReferenceQueryTests.TerminalChain());
        var result = await CliExeRunner.RunAsync(["refs", path, "0x123456", "--format", "json"], TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.Describe());
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Empty(json.RootElement.GetProperty("inbound").EnumerateArray());
        Assert.Contains("not proof", json.RootElement.GetProperty("coverage").GetProperty("caveat").GetString());
    }

    [Fact]
    public async Task InvalidDepthReportsAnErrorWithoutAStdoutDocument()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(directory, ReferenceQueryTests.TerminalChain());
        var result = await CliExeRunner.RunAsync(["refs", path, "0x8000", "--format", "json", "--root", "0x8000",
            "--max-depth", "-1"], TestContext.Current.CancellationToken);
        Assert.NotEqual(0, result.ExitCode); Assert.Equal("", result.StandardOutput);
        Assert.Contains("--max-depth", result.StandardError);
    }

    [Fact]
    public async Task MalformedPluginHasACommandDiagnosticAndNoStdoutDocument()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var path = Path.Combine(directory.Path, "FalloutNV.esm"); File.WriteAllBytes(path, new byte[24]);
        var result = await CliExeRunner.RunAsync(["refs", path, "0x8000", "--format", "json"], TestContext.Current.CancellationToken);
        Assert.Equal(1, result.ExitCode); Assert.Equal("", result.StandardOutput);
        Assert.Contains("refs: Explicit load order requires TES4-family plugins", result.StandardError);
    }
}
