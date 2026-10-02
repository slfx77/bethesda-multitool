using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

public sealed class RuntimeImportExeTests
{
    [Fact]
    public async Task ScriptReportsPreserveTraceAndExistingOutputWithCleanJsonStdout()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var trace = Path.Combine(directory.Path, "trace.jsonl");
        var calls = Path.Combine(directory.Path, "calls.json");
        var map = Path.Combine(directory.Path, "map.json");
        var commands = Path.Combine(directory.Path, "commands.json");
        var source = Path.Combine(directory.Path, "FalloutNV.esm");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(trace, """
            {"kind":"capture-header","schema":"bmt/runtime-trace","version":1}
            {"kind":"capture-start","protocol":1,"sequence":1,"dropped":0}
            {"kind":"script-entry","protocol":1,"sequence":2,"dropped":0,"callId":1,"threadId":4,"requestId":1,"parentCallId":0,"depth":0,"scriptFormId":4660,"scriptAddress":1048576}
            {"kind":"command-execute","protocol":1,"sequence":3,"dropped":0,"command":"GetDead","opcode":1046,"threadId":4,"requestId":1,"observedScriptCallId":1,"scriptFormId":4660,"scriptAddress":1048576,"returnType":0,"returnTypeStatus":"observed","resultStatus":"observed","resultBits":"3ff0000000000000","numericValue":1}
            {"kind":"capture-end","protocol":1,"sequence":4,"dropped":0,"status":"completed"}
            {"kind":"capture-footer","events":4,"dropped":0,"snapshots":0,"errors":0,"status":"completed"}
            """, token);
        // An unbound plugin is hashed, but must never be parsed or assigned an owner.
        await File.WriteAllTextAsync(source, "unbound source", token);
        var original = await File.ReadAllBytesAsync(trace, token);
        CliExeRunner.AssertShippedJsonReflectionIsDisabled();
        var result = await CliExeRunner.RunAsync(["runtime", "import", trace, "--script-calls", calls,
            "--script-map", map, "--source-plugin", source, "--commands", commands], token);
        Assert.True(result.ExitCode == 0, result.Describe());
        using var summary = JsonDocument.Parse(result.StandardOutput);
        Assert.True(summary.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal("Partial", summary.RootElement.GetProperty("scriptCalls").GetProperty("status").GetString());
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(calls, token));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), report.RootElement.GetProperty("traceSha256").GetString());
        Assert.Equal("missing-exit", report.RootElement.GetProperty("calls")[0].GetProperty("diagnostics")[0].GetString());
        using var sourceMap = JsonDocument.Parse(await File.ReadAllTextAsync(map, token));
        Assert.Equal("Unbound", sourceMap.RootElement.GetProperty("binding").GetProperty("status").GetString());
        Assert.Empty(sourceMap.RootElement.GetProperty("blocks").EnumerateArray());
        using var commandReport = JsonDocument.Parse(await File.ReadAllTextAsync(commands, token));
        Assert.Equal("Partial", commandReport.RootElement.GetProperty("status").GetString());
        var commandRow = Assert.Single(commandReport.RootElement.GetProperty("commands").EnumerateArray());
        Assert.Equal("Partial", commandRow.GetProperty("scriptCall").GetProperty("status").GetString());
        Assert.Equal(1.0, commandRow.GetProperty("result").GetProperty("numericValue").GetDouble());
        Assert.Equal("GetDead", commandRow.GetProperty("evidence").GetProperty("command").GetString());
        var reportBytes = await File.ReadAllBytesAsync(calls, token);
        var refused = await CliExeRunner.RunAsync(["runtime", "import", trace, "--script-calls", calls], token);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("already exists", refused.StandardError, StringComparison.Ordinal);
        Assert.Equal(reportBytes, await File.ReadAllBytesAsync(calls, token));
        Assert.Equal(original, await File.ReadAllBytesAsync(trace, token));
    }

    [Theory]
    [InlineData("map-only")]
    [InlineData("plugins-only")]
    [InlineData("same-output")]
    [InlineData("same-commands-output")]
    public async Task InvalidReportArgumentsFailBeforeAnyOutputIsCreated(string variation)
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var output = Path.Combine(directory.Path, "new.json");
        var arguments = new List<string> { "runtime", "import", Path.Combine(directory.Path, "absent.jsonl") };
        if (variation != "plugins-only") arguments.AddRange(["--script-map", output]);
        if (variation != "map-only") arguments.AddRange(["--source-plugin", "absent.esm"]);
        if (variation == "same-output") arguments.AddRange(["--script-calls", output]);
        if (variation == "same-commands-output") arguments.AddRange(["--commands", output]);
        var result = await CliExeRunner.RunAsync(arguments.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(variation is "same-output" or "same-commands-output" ? "distinct" : "complete ordered", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }
}
