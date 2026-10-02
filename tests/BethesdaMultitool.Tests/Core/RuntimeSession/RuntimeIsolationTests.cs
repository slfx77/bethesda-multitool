using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeIsolationTests
{
    [Theory]
    [InlineData("released")]
    [InlineData("cancelled")]
    [InlineData("persistent")]
    public async Task Atomic_status_replacement_preserves_complete_state_during_windows_reader_contention(string outcome)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows delete-sharing behavior.");
        using var fixture = new DirectoryFixture();
        var path = Path.Combine(fixture.Root, "state.json");
        const string previous = "{\"status\":\"old\"}";
        const string next = "{\"status\":\"new\"}";
        await File.WriteAllTextAsync(path, previous);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var cancellation = new CancellationTokenSource();
        var replacement = RuntimeIsolationService.AtomicWrite(path, next, cancellation.Token);
        await Task.Delay(150);
        Assert.False(replacement.IsCompleted);
        Assert.Equal(previous, await File.ReadAllTextAsync(path));

        if (outcome == "released")
        {
            reader.Dispose();
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(next, await File.ReadAllTextAsync(path));
        }
        else
        {
            if (outcome == "cancelled") cancellation.Cancel();
            var error = await Record.ExceptionAsync(() => replacement.WaitAsync(TimeSpan.FromSeconds(5)));
            if (outcome == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(error);
            else Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.Equal(previous, await File.ReadAllTextAsync(path));
        }
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.writing"));
    }

    [Theory]
    [InlineData("complete", "complete")]
    [InlineData("missing", "unavailable")]
    [InlineData("engine-partial", "partial")]
    [InlineData("gap", "partial")]
    [InlineData("duplicate", "partial")]
    [InlineData("unknown-file", "partial")]
    [InlineData("modified-copy", "partial")]
    [InlineData("non-object-entry", "partial")]
    [InlineData("wrong-backend", "unavailable")]
    [InlineData("wrong-evidence", "unavailable")]
    public async Task Plugin_namespace_requires_actual_complete_engine_slots_and_unchanged_backing_files(string defect, string expected)
    {
        using var fixture = new DirectoryFixture();
        var data = Directory.CreateDirectory(Path.Combine(fixture.Root, "Data")).FullName;
        var file = Path.Combine(data, "FalloutNV.esm");
        await File.WriteAllTextAsync(file, "plugin bytes");
        var hash = await RuntimeIsolationService.Hash(file, CancellationToken.None);
        var profile = new RuntimeRunProfile("bmt/runtime-profile", 1, fixture.Root, "verified-usvfs", 123, "exe",
            [new RuntimeProfileFile("original", file, hash)], Layout(fixture.Root));
        var resolver = new RuntimePluginNamespace(profile);
        Assert.Equal("session\nFalloutNV.esm", resolver.StartPayload("session"));
        var query = defect switch
        {
            "missing" => """{"status":"unavailable"}""",
            "engine-partial" => """{"status":"partial","count":2,"entries":[{"index":0,"name":"FalloutNV.esm"}]}""",
            "gap" => """{"status":"complete","count":1,"entries":[{"index":1,"name":"FalloutNV.esm"}]}""",
            "duplicate" => """{"status":"complete","count":2,"entries":[{"index":0,"name":"FalloutNV.esm"},{"index":1,"name":"FalloutNV.esm"}]}""",
            "unknown-file" => """{"status":"complete","count":1,"entries":[{"index":0,"name":"Other.esm"}]}""",
            "non-object-entry" => """{"status":"complete","count":1,"entries":[0]}""",
            _ => """{"status":"complete","count":1,"entries":[{"index":0,"name":"FalloutNV.esm"}]}"""
        };
        if (defect == "modified-copy") await File.WriteAllTextAsync(file, "changed after preparation");
        var queryNode = JsonNode.Parse(query)!.AsObject();
        queryNode["evidence"] = defect == "wrong-evidence" ? "filename-list" : "engine-get-mod-index-and-count";
        using var observed = JsonDocument.Parse(queryNode.ToJsonString());
        using var identity = JsonDocument.Parse(defect == "wrong-backend"
            ? """{"backend":"xenia-canary","executableFileSha256":"exe"}"""
            : """{"backend":"xnvse","executableFileSha256":"exe"}""");
        var enriched = await resolver.EnrichAsync(identity.RootElement, observed.RootElement, CancellationToken.None);
        Assert.Equal(expected, enriched["activePluginIdentityStatus"]!.GetValue<string>());
        var plugins = enriched["activePlugins"]!.AsArray();
        Assert.Equal(expected == "complete" ? 1 : 0, plugins.Count);
        if (expected == "complete") Assert.Equal(hash, plugins[0]!["sha256"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mapped_module_identity_compares_the_backing_file_independently_of_loader_names(bool sameFile)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DirectoryFixture();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var module = process.MainModule!;
        var other = Path.Combine(fixture.Root, "different.dll");
        File.WriteAllText(other, "not the running module");
        var mapped = RuntimeModuleIdentity.MappedFile(process, module);
        Assert.Equal(sameFile, RuntimeModuleIdentity.IsBackingFile(mapped, sameFile ? module.FileName : other));
        using var readable = File.OpenRead(RuntimeModuleIdentity.ReadableMappedPath(mapped));
        Assert.True(readable.Length > 0);
        var displayPath = sameFile ? module.FileName : other;
        var identity = await RuntimeModuleIdentity.ExecutableIdentityAsync(displayPath, mapped, TestContext.Current.CancellationToken);
        Assert.Equal(displayPath, identity["executablePath"]!.GetValue<string>());
        Assert.Equal(mapped, identity["executableMappedFile"]!.GetValue<string>());
        Assert.Equal(RuntimeModuleIdentity.ReadableMappedPath(mapped), identity["executableBackingPath"]!.GetValue<string>());
        Assert.Equal(await RuntimeIsolationService.Hash(module.FileName, TestContext.Current.CancellationToken),
            identity["executableFileSha256"]!.GetValue<string>());
        Assert.Equal("mapped-executable-backing-file-on-disk", identity["hashScope"]!.GetValue<string>());
        if (!sameFile)
        {
            Assert.NotEqual(await RuntimeIsolationService.Hash(other, TestContext.Current.CancellationToken), identity["executableFileSha256"]!.GetValue<string>());
            await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeModuleIdentity.ExecutableIdentityAsync(other, "unresolved", TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("unchanged", true)]
    [InlineData("content", false)]
    [InlineData("new-file", false)]
    [InlineData("new-directory", false)]
    [InlineData("deleted-file", false)]
    public async Task Original_inventory_detects_content_and_membership_changes(string change, bool valid)
    {
        using var fixture = new DirectoryFixture();
        var original = Path.Combine(fixture.Root, "original");
        Directory.CreateDirectory(original);
        var file = Path.Combine(original, "Fallout.ini");
        await File.WriteAllTextAsync(file, "original settings");
        var hash = await RuntimeIsolationService.Hash(file, CancellationToken.None);
        var layout = Layout(fixture.Root) with
        {
            SourceRoots = [new RuntimeSourceRoot(original, true, [])],
            SourceFiles = [new RuntimeSourceFile(file, hash)]
        };
        switch (change)
        {
            case "content": await File.WriteAllTextAsync(file, "changed settings"); break;
            case "new-file": await File.WriteAllTextAsync(Path.Combine(original, "new.fos"), "save"); break;
            case "new-directory": Directory.CreateDirectory(Path.Combine(original, "new saves")); break;
            case "deleted-file": File.Delete(file); break;
        }
        if (valid) await RuntimeIsolationService.VerifySourceInventoriesAsync(layout, CancellationToken.None);
        else await Assert.ThrowsAsync<IOException>(() => RuntimeIsolationService.VerifySourceInventoriesAsync(layout, CancellationToken.None));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("reused-process-id")]
    [InlineData("wrong-bridge")]
    public async Task Run_rejects_stale_or_mismatched_isolation_observations(string defect)
    {
        using var fixture = new DirectoryFixture();
        var layout = Layout(fixture.Root);
        await File.WriteAllTextAsync(layout.BridgePath, "bridge fixture");
        var bridgeHash = await RuntimeIsolationService.Hash(layout.BridgePath, CancellationToken.None);
        layout = layout with { BridgeSha256 = bridgeHash };
        var start = "2026-09-30T00:00:00.0000000Z";
        var profile = new RuntimeRunProfile("bmt/runtime-profile", 1, fixture.Root, "verified-usvfs", 123, "exe", [], layout, start);
        var state = new RuntimeIsolationState("active", DateTimeOffset.UtcNow.ToString("O"), 456, 123, start, "exe", bridgeHash, [123]);
        state = defect switch
        {
            "stale" => state with { UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O") },
            "reused-process-id" => state with { ProcessStartedUtc = "2026-09-29T00:00:00.0000000Z" },
            _ => state with { BridgeSha256 = "different binary" }
        };
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, RuntimeIsolationService.StateFileName),
            JsonSerializer.Serialize(state, RuntimeJsonContext.Default.RuntimeIsolationState));
        using var identity = JsonDocument.Parse("""{"processId":123,"executableFileSha256":"exe","processStartedUtc":"2026-09-30T00:00:00.0000000Z"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => profile.ValidateForRunAsync(identity.RootElement));
    }

    [Fact]
    public void Directory_scope_uses_path_boundaries_and_normalizes_parent_segments()
    {
        using var fixture = new DirectoryFixture();
        var source = Path.Combine(fixture.Root, "game");
        Assert.True(RuntimeIsolationService.ContainsPath(source, Path.Combine(source, "Data", "FalloutNV.esm")));
        Assert.False(RuntimeIsolationService.ContainsPath(source, Path.Combine(fixture.Root, "game-other", "FalloutNV.exe")));
        Assert.False(RuntimeIsolationService.ContainsPath(source, Path.Combine(source, "..", "original.ini")));
    }

    private static RuntimeIsolationLayout Layout(string root) => new(root, root, root, root, root, root,
        root, Path.Combine(root, "probe.exe"), "probe", Path.Combine(root, "bridge.dll"), "bridge", [], []);

    private sealed class DirectoryFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "bmt-isolation-" + Guid.NewGuid().ToString("N"));
        internal DirectoryFixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
