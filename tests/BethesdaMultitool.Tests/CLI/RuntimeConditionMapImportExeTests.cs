using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

public sealed class RuntimeConditionMapImportExeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Optional_condition_map_preserves_summary_trace_and_existing_report(bool withScriptMap)
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var token = TestContext.Current.CancellationToken;
        var trace = Path.Combine(directory.Path, "trace.jsonl");
        var source = Path.Combine(directory.Path, "FalloutNV.esm");
        var output = Path.Combine(directory.Path, "conditions.json");
        await File.WriteAllTextAsync(trace, """
            {"kind":"capture-header","schema":"bmt/runtime-trace","version":1}
            {"kind":"capture-start","protocol":1,"sequence":1,"dropped":0}
            {"kind":"capture-end","protocol":1,"sequence":2,"dropped":0,"status":"completed"}
            {"kind":"capture-footer","events":2,"dropped":0,"snapshots":0,"errors":0,"status":"completed"}
            """, token);
        await File.WriteAllTextAsync(source, "unbound source is not parsed", token);
        var original = await File.ReadAllBytesAsync(trace, token);
        var args = new List<string> { "runtime", "import", trace, "--condition-map", output, "--source-plugin", source };
        if (withScriptMap) args.AddRange(["--script-map", Path.Combine(directory.Path, "scripts.json")]);
        CliExeRunner.AssertShippedJsonReflectionIsDisabled();
        var result = await CliExeRunner.RunAsync(args.ToArray(), token);
        Assert.True(result.ExitCode == 0, result.Describe());
        using var summary = JsonDocument.Parse(result.StandardOutput);
        Assert.True(summary.RootElement.GetProperty("complete").GetBoolean());
        var reportBytes = await File.ReadAllBytesAsync(output, token);
        using var report = JsonDocument.Parse(reportBytes);
        Assert.Equal("Unbound", report.RootElement.GetProperty("binding").GetProperty("status").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), report.RootElement.GetProperty("traceSha256").GetString());
        Assert.Empty(report.RootElement.GetProperty("records").EnumerateArray());
        var refused = await CliExeRunner.RunAsync(args.ToArray(), token);
        Assert.Equal(1, refused.ExitCode); Assert.Contains("already exists", refused.StandardError, StringComparison.Ordinal);
        Assert.Equal(reportBytes, await File.ReadAllBytesAsync(output, token));
        Assert.Equal(original, await File.ReadAllBytesAsync(trace, token));
    }

    [Theory]
    [InlineData("missing-sources")]
    [InlineData("same-map-output")]
    public async Task Invalid_condition_map_arguments_do_not_create_reports(string variation)
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var output = Path.Combine(directory.Path, "new.json");
        var args = new List<string> { "runtime", "import", "absent.jsonl", "--condition-map", output };
        if (variation == "same-map-output") args.AddRange(["--script-map", output, "--source-plugin", "absent.esm"]);
        var result = await CliExeRunner.RunAsync(args.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(variation == "missing-sources" ? "complete ordered" : "distinct", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }
}
