using System.Text.Json;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeInstallationReuseTests
{
    [Fact]
    public async Task Closed_sessions_reuse_assets_without_copying_or_changing_timestamps()
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var source = fixture.Write("original/Data/base.bsa", "immutable archive");
        var asset = fixture.Write("working/Data/base.bsa", "immutable archive");
        var timestamp = File.GetLastWriteTimeUtc(asset);
        var profile = fixture.Profile("first");
        using (var lease = RuntimeInstallationLease.AcquireForPreparation(fixture.Working, profile.Root, [fixture.Original]))
        {
            var files = new List<RuntimeProfileFile>();
            await RuntimeIsolationService.StageDirectoryAsync(fixture.Original, fixture.Working, true, false,
                null, files, [], null, token);
            Assert.Equal(asset, Assert.Single(files).CopyPath);
            profile = profile with { Files = files };
            await RuntimeIsolationService.SaveProfile(profile, token);
            await lease.CommitAsync(token);
        }
        await fixture.Close(profile);
        using (var next = RuntimeInstallationLease.AcquireForPreparation(fixture.Working, fixture.Profile("second").Root, [fixture.Original]))
        {
            Assert.Equal(await RuntimeIsolationService.Hash(source, token),
                await RuntimeIsolationService.StageInputFileAsync(source, asset, true, token));
        }
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(asset));
        Assert.False(Directory.Exists(Path.Combine(profile.Root, "game")));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(asset)!));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("cancelled")]
    public async Task Reuse_never_repairs_missing_or_changed_assets_and_cancellation_does_not_copy(string defect)
    {
        using var fixture = new Fixture();
        var source = fixture.Write("original/Data/base.bsa", "original bytes");
        var target = Path.Combine(fixture.Working, "base.bsa");
        if (defect != "missing") File.WriteAllText(target, "different bytes");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (defect == "cancelled") cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => RuntimeIsolationService.StageInputFileAsync(source, target, true, cancellation.Token));
        Assert.NotNull(error);
        if (defect == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsAssignableFrom<IOException>(error);
        if (defect == "missing") Assert.False(File.Exists(target));
        else Assert.Equal("different bytes", File.ReadAllText(target));
    }

    [Fact]
    public async Task Busy_handle_and_prepared_reservation_block_other_sessions_until_explicit_release()
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var profile = fixture.Profile("first");
        var second = fixture.Profile("second");
        using (var lease = RuntimeInstallationLease.AcquireForPreparation(fixture.Working, profile.Root, [fixture.Original]))
        {
            Assert.Throws<IOException>(() => RuntimeInstallationLease.AcquireForPreparation(fixture.Working, second.Root, [fixture.Original]));
            await RuntimeIsolationService.SaveProfile(profile, token);
            await lease.CommitAsync(token);
        }
        Assert.Throws<InvalidOperationException>(() => RuntimeInstallationLease.AcquireForPreparation(fixture.Working, second.Root, [fixture.Original]));
        await RuntimeIsolationService.ReleasePreparedInstallationAsync(profile, token);
        using var available = RuntimeInstallationLease.AcquireForPreparation(fixture.Working, second.Root, [fixture.Original]);
    }

    [Fact]
    public async Task Cancelled_preparation_does_not_leave_a_reservation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using (var lease = RuntimeInstallationLease.AcquireForPreparation(fixture.Working, fixture.Profile("first").Root, [fixture.Original]))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.CommitAsync(cancellation.Token));
        using var available = RuntimeInstallationLease.AcquireForPreparation(fixture.Working, fixture.Profile("second").Root, [fixture.Original]);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("wrong-instance")]
    public async Task Old_reservation_requires_matching_terminal_closure(string defect)
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var profile = fixture.Profile("first") with { ProcessId = 123, ProcessStartedUtc = "start", ExecutableSha256 = "exe" };
        using (var lease = RuntimeInstallationLease.AcquireForPreparation(fixture.Working, profile.Root, [fixture.Original]))
        {
            await RuntimeIsolationService.SaveProfile(profile, token);
            await lease.CommitAsync(token);
        }
        await fixture.Close(profile, defect == "active" ? "active" : "engine-exited-originals-unchanged", defect == "wrong-instance");
        Assert.Throws<InvalidOperationException>(() => RuntimeInstallationLease.AcquireForPreparation(fixture.Working, fixture.Profile("second").Root, [fixture.Original]));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("source-parent")]
    [InlineData("session")]
    public void Reuse_rejects_original_or_session_scope_overlap(string overlap)
    {
        using var fixture = new Fixture();
        var source = overlap switch { "source" => fixture.Working, "source-parent" => fixture.Root, _ => fixture.Original };
        var session = overlap == "session" ? Path.Combine(fixture.Working, "session") : fixture.Profile("session").Root;
        Assert.Throws<InvalidDataException>(() => RuntimeInstallationLease.ValidateScope(fixture.Working, session, [source]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Selected_save_keeps_configs_and_optional_sidecar_but_omits_unrelated_saves(bool hasSidecar)
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var documents = Path.Combine(fixture.Root, "documents-original");
        fixture.Write("documents-original/Fallout.ini", "config");
        fixture.Write("documents-original/Saves/unrelated.fos", "old save");
        fixture.Write("documents-original/Saves/unrelated.nvse", "old sidecar");
        var save = fixture.Write("retained/selected.fos", "selected save");
        if (hasSidecar) fixture.Write("retained/selected.nvse", "selected sidecar");
        var target = Path.Combine(fixture.Root, "private-documents");
        var copies = new List<RuntimeProfileFile>();
        var originals = new List<RuntimeSourceFile>();
        await RuntimeIsolationService.StageDirectoryAsync(documents, target, false, true, null, copies, originals, null, token);
        await RuntimeIsolationService.StageSelectedSavesAsync(RuntimeIsolationService.SelectSavePair(save), target, copies, originals, token);
        Assert.Equal("config", File.ReadAllText(Path.Combine(target, "Fallout.ini")));
        string[] expected = hasSidecar ? ["selected.fos", "selected.nvse"] : ["selected.fos"];
        Assert.Equal(expected, Directory.GetFiles(Path.Combine(target, "Saves")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("selected save", File.ReadAllText(Path.Combine(target, "Saves", "selected.fos")));
        if (hasSidecar) Assert.Equal("selected sidecar", File.ReadAllText(Path.Combine(target, "Saves", "selected.nvse")));
        else Assert.False(File.Exists(Path.Combine(target, "Saves", "selected.nvse")));
        Assert.Equal(hasSidecar ? 5 : 4, originals.Count); // Includes unchanged originals that were deliberately not copied.
        Assert.Equal(hasSidecar ? 3 : 2, copies.Count);
    }

    [Theory]
    [InlineData("missing-save")]
    [InlineData("wrong-extension")]
    public void Save_selection_rejects_missing_save_or_wrong_extension(string defect)
    {
        using var fixture = new Fixture();
        var path = defect == "wrong-extension" ? fixture.Write("save.txt", "save") : Path.Combine(fixture.Root, "missing.fos");
        if (defect == "missing-save") Assert.Throws<FileNotFoundException>(() => RuntimeIsolationService.SelectSavePair(path));
        else Assert.Throws<InvalidDataException>(() => RuntimeIsolationService.SelectSavePair(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evidence_survives_later_working_binary_replacement_without_bulk_copies(bool reuse)
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var exe = fixture.Write("working/FalloutNV.exe", "version one");
        var archive = fixture.Write("working/Data/base.bsa", "bulk archive");
        var master = fixture.Write("working/Data/FalloutNV.esm", "immutable master");
        var fixturePlugin = fixture.Write("working/Data/Control.esp", "fixture plugin");
        var files = new List<RuntimeProfileFile>();
        foreach (var path in new[] { exe, archive, master, fixturePlugin })
            files.Add(new(Path.Combine(fixture.Original, Path.GetFileName(path)), path, await RuntimeIsolationService.Hash(path, token)));
        var profile = fixture.Profile("first") with { Files = files };
        if (reuse) await fixture.Reserve(profile);
        else profile = profile with { Isolation = profile.Isolation! with { ReusedInstallation = null } };
        var evidence = await RuntimeIsolationService.SnapshotGameEvidenceAsync(profile, token);
        Assert.Equal(2, evidence.Count);
        File.WriteAllText(exe, "version two");
        var savedExe = Assert.Single(evidence, file => file.OriginalPath == exe);
        Assert.Equal("version one", File.ReadAllText(savedExe.CopyPath));
        Assert.Equal(savedExe.Sha256, await RuntimeIsolationService.Hash(savedExe.CopyPath, token));
        Assert.False(File.Exists(Path.Combine(profile.Root, "game-evidence", "Data", "base.bsa")));
        Assert.False(File.Exists(Path.Combine(profile.Root, "game-evidence", "Data", "FalloutNV.esm")));
        Assert.Equal(!reuse, RuntimeIsolationService.ContainsPath(profile.Root, savedExe.CopyPath));
        Assert.False(RuntimeIsolationService.ContainsPath(fixture.Working, savedExe.CopyPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reused_evidence_keeps_identical_versions_and_adds_only_changed_bridge_bytes(bool changedBridge)
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var sourceExe = fixture.Write("original/FalloutNV.exe", "executable");
        var sourceBridge = fixture.Write("original/native-one.dll", "bridge one");
        var exe = fixture.Write("working/FalloutNV.exe", "executable");
        var bridge = fixture.Write("working/Data/NVSE/Plugins/NvseRuntimeBridge.dll", "bridge one");
        var first = await fixture.EvidenceProfile("first", (sourceExe, exe), (sourceBridge, bridge));
        var retained = await RuntimeIsolationService.SnapshotGameEvidenceAsync(first, token);
        var stamps = retained.Select(file => File.GetLastWriteTimeUtc(file.CopyPath)).ToArray();
        await fixture.Close(first);
        if (changedBridge)
        {
            sourceBridge = fixture.Write("original/native-two.dll", "bridge two");
            File.WriteAllText(bridge, "bridge two");
        }
        var second = await fixture.EvidenceProfile("second", (sourceExe, exe), (sourceBridge, bridge));
        var current = await RuntimeIsolationService.SnapshotGameEvidenceAsync(second, token);
        Assert.Equal(retained[0], current[0]);
        Assert.Equal(!changedBridge, retained[1] == current[1]);
        Assert.Equal(stamps, retained.Select(file => File.GetLastWriteTimeUtc(file.CopyPath)).ToArray());
        Assert.Equal("bridge one", File.ReadAllText(retained[1].CopyPath));
        Assert.Equal(changedBridge ? "bridge two" : "bridge one", File.ReadAllText(current[1].CopyPath));
        Assert.Equal(changedBridge ? 3 : 2, Directory.GetFiles(Path.GetDirectoryName(current[0].CopyPath)!, "*.blob").Length);
        Assert.Equal("executable", File.ReadAllText(sourceExe));
        Assert.Equal("bridge one", File.ReadAllText(Path.Combine(fixture.Original, "native-one.dll")));
        Assert.False(Directory.Exists(Path.Combine(first.Root, "game-evidence")));
        Assert.False(Directory.Exists(Path.Combine(second.Root, "game-evidence")));
        Assert.All(retained.Concat(current), file => Assert.False(RuntimeIsolationService.ContainsPath(fixture.Working, file.CopyPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Damaged_or_shared_cache_entries_are_refused_without_repairing_them_or_originals(bool hardLink)
    {
        if (hardLink) Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file-identity admission.");
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var original = fixture.Write("original/FalloutNV.exe", "original executable");
        var working = fixture.Write("working/FalloutNV.exe", "original executable");
        var profile = await fixture.EvidenceProfile("first", (original, working));
        var retained = Assert.Single(await RuntimeIsolationService.SnapshotGameEvidenceAsync(profile, token));
        if (hardLink)
        {
            File.Delete(retained.CopyPath);
            Assert.True(CreateHardLinkW(retained.CopyPath, original, nint.Zero), $"CreateHardLink error {Marshal.GetLastWin32Error()}");
        }
        else File.WriteAllText(retained.CopyPath, "damaged cache");
        await Assert.ThrowsAsync<IOException>(() => RuntimeIsolationService.SnapshotGameEvidenceAsync(profile, token));
        Assert.Equal(hardLink ? "original executable" : "damaged cache", File.ReadAllText(retained.CopyPath));
        Assert.Equal("original executable", File.ReadAllText(original));
        Assert.Equal("original executable", File.ReadAllText(working));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(retained.CopyPath)!, "*.writing"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_evidence_request_preserves_existing_files_and_creates_no_partial_blob(bool populated)
    {
        using var fixture = new Fixture();
        var original = fixture.Write("original/FalloutNV.exe", "executable");
        var working = fixture.Write("working/FalloutNV.exe", "executable");
        var profile = await fixture.EvidenceProfile("first", (original, working));
        if (populated) await RuntimeIsolationService.SnapshotGameEvidenceAsync(profile, TestContext.Current.CancellationToken);
        var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeIsolationService.SnapshotGameEvidenceAsync(profile, cancellation.Token));
        Assert.Equal(before, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("executable", File.ReadAllText(original));
    }

    [Fact]
    public async Task Concurrent_identical_evidence_publication_returns_one_complete_blob()
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var contents = new string('e', 256 * 1024);
        var original = fixture.Write("original/FalloutNV.exe", contents);
        var working = fixture.Write("working/FalloutNV.exe", contents);
        var profile = await fixture.EvidenceProfile("first", (original, working));
        var results = await Task.WhenAll(RuntimeIsolationService.SnapshotGameEvidenceAsync(profile, token),
            RuntimeIsolationService.SnapshotGameEvidenceAsync(profile, token));
        var first = Assert.Single(results[0]);
        Assert.Equal(first, Assert.Single(results[1]));
        Assert.Equal(contents, File.ReadAllText(first.CopyPath));
        Assert.Equal(first.Sha256, await RuntimeIsolationService.Hash(first.CopyPath, token));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(first.CopyPath)!));
    }

    [Fact]
    public async Task Cache_cannot_become_an_original_input_scope()
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var original = fixture.Write("original/FalloutNV.exe", "executable");
        var working = fixture.Write("working/FalloutNV.exe", "executable");
        var profile = await fixture.EvidenceProfile("first", (original, working));
        var retained = Assert.Single(await RuntimeIsolationService.SnapshotGameEvidenceAsync(profile, token));
        var cache = Path.GetDirectoryName(Path.GetDirectoryName(retained.CopyPath)!)!;
        var invalid = profile with { Isolation = profile.Isolation! with { GameSource = cache } };
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeIsolationService.SnapshotGameEvidenceAsync(invalid, token));
        Assert.Equal("executable", File.ReadAllText(retained.CopyPath));
        Assert.Equal("executable", File.ReadAllText(original));
    }

    [Theory]
    [InlineData("reserved", true)]
    [InlineData("foreign-file", false)]
    [InlineData("foreign-owner", false)]
    [InlineData("private-documents", false)]
    [InlineData("private-local", false)]
    [InlineData("private-overlap", false)]
    public async Task External_copy_scope_requires_the_exact_reserved_installation(string mode, bool accepted)
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var asset = fixture.Write(mode == "foreign-file" ? "elsewhere/asset.bsa" : "working/asset.bsa", "asset");
        var profile = fixture.Profile("first") with { Files = [new("original", asset, await RuntimeIsolationService.Hash(asset, token))] };
        using (var lease = RuntimeInstallationLease.AcquireForPreparation(fixture.Working, profile.Root, [fixture.Original]))
        {
            await RuntimeIsolationService.SaveProfile(profile, token);
            await lease.CommitAsync(token);
        }
        if (mode == "foreign-owner") profile = profile with { Root = fixture.Profile("second").Root };
        if (mode == "private-documents") profile = profile with { Isolation = profile.Isolation! with { DocumentsCopy = fixture.Working } };
        if (mode == "private-local") profile = profile with { Isolation = profile.Isolation! with { LocalDataCopy = fixture.Working } };
        if (mode == "private-overlap") profile = profile with { Isolation = profile.Isolation! with { LocalDataCopy = profile.Isolation.DocumentsCopy } };
        if (accepted) await profile.VerifyCopiesAsync(token);
        else Assert.NotNull(await Record.ExceptionAsync(() => profile.VerifyCopiesAsync(token)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Writable_installation_requires_independent_files_even_when_bytes_match(bool hardLink)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file-identity admission.");
        using var fixture = new Fixture();
        var original = fixture.Write("original/config.ini", "original bytes");
        var target = Path.Combine(fixture.Working, "config.ini");
        if (hardLink)
            Assert.True(CreateHardLinkW(target, original, nint.Zero), $"CreateHardLink error {Marshal.GetLastWin32Error()}");
        else File.Copy(original, target);
        using var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (hardLink)
            Assert.Throws<IOException>(() => RuntimeIsolationService.ValidateReusableFiles(fixture.Working, TestContext.Current.CancellationToken));
        else RuntimeIsolationService.ValidateReusableFiles(fixture.Working, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancelled")]
    public async Task Owned_probe_directory_is_cleaned_after_the_operation_releases_it(string outcome)
    {
        using var fixture = new Fixture();
        var folder = "__bmt_isolation_probe_" + Guid.NewGuid().ToString("N");
        var expected = Path.Combine(fixture.Working, folder);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var error = await Record.ExceptionAsync(() => RuntimeIsolationService.WithOwnedProbeDirectoryAsync(fixture.Working, folder, async path =>
        {
            Assert.Equal(expected, path);
            await File.WriteAllTextAsync(Path.Combine(path, "owned.txt"), "probe output", TestContext.Current.CancellationToken);
            if (outcome == "failure") throw new IOException("probe failed");
            if (outcome == "cancelled") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
        }, static () => true));
        if (outcome == "success") Assert.Null(error);
        else if (outcome == "failure") Assert.IsType<IOException>(error);
        else Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.False(Directory.Exists(expected));
    }

    [Theory]
    [InlineData("preexisting")]
    [InlineData("child-not-released")]
    public async Task Unowned_or_still_in_use_probe_evidence_is_retained(string reason)
    {
        using var fixture = new Fixture();
        var folder = "__bmt_isolation_probe_" + Guid.NewGuid().ToString("N");
        var expected = Path.Combine(fixture.Working, folder);
        if (reason == "preexisting") Directory.CreateDirectory(expected);
        var error = await Record.ExceptionAsync(() => RuntimeIsolationService.WithOwnedProbeDirectoryAsync(fixture.Working, folder,
            path => File.WriteAllTextAsync(Path.Combine(path, "owned.txt"), "pending child", TestContext.Current.CancellationToken), static () => false));
        if (reason == "preexisting") Assert.IsType<IOException>(error);
        else Assert.Null(error);
        Assert.True(Directory.Exists(expected));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newPath, string existingPath, nint security);

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "bmt-reuse-" + Guid.NewGuid().ToString("N"));
        internal string Working => Path.Combine(Root, "working");
        internal string Original => Path.Combine(Root, "original");
        internal Fixture() { Directory.CreateDirectory(Working); Directory.CreateDirectory(Original); }
        internal string Write(string relative, string text)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }
        internal RuntimeRunProfile Profile(string name)
        {
            var root = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            var layout = new RuntimeIsolationLayout(Original, Working, Path.Combine(Root, "source-documents"), Path.Combine(root, "documents"),
                Path.Combine(Root, "source-local"), Path.Combine(root, "local"), Root, "probe", "probe", "bridge", "bridge", [], [],
                ReusedInstallation: Working);
            return new("bmt/runtime-profile", 1, root, "prepared-not-engine-verified", null, null, [], layout);
        }
        internal async Task Reserve(RuntimeRunProfile profile)
        {
            using var lease = RuntimeInstallationLease.AcquireForPreparation(Working, profile.Root, [Original]);
            await RuntimeIsolationService.SaveProfile(profile, TestContext.Current.CancellationToken);
            await lease.CommitAsync(TestContext.Current.CancellationToken);
        }
        internal async Task<RuntimeRunProfile> EvidenceProfile(string name, params (string Original, string Copy)[] files)
        {
            var entries = new List<RuntimeProfileFile>();
            foreach (var file in files)
                entries.Add(new(file.Original, file.Copy, await RuntimeIsolationService.Hash(file.Copy, TestContext.Current.CancellationToken)));
            var profile = Profile(name) with { Files = entries };
            await Reserve(profile);
            return profile;
        }
        internal async Task Close(RuntimeRunProfile profile, string status = "engine-exited-originals-unchanged", bool wrongInstance = false)
        {
            await RuntimeIsolationService.SaveProfile(profile with { Activation = "engine-exited" }, TestContext.Current.CancellationToken);
            var state = new RuntimeIsolationState(status, DateTimeOffset.UtcNow.ToString("O"), 456,
                wrongInstance ? 999 : profile.ProcessId, profile.ProcessStartedUtc, profile.ExecutableSha256, "bridge", []);
            await File.WriteAllTextAsync(Path.Combine(profile.Root, RuntimeIsolationService.StateFileName),
                JsonSerializer.Serialize(state, RuntimeJsonContext.Default.RuntimeIsolationState), TestContext.Current.CancellationToken);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
