using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeScriptCallTests
{
    [Fact]
    public async Task NestedAndInterleavedCallsKeepTheirExactEvidence()
    {
        var rows = new[] { Call(true, 1), Call(true, 2, 1, 1), Call(true, 3, thread: 20),
            Call(false, 2, 1, 1), Call(false, 3, thread: 20), Call(false, 1) };
        var (document, bytes) = await Import(rows);
        Assert.True(document.Summary.Complete);
        Assert.Equal("Observed", document.ScriptCalls.Summary.Status);
        Assert.Equal(3, document.ScriptCalls.Summary.Observed);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), document.ScriptCalls.TraceSha256);
        Assert.Equal(new[] { 3, 8 }, document.ScriptCalls.Calls[0].EvidenceLines);
        Assert.Equal(new[] { 4, 6 }, document.ScriptCalls.Calls[1].EvidenceLines);
        Assert.Equal(1UL, document.ScriptCalls.Calls[1].ParentCallId);
        Assert.Equal(20U, document.ScriptCalls.Calls[2].ThreadId);
        Assert.Equal(6UL, document.ScriptCalls.Calls[1].Exit!.Sequence);
        Assert.Contains("\"callId\":2", document.ReadSourceLine(document.Events[4]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-entry", "Partial", "missing-entry")]
    [InlineData("missing-exit", "Partial", "missing-exit")]
    [InlineData("duplicate-entry", "Ambiguous", "duplicate-entry")]
    [InlineData("duplicate-exit", "Ambiguous", "duplicate-exit")]
    [InlineData("exit-before-entry", "Ambiguous", "exit-before-entry")]
    [InlineData("identity", "Ambiguous", "call-identity-mismatch")]
    [InlineData("request", "Ambiguous", "call-identity-mismatch")]
    [InlineData("return", "Partial", "return-unavailable")]
    [InlineData("nesting", "Partial", "nesting-unavailable")]
    [InlineData("script", "Partial", "script-identity-unavailable")]
    [InlineData("legacy", "Unavailable", "call-or-thread-identity-unavailable")]
    public async Task IncompleteOrConflictingCallsDoNotChangeTransportCompletion(string variation, string status, string diagnostic)
    {
        var entry = Call(true, 1);
        var exit = Call(false, 1);
        var rows = new List<JsonObject> { entry, exit };
        switch (variation)
        {
            case "missing-entry": rows.Remove(entry); break;
            case "missing-exit": rows.Remove(exit); break;
            case "duplicate-entry": rows.Insert(1, (JsonObject)entry.DeepClone()); break;
            case "duplicate-exit": rows.Add((JsonObject)exit.DeepClone()); break;
            case "exit-before-entry": rows.Reverse(); break;
            case "identity": exit["scriptFormId"] = 0x9999; break;
            case "request": exit["requestId"] = 22; break;
            case "return": exit.Remove("returned"); break;
            case "nesting": entry.Remove("depth"); exit.Remove("depth"); break;
            case "script": entry.Remove("scriptAddress"); exit.Remove("scriptAddress"); break;
            case "legacy": entry.Remove("callId"); exit.Remove("callId"); break;
        }
        var (document, _) = await Import(rows);
        Assert.True(document.Summary.Complete);
        Assert.All(document.ScriptCalls.Calls, call => Assert.Equal(status, call.Status));
        Assert.Contains(document.ScriptCalls.Calls, call => call.Diagnostics.Contains(diagnostic));
        Assert.Equal(rows.Count, document.ScriptCalls.Calls.Sum(call => call.EvidenceLines.Count));
        Assert.Equal(document.ScriptCalls.Summary, document.Summary.ScriptCalls);
    }

    [Theory]
    [InlineData("depth", "nested-depth-mismatch")]
    [InlineData("parent", "parent-call-mismatch")]
    [InlineData("self", "self-parent")]
    [InlineData("root", "root-depth-mismatch")]
    [InlineData("thread", "parent-thread-mismatch")]
    [InlineData("closed", "parent-already-exited")]
    [InlineData("order", "out-of-order-exit")]
    public async Task InvalidCallNestingIsAmbiguous(string variation, string diagnostic)
    {
        var outer = Call(true, 1);
        var inner = Call(true, 2, 1, 1);
        var innerExit = Call(false, 2, 1, 1);
        var outerExit = Call(false, 1);
        var rows = new List<JsonObject> { outer, inner, innerExit, outerExit };
        switch (variation)
        {
            case "depth": inner["depth"] = 2; innerExit["depth"] = 2; break;
            case "parent": inner["parentCallId"] = 7; innerExit["parentCallId"] = 7; break;
            case "self": inner["parentCallId"] = 2; innerExit["parentCallId"] = 2; break;
            case "root": outer["depth"] = 2; outerExit["depth"] = 2; break;
            case "thread": inner["threadId"] = 20; innerExit["threadId"] = 20; break;
            case "closed": rows = [outer, outerExit, inner, innerExit]; break;
            case "order": rows = [outer, inner, outerExit, innerExit]; break;
        }
        var (document, _) = await Import(rows);
        Assert.True(document.Summary.Complete);
        Assert.Equal("Ambiguous", document.ScriptCalls.Summary.Status);
        Assert.Contains(document.ScriptCalls.Calls, call => call.Diagnostics.Contains(diagnostic));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingParentOrCaptureLossRetainsPartialCalls(bool loss)
    {
        var (document, _) = await Import([Call(true, 2, loss ? 0UL : 1UL, loss ? 0U : 1U),
            Call(false, 2, loss ? 0UL : 1UL, loss ? 0U : 1U)], loss);
        Assert.Equal(!loss, document.Summary.Complete);
        Assert.Equal("Partial", document.ScriptCalls.Summary.Status);
        var call = Assert.Single(document.ScriptCalls.Calls);
        Assert.Contains(loss ? "capture-event-loss" : "parent-entry-unavailable", call.Diagnostics);
        Assert.Equal(loss, document.ScriptCalls.Summary.EventLoss);
    }

    private static JsonObject Call(bool entry, ulong id, ulong parent = 0, uint depth = 0, uint thread = 10) => new()
    {
        ["kind"] = entry ? "script-entry" : "script-exit", ["callId"] = id,
        ["parentCallId"] = parent, ["depth"] = depth, ["threadId"] = thread,
        ["scriptFormId"] = 0x1234, ["scriptAddress"] = 0x100000, ["requestId"] = 1,
        ["returned"] = !entry
    };

    private static async Task<(RuntimeTraceDocument Document, byte[] Bytes)> Import(IEnumerable<JsonObject> calls, bool loss = false)
    {
        var rows = new List<JsonObject> { new() { ["kind"] = "capture-start" } };
        rows.AddRange(calls);
        rows.Add(new JsonObject { ["kind"] = "capture-end", ["status"] = "completed" });
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i]["protocol"] = 1;
            rows[i]["sequence"] = i + 2;
            rows[i]["frame"] = i;
            rows[i]["dropped"] = loss ? 1 : 0;
        }
        var lines = new List<string> { "{\"kind\":\"capture-header\",\"schema\":\"bmt/runtime-trace\",\"version\":1}" };
        lines.AddRange(rows.Select(row => row.ToJsonString()));
        lines.Add(new JsonObject { ["kind"] = "capture-footer", ["status"] = "completed", ["events"] = rows.Count,
            ["dropped"] = loss ? 1 : 0, ["snapshots"] = 0, ["errors"] = 0 }.ToJsonString());
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines));
        await using var stream = new MemoryStream(bytes);
        return (await RuntimeTraceImporter.ReadDocumentAsync(stream, TestContext.Current.CancellationToken), bytes);
    }
}
