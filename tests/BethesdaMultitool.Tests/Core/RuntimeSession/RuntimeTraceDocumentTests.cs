using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeTraceDocumentTests
{
    private static readonly string PluginHash = new('a', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceSpansPreserveExactUtf8LinesAndZeroObservations(bool bom)
    {
        var bytes = Trace(Identity(), null, bom);
        await using var input = new MemoryStream(bytes);
        var document = await RuntimeTraceImporter.ReadDocumentAsync(input, TestContext.Current.CancellationToken);
        Assert.True(document.Summary.Complete);
        Assert.Empty(document.ControllerResults); // Legacy captures still expose native events alone.
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), document.Summary.Sha256);
        var observed = Assert.Single(document.Events, item => item.Kind == "snapshot");
        Assert.Equal(3, observed.Line);
        Assert.Equal(0d, observed.Value);
        Assert.True(observed.RefersTo(0x01001234));
        var line = document.ReadSourceLine(observed);
        Assert.Contains("café", line);
        Assert.DoesNotContain('\r', line);
        bytes.AsSpan().Fill(0); // The caller's input storage cannot mutate imported source links.
        Assert.Equal(line, document.ReadSourceLine(observed));
    }

    [Theory]
    [InlineData("matched", true, 0)]
    [InlineData("timeout", false, 1)]
    [InlineData("mismatch", true, 1)]
    public async Task ControllerOutcomesKeepExactSourceWithoutNativeOwnershipOrCounts(string status, bool associated, int errors)
    {
        var row = new JsonObject { ["kind"] = "scenario-result", ["origin"] = "controller", ["actionIndex"] = 0,
            ["actionKind"] = "wait-message-state", ["status"] = status, ["elapsedMilliseconds"] = 0,
            ["label"] = "café", ["engineTargetFormId"] = 16781876 };
        if (associated)
        {
            row["observedRequestId"] = 9;
            row["observedSequence"] = 3;
            row["observedFrame"] = 9;
        }
        var text = Encoding.UTF8.GetString(Trace(Identity(), bom: true))
            .Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)
            .Replace("{\"kind\":\"capture-end\"", row.ToJsonString() + "\r\n{\"kind\":\"capture-end\"", StringComparison.Ordinal)
            .Replace("\"errors\":0}", $"\"errors\":{errors},\"controllerResults\":1,\"controllerErrors\":{errors}}}", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(text);
        await using var input = new MemoryStream(bytes);
        var document = await RuntimeTraceImporter.ReadDocumentAsync(input, TestContext.Current.CancellationToken);
        Assert.True(document.Summary.Complete);
        Assert.Equal(3, document.Summary.Events);
        Assert.Equal(3, document.Events.Count);
        Assert.Equal(0, document.Summary.MissingSequences);
        Assert.Equal(1, document.Summary.ControllerResults);
        Assert.Equal(errors, document.Summary.ControllerErrors);
        Assert.Equal(errors, document.Summary.Errors);
        Assert.Single(document.Events.Where(item => item.RefersTo(0x01001234)));
        var outcome = Assert.Single(document.ControllerResults);
        Assert.Equal(4, outcome.Line);
        Assert.Equal(status, outcome.Status);
        Assert.Equal(0, outcome.ActionIndex);
        Assert.Equal(0d, outcome.ElapsedMilliseconds);
        Assert.Equal("wait-message-state", outcome.ActionKind);
        Assert.Equal(associated ? 9UL : (ulong?)null, outcome.ObservedRequestId);
        Assert.Equal(associated ? 3UL : (ulong?)null, outcome.ObservedSequence);
        Assert.Equal(associated ? 9UL : (ulong?)null, outcome.ObservedFrame);
        Assert.Equal(row.ToJsonString(), document.ReadSourceLine(outcome));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), document.Summary.Sha256);
        bytes.AsSpan().Fill(0);
        Assert.Equal(row.ToJsonString(), document.ReadSourceLine(outcome));
    }

    [Theory]
    [InlineData("matching", "Matched")]
    [InlineData("legacy", "Unbound")]
    [InlineData("partial", "Unbound")]
    [InlineData("hash", "Mismatch")]
    [InlineData("name", "Mismatch")]
    [InlineData("slot", "Unbound")]
    [InlineData("extra", "Mismatch")]
    [InlineData("missing-sources", "Unbound")]
    [InlineData("start-overrides-header", "Matched")]
    public async Task BindingRequiresCompleteExactCaptureTimeNamespace(string variation, string expected)
    {
        var identity = Identity();
        var plugin = identity["activePlugins"]![0]!.AsObject();
        switch (variation)
        {
            case "legacy": identity.Remove("activePluginIdentityStatus"); break;
            case "partial": identity["activePluginIdentityStatus"] = "partial"; break;
            case "hash": plugin["sha256"] = new string('b', 64); break;
            case "name": plugin["name"] = "Other.esm"; break;
            case "slot": plugin["index"] = 1; break;
            case "extra": identity["activePlugins"]!.AsArray().Add(plugin.DeepClone()); break;
            case "start-overrides-header": identity["activePluginIdentityStatus"] = "unavailable"; break;
        }
        await using var input = new MemoryStream(Trace(identity,
            variation == "start-overrides-header" ? Identity() : null));
        var document = await RuntimeTraceImporter.ReadDocumentAsync(input, TestContext.Current.CancellationToken);
        var sources = new RuntimeTraceSources([new(0, "falloutnv.esm", PluginHash)], variation != "missing-sources");
        var binding = RuntimeTraceBinding.Match(document, sources);
        Assert.Equal(expected, binding.Status);
        Assert.Equal(expected == "Matched", binding.Matched);
        Assert.Single(document.Events, item => item.Kind == "snapshot"); // Unbound evidence is retained.
    }

    private static JsonObject Identity() => new()
    {
        ["sequence"] = 1,
        ["activePluginIdentityStatus"] = "complete",
        ["activePlugins"] = new JsonArray(new JsonObject
        {
            ["index"] = 0, ["name"] = "FalloutNV.esm", ["sha256"] = PluginHash,
            ["status"] = "verified", ["hashScope"] = "verified-copied-plugin-backing-file"
        })
    };

    [Fact]
    public async Task ControllerEnrichedIdentityCanExceedTheWireFrameLimit()
    {
        var identity = Identity();
        identity["controllerEvidence"] = new string('x', 140000);
        await using var input = new MemoryStream(Trace(identity));
        var document = await RuntimeTraceImporter.ReadDocumentAsync(input, TestContext.Current.CancellationToken);
        Assert.True(document.Summary.Complete);
        Assert.Equal(65536, RuntimeProtocol.MaximumPayloadBytes);
    }

    [Theory]
    [InlineData("Resolved", true)]
    [InlineData("TypeMismatch", false)]
    public async Task XboxScriptIdentityIsIndexedOnlyWhenExplicitlyResolved(string status, bool indexed)
    {
        var text = Encoding.UTF8.GetString(Trace(Identity())).Replace("\"label\":\"café\"",
            $"\"scriptIdentityStatus\":\"{status}\",\"engineScriptFormId\":4660", StringComparison.Ordinal);
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var document = await RuntimeTraceImporter.ReadDocumentAsync(input, TestContext.Current.CancellationToken);
        var entry = Assert.Single(document.Events, item => item.Kind == "snapshot");
        Assert.Equal(indexed, entry.RefersTo(0x1234));
        Assert.Equal("Unbound", RuntimeTraceBinding.Match(document, null).Status);
    }

    private static byte[] Trace(JsonObject identity, JsonObject? startIdentity = null, bool bom = false)
    {
        var header = new JsonObject { ["kind"] = "capture-header", ["schema"] = "bmt/runtime-trace",
            ["version"] = 1, ["identity"] = identity };
        var start = new JsonObject { ["kind"] = "capture-start", ["protocol"] = 1, ["sequence"] = 2, ["dropped"] = 0 };
        if (startIdentity is not null) start["identity"] = startIdentity;
        var text = string.Join("\r\n", header.ToJsonString(), start.ToJsonString(),
            "{\"kind\":\"snapshot\",\"protocol\":1,\"sequence\":3,\"dropped\":0,\"frame\":9,\"value\":0,\"engineTargetFormId\":16781876,\"label\":\"café\"}",
            "{\"kind\":\"capture-end\",\"protocol\":1,\"sequence\":4,\"dropped\":0,\"status\":\"completed\"}",
            "{\"kind\":\"capture-footer\",\"status\":\"completed\",\"events\":3,\"dropped\":0,\"snapshots\":1,\"errors\":0}");
        return Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + text);
    }
}
