using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeCommandTraceTests
{
    [Fact]
    public async Task ReportPreservesExactCommandAndCoverageEvidence()
    {
        var command = Command();
        command["futureField"] = "retained";
        var (document, bytes) = await Import([Call(true), command, Call(false)], coverage: true);
        var report = RuntimeCommandTrace.Build(document);
        Assert.Equal("Observed", report.Status);
        var observed = Assert.Single(report.Commands);
        Assert.Equal(4, observed.Line);
        Assert.Equal(3UL, observed.Sequence);
        Assert.Equal(1UL, document.ScriptCalls.Calls[0].RequestId);
        Assert.Equal("Observed", observed.ScriptCall.Status);
        Assert.Equal(3, observed.ScriptCall.Entry!.Line);
        Assert.Equal(5, observed.ScriptCall.Exit!.Line);
        Assert.Equal("Observed", observed.Result.Status);
        Assert.Equal(1.0, observed.Result.NumericValue);
        Assert.Equal("3ff0000000000000", observed.Result.ResultBits);
        Assert.Equal("retained", observed.Evidence.GetProperty("futureField").GetString());
        Assert.Equal(document.ReadSourceLine(document.Events[2]), observed.Evidence.GetRawText());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), report.TraceSha256);
        Assert.Equal(2, report.Coverage.Count);
        Assert.Null(report.Coverage[0].Line);
        Assert.True(report.Coverage[1].Evidence.GetProperty("truncated").GetBoolean());
        Assert.Equal(RuntimeCommandTrace.Serialize(report), RuntimeCommandTrace.Serialize(RuntimeCommandTrace.Build(document)));
        using var json = JsonDocument.Parse(RuntimeCommandTrace.Serialize(report));
        Assert.Equal("retained", json.RootElement.GetProperty("commands")[0].GetProperty("evidence")
            .GetProperty("futureField").GetString());
    }

    [Theory]
    [InlineData(0u, "0000000000000000", 0.0, "Observed", "Default")]
    [InlineData(1u, "0000000000000014", null, "Observed", "Form")]
    [InlineData(2u, "7ff8000000000000", null, "Observed", "String")]
    [InlineData(3u, "0000000000000001", null, "Observed", "Array")]
    [InlineData(4u, "0000000000000001", null, "Observed", "ArrayIndex")]
    [InlineData(5u, "0000000000000001", null, "Observed", "Ambiguous")]
    [InlineData(0u, "7ff0000000000000", null, "Observed", "Default")]
    [InlineData(0u, "7ff8000000000000", 1.0, "Ambiguous", "Default")]
    [InlineData(0u, "3ff0000000000000", 2.0, "Ambiguous", "Default")]
    [InlineData(1u, "3ff0000000000000", 1.0, "Ambiguous", "Form")]
    [InlineData(6u, "0000000000000000", null, "Partial", "Unknown")]
    [InlineData(null, "0000000000000000", null, "Partial", "Unavailable")]
    [InlineData(0u, "xyz", null, "Ambiguous", "Default")]
    [InlineData(0u, null, null, "Unavailable", "Default")]
    public async Task NumericResultsRequireExactDefaultTypeBits(uint? type, string? bits, double? number,
        string status, string name)
    {
        var row = Command();
        row["returnType"] = type; row["resultBits"] = bits; row["numericValue"] = number;
        var (document, _) = await Import([row]);
        var result = Assert.Single(RuntimeCommandTrace.Build(document).Commands).Result;
        Assert.Equal(status, result.Status);
        Assert.Equal(name, result.ReturnTypeName);
        Assert.Equal(bits, result.ResultBits);
        Assert.Equal(status == "Observed" && type == 0 ? number : null, result.NumericValue);
    }

    [Theory]
    [InlineData("unknown-call", "Unavailable", "script-call-unavailable")]
    [InlineData("thread", "Ambiguous", "command-call-identity-mismatch")]
    [InlineData("request", "Ambiguous", "command-call-identity-mismatch")]
    [InlineData("script", "Ambiguous", "command-call-identity-mismatch")]
    [InlineData("address", "Ambiguous", "command-call-identity-mismatch")]
    [InlineData("missing-request", "Unavailable", "command-call-identity-unavailable")]
    [InlineData("after-exit", "Ambiguous", "command-outside-call")]
    [InlineData("before-entry", "Ambiguous", "command-outside-call")]
    [InlineData("missing-exit", "Partial", "script-call-partial")]
    [InlineData("native-unavailable", "Unavailable", "native-script-call-unavailable")]
    [InlineData("changed-script", "Ambiguous", "command-script-after-mismatch")]
    [InlineData("unreadable-script", "Unavailable", "command-script-before-unavailable")]
    [InlineData("event-loss", "Partial", "capture-event-loss")]
    [InlineData("duplicate-entry", "Ambiguous", "script-call-ambiguous")]
    [InlineData("duplicate-exit", "Ambiguous", "script-call-ambiguous")]
    public async Task CallAttributionRejectsMissingAndConflictingIdentity(string variation, string status, string diagnostic)
    {
        var row = Command();
        var entry = Call(true); var exit = Call(false);
        var rows = new List<JsonObject> { entry, row, exit };
        switch (variation)
        {
            case "unknown-call": row["observedScriptCallId"] = 77; break;
            case "thread": row["threadId"] = 8; break;
            case "request": row["requestId"] = 2; break;
            case "script": row["scriptFormId"] = 9; break;
            case "address": row["scriptAddress"] = 99; break;
            case "missing-request": row.Remove("requestId"); break;
            case "after-exit": rows = [entry, exit, row]; break;
            case "before-entry": rows = [row, entry, exit]; break;
            case "missing-exit": rows.Remove(exit); break;
            case "native-unavailable": row["scriptCallStatus"] = "Unavailable"; break;
            case "changed-script": row["commandLocation"]!["after"]!["script"]!["formId"] = 99; break;
            case "unreadable-script": row["commandLocation"]!["before"]!["script"]!["status"] = "unreadable"; break;
            case "duplicate-entry": rows.Insert(1, (JsonObject)entry.DeepClone()); break;
            case "duplicate-exit": rows.Add((JsonObject)exit.DeepClone()); break;
        }
        var (document, _) = await Import(rows, loss: variation == "event-loss");
        var association = Assert.Single(RuntimeCommandTrace.Build(document).Commands).ScriptCall;
        Assert.Equal(status, association.Status);
        Assert.Contains(diagnostic, association.Diagnostics);
    }

    [Fact]
    public async Task ACommandInsideANestedCallCannotClaimItsOuterCall()
    {
        var inner = Call(true, 2, 1, 1); var innerExit = Call(false, 2, 1, 1);
        var (document, _) = await Import([Call(true), inner, Command(), innerExit, Call(false)]);
        var association = Assert.Single(RuntimeCommandTrace.Build(document).Commands).ScriptCall;
        Assert.Equal("Ambiguous", association.Status);
        Assert.Contains("command-call-not-innermost", association.Diagnostics);
    }

    [Fact]
    public async Task AnonymousScriptAndFalseHandlerReturnRemainObservedWithoutSourceOwnership()
    {
        var entry = Call(true); var exit = Call(false); var command = Command();
        foreach (var row in new[] { entry, exit, command }) row["scriptFormId"] = 0;
        command["commandLocation"]!["before"]!["script"]!["formId"] = 0;
        command["commandLocation"]!["after"]!["script"]!["formId"] = 0;
        command["handlerReturned"] = false;
        var (document, _) = await Import([entry, command, exit]);
        var observation = Assert.Single(RuntimeCommandTrace.Build(document).Commands);
        Assert.Equal("Observed", observation.ScriptCall.Status);
        Assert.Equal("Observed", observation.Result.Status);
        Assert.False(observation.HandlerReturned);
        Assert.Equal(0U, observation.ScriptFormId);
    }

    [Theory]
    [InlineData("message-choice-read", "command")]
    [InlineData("condition-function", "function")]
    public async Task LegacyEventsRetainTheirValueWithoutInventingATypedResult(string kind, string nameField)
    {
        var (document, _) = await Import([new JsonObject { ["kind"] = kind,
            [nameField] = "GetButtonPressed", ["value"] = -1.0 }]);
        var report = RuntimeCommandTrace.Build(document);
        var observation = Assert.Single(report.Commands);
        Assert.Equal("GetButtonPressed", observation.Command);
        Assert.Equal("Unavailable", observation.Result.Status);
        Assert.Null(observation.Result.NumericValue);
        Assert.Equal(-1.0, observation.Evidence.GetProperty("value").GetDouble());
        Assert.Equal("Unavailable", report.Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => RuntimeCommandTrace.Build(document, cancelled.Token));
    }

    private static JsonObject Call(bool entry, ulong id = 1, ulong parent = 0, uint depth = 0) => new()
    {
        ["kind"] = entry ? "script-entry" : "script-exit", ["callId"] = id, ["threadId"] = 4,
        ["parentCallId"] = parent, ["depth"] = depth, ["scriptFormId"] = 0x1234,
        ["scriptAddress"] = 0x100000, ["requestId"] = 1, ["returned"] = true
    };

    private static JsonObject Command() => new()
    {
        ["kind"] = "command-execute", ["command"] = "GetDead", ["opcode"] = 1046,
        ["handlerReturned"] = true, ["threadId"] = 4, ["requestId"] = 1,
        ["scriptFormId"] = 0x1234, ["scriptAddress"] = 0x100000, ["observedScriptCallId"] = 1,
        ["scriptCallStatus"] = "observed-scope", ["returnType"] = 0, ["returnTypeStatus"] = "observed",
        ["resultStatus"] = "observed", ["resultBits"] = "3ff0000000000000", ["numericValue"] = 1.0,
        ["commandLocation"] = new JsonObject
        {
            ["before"] = new JsonObject { ["script"] = new JsonObject { ["status"] = "observed", ["formId"] = 0x1234, ["address"] = 0x100000 } },
            ["after"] = new JsonObject { ["script"] = new JsonObject { ["status"] = "observed", ["formId"] = 0x1234, ["address"] = 0x100000 } }
        }
    };

    private static async Task<(RuntimeTraceDocument Document, byte[] Bytes)> Import(
        IEnumerable<JsonObject> observations, bool loss = false, bool coverage = false)
    {
        var rows = new List<JsonObject> { new() { ["kind"] = "capture-start" } };
        rows.AddRange(observations);
        if (coverage) rows.Add(new JsonObject { ["kind"] = "command-hook-coverage", ["part"] = 1, ["truncated"] = true });
        rows.Add(new JsonObject { ["kind"] = "capture-end", ["status"] = "completed" });
        for (var i = 0; i < rows.Count; ++i)
        { rows[i]["protocol"] = 1; rows[i]["sequence"] = i + 1; rows[i]["dropped"] = loss ? 1 : 0; }
        var header = new JsonObject { ["kind"] = "capture-header", ["schema"] = "bmt/runtime-trace", ["version"] = 1 };
        if (coverage) header["identity"] = new JsonObject { ["commandTraceCoverage"] = new JsonObject { ["status"] = "partial" } };
        var lines = new List<string> { header.ToJsonString() };
        lines.AddRange(rows.Select(row => row.ToJsonString()));
        lines.Add(new JsonObject { ["kind"] = "capture-footer", ["status"] = "completed", ["events"] = rows.Count,
            ["dropped"] = loss ? 1 : 0, ["snapshots"] = 0, ["errors"] = 0 }.ToJsonString());
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        await using var stream = new MemoryStream(bytes);
        return (await RuntimeTraceImporter.ReadDocumentAsync(stream, TestContext.Current.CancellationToken), bytes);
    }
}
