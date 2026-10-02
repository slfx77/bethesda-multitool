using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeGuestManifestTests
{
    [Theory]
    [InlineData("matching", "complete")]
    [InlineData("wrong-backend", "unavailable")]
    [InlineData("wrong-evidence", "unavailable")]
    [InlineData("missing-backing", "partial")]
    [InlineData("multiple-backings", "partial")]
    [InlineData("limit", "partial")]
    [InlineData("writable", "partial")]
    [InlineData("hash", "partial")]
    [InlineData("path", "partial")]
    [InlineData("length", "partial")]
    [InlineData("modified-time", "partial")]
    [InlineData("file-index", "partial")]
    [InlineData("volume", "partial")]
    [InlineData("pending", "partial")]
    [InlineData("changed-file", "partial")]
    [InlineData("payload-limit", "partial")]
    public async Task Binding_requires_unique_unchanged_open_handle_evidence(string defect, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var manifest = await fixture.Prepare();
        // Filename sorting never supplies load order: the engine reports the reverse order.
        var files = manifest.Plugins.Reverse().ToArray();
        var entries = new JsonArray(files.Select((file, index) => (JsonNode)Entry(file, index)).ToArray());
        var first = entries[0]!.AsObject();
        var backing = first["backingFiles"]![0]!.AsObject();
        var query = new JsonObject { ["status"] = "complete", ["count"] = files.Length,
            ["evidence"] = "validated-guest-compiled-file-table", ["entries"] = entries };
        var hello = Hello(manifest);
        switch (defect)
        {
            case "wrong-backend": hello["backend"] = "xnvse"; break;
            case "wrong-evidence": query["evidence"] = "filename-list"; break;
            case "missing-backing": first.Remove("backingFiles"); break;
            case "multiple-backings": first["backingFiles"]!.AsArray().Add(backing.DeepClone()); break;
            case "limit": first["backingObservationLimitReached"] = true; break;
            case "writable": backing["readOnlyDevice"] = false; break;
            case "hash": backing["sha256"] = new string('0', 64); break;
            case "path": backing["path"] = manifest.Guest.Path; break;
            case "length": backing["length"] = files[0].Length + 1; break;
            case "modified-time": backing["lastWriteFileTime"] = files[0].LastWriteFileTime + 1; break;
            case "file-index": backing["fileIndex"] = files[0].FileIndex + 1; break;
            case "volume": backing["volumeSerial"] = files[0].VolumeSerial + 1; break;
            case "pending": backing["status"] = "pending"; break;
            case "changed-file": await File.AppendAllTextAsync(files[0].Path, "changed"); break;
            case "payload-limit": query["status"] = "partial"; query["detail"] = "ProtocolPayloadLimit";
                query["entries"] = new JsonArray(); break;
        }
        using var identity = JsonDocument.Parse(hello.ToJsonString());
        using var observed = JsonDocument.Parse(query.ToJsonString());
        var enriched = await new RuntimePluginNamespace(manifest).EnrichAsync(identity.RootElement, observed.RootElement, CancellationToken.None);
        Assert.Equal(expected, enriched["activePluginIdentityStatus"]!.GetValue<string>());
        Assert.Equal(expected == "complete" ? files.Length : 0, enriched["activePlugins"]!.AsArray().Count);
        if (expected != "complete") return;
        Assert.Equal(files.Select(f => f.Name), enriched["activePlugins"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
        var trace = string.Join('\n',
            "{\"kind\":\"capture-header\",\"schema\":\"bmt/runtime-trace\",\"version\":1,\"identity\":{\"sequence\":1}}",
            new JsonObject { ["kind"] = "capture-start", ["protocol"] = 1, ["sequence"] = 2, ["dropped"] = 0, ["identity"] = enriched }.ToJsonString(),
            "{\"kind\":\"capture-end\",\"protocol\":1,\"sequence\":3,\"dropped\":0,\"status\":\"completed\"}",
            "{\"kind\":\"capture-footer\",\"status\":\"completed\",\"events\":2,\"dropped\":0,\"snapshots\":0,\"errors\":0}");
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(trace));
        var document = await RuntimeTraceImporter.ReadDocumentAsync(input);
        Assert.True(document.Summary.Complete);
        var sources = new RuntimeTraceSources(files.Select((f, index) => new RuntimeSourcePlugin(index, f.Name, f.Sha256)).ToArray(), true);
        Assert.Equal("Matched", RuntimeTraceBinding.Match(document, sources).Status);
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("host")]
    [InlineData("guest")]
    [InlineData("changed-plugin")]
    [InlineData("null-hash")]
    [InlineData("duplicate")]
    public async Task Prepared_manifest_round_trips_and_rejects_changed_identity(string defect)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var manifest = await fixture.Prepare();
        var hello = Hello(manifest);
        if (defect == "host") hello["executableFileSha256"] = new string('0', 64);
        if (defect == "guest") hello["guestExecutableSha256"] = new string('0', 64);
        if (defect == "changed-plugin") await File.AppendAllTextAsync(manifest.Plugins[0].Path, "changed");
        if (defect == "null-hash") manifest = manifest with { Guest = manifest.Guest with { Sha256 = null! } };
        if (defect == "duplicate") manifest = manifest with { Plugins = [manifest.Plugins[0], manifest.Plugins[0]] };
        using var observed = JsonDocument.Parse(hello.ToJsonString());
        if (defect == "matching") await manifest.ValidateAsync(observed.RootElement);
        else if (defect is "host" or "guest") await Assert.ThrowsAsync<InvalidOperationException>(() => manifest.ValidateAsync(observed.RootElement));
        else await Assert.ThrowsAsync<InvalidDataException>(() => manifest.ValidateAsync(observed.RootElement));
    }

    private static JsonObject Hello(RuntimeGuestManifest manifest) => new()
    {
        ["backend"] = "xenia-canary", ["executablePath"] = manifest.Emulator.Path,
        ["executableFileSha256"] = manifest.Emulator.Sha256,
        ["guestExecutablePath"] = manifest.Guest.Path, ["guestExecutableSha256"] = manifest.Guest.Sha256
    };

    private static JsonObject Entry(RuntimeBackingFile file, int index) => new()
    {
        ["index"] = index, ["name"] = file.Name, ["backingObservationLimitReached"] = false,
        ["backingFiles"] = new JsonArray(new JsonObject
        {
            ["status"] = "verified", ["hashScope"] = "successful-host-open-handle", ["readOnlyDevice"] = true,
            ["path"] = @"\\?\" + file.Path, ["sha256"] = file.Sha256, ["length"] = file.Length,
            ["lastWriteFileTime"] = file.LastWriteFileTime, ["volumeSerial"] = file.VolumeSerial, ["fileIndex"] = file.FileIndex
        })
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bmt-guest-manifest-" + Guid.NewGuid().ToString("N"));
        internal async Task<RuntimeGuestManifest> Prepare()
        {
            var data = Directory.CreateDirectory(Path.Combine(_root, "Data")).FullName;
            foreach (var name in new[] { "FalloutNV.esm", "Other.esp" }) await File.WriteAllTextAsync(Path.Combine(data, name), name + " bytes");
            var emulator = Path.Combine(_root, "xenia.exe");
            var guest = Path.Combine(_root, "guest.xex");
            await File.WriteAllTextAsync(emulator, "emulator fixture");
            await File.WriteAllTextAsync(guest, "guest fixture");
            var path = Path.Combine(_root, "manifest.json");
            await RuntimeGuestManifest.PrepareAsync(emulator, guest, data, path);
            return JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), RuntimeJsonContext.Default.RuntimeGuestManifest)!;
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
