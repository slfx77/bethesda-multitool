using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Esm;

/// <summary>
///     Runs <c>esm &lt;file&gt; -f json</c> through the SHIPPED exe (see <see cref="CliExeRunner" />). Its JSON
///     used to serialize an anonymous type through the reflection-based JsonSerializer — which the trimmed
///     exe disables — and its "Scanning records..." status lines shared stdout with the document.
/// </summary>
public sealed class EsmCommandJsonExeSmokeTests
{
    [Fact]
    public async Task EsmJsonFormat_RunsUnderShippedRuntimeConfig()
    {
        CliExeRunner.AssertShippedJsonReflectionIsDisabled();
        using var directory = CliExeRunner.CreateTempDirectory();
        var plugin = CliExeRunner.WriteFalloutNvEsm(directory, PackagesCommandJsonExeSmokeTests.BuildSmokePlugin());

        var result = await CliExeRunner.RunAsync(
            ["--plain", "esm", plugin, "-f", "json"],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.DoesNotContain("Reflection-based serialization", result.StandardError, StringComparison.Ordinal);

        // The status lines still print, but on stderr: stdout is the document alone.
        Assert.Contains("Scanning records", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Scanning records", result.StandardOutput, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("bethesda-multitool/esm-summary", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());

        // TES4 + one STAT + two PACKs; the builder writes HEDR version 1.34 and next object ID 0x800.
        Assert.Equal(4, root.GetProperty("totalRecords").GetInt32());
        var counts = root.GetProperty("recordTypeCounts");
        Assert.Equal(2, counts.GetProperty("PACK").GetInt32());
        Assert.Equal(1, counts.GetProperty("STAT").GetInt32());
        Assert.Equal(1, counts.GetProperty("TES4").GetInt32());

        var header = root.GetProperty("header");
        Assert.Equal(1.34f, header.GetProperty("version").GetSingle());
        Assert.Equal(0x800, header.GetProperty("nextObjectId").GetInt32());
        Assert.Equal(0, header.GetProperty("masters").GetArrayLength());
    }
}
