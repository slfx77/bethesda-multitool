using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeGuestRunProfileTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Preparation_preserves_relative_saves_and_pins_fresh_private_routes(bool saves, bool gameConfig)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows backing-file admission.");
        using var fixture = new Fixture();
        var profile = await fixture.Prepare(saves, gameConfig);
        var loaded = await RuntimeGuestRunProfile.LoadAsync(profile.ProfilePath, profile.ProfileSha256);
        await loaded.VerifyInitialCopiesAsync();
        await loaded.VerifyOriginalsAsync();
        using var identity = fixture.Identity(loaded);
        var admission = await loaded.ValidateForRunAsync(identity.RootElement, loaded.Scenario.Sha256);
        Assert.Equal(profile.ProfileSha256, admission.ProfileSha256);
        Assert.Equal(Path.Combine(profile.Root, "storage"), admission.Environment.StorageRoot);
        Assert.Equal(Path.Combine(profile.Root, "content"), admission.Environment.ContentRoot);
        Assert.Equal(Path.Combine(profile.Root, "cache"), admission.Environment.CacheRoot);
        Assert.Equal(123, admission.ProcessId);
        Assert.Equal(saves, loaded.InitialSave is not null);
        if (saves)
        {
            Assert.Equal(Path.Combine(profile.Root, "content", "title", "slot", "initial.sav"), loaded.InitialSave!.CopyPath);
            Assert.Equal("initial save bytes", await File.ReadAllTextAsync(loaded.InitialSave.CopyPath));
        }
        Assert.True(Directory.Exists(Path.Combine(profile.Environment.ContentRoot, "empty-directory")));
        Assert.Equal(gameConfig, loaded.GameConfiguration is not null);
        Assert.Equal("source config bytes", await File.ReadAllTextAsync(fixture.Config));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("save")]
    [InlineData("new-output")]
    public async Task Startup_outputs_may_change_but_initial_copy_verification_remains_strict(string change)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows backing-file admission.");
        using var fixture = new Fixture();
        var profile = await fixture.Prepare(true);
        var changed = change switch
        {
            "config" => profile.Configuration.CopyPath,
            "save" => profile.InitialSave!.CopyPath,
            _ => Path.Combine(profile.Environment.CacheRoot, "compiled-cache.bin")
        };
        await File.WriteAllTextAsync(changed, "engine output");
        await Assert.ThrowsAsync<IOException>(() => profile.VerifyInitialCopiesAsync());
        using var identity = fixture.Identity(profile);
        await profile.ValidateForRunAsync(identity.RootElement, profile.Scenario.Sha256);
        await profile.VerifyOriginalsAsync();
    }

    [Theory]
    [InlineData("source-bytes")]
    [InlineData("source-added-file")]
    [InlineData("source-added-directory")]
    [InlineData("source-removed-file")]
    [InlineData("scenario-copy")]
    [InlineData("manifest-copy")]
    [InlineData("profile-bytes")]
    public async Task Admission_rejects_changed_originals_and_immutable_inputs(string change)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows backing-file admission.");
        using var fixture = new Fixture();
        var profile = await fixture.Prepare(true);
        switch (change)
        {
            case "source-bytes": await File.WriteAllTextAsync(fixture.Config, "modified original"); break;
            case "source-added-file": await File.WriteAllTextAsync(Path.Combine(fixture.Saves, "new.sav"), "new"); break;
            case "source-added-directory": Directory.CreateDirectory(Path.Combine(fixture.Saves, "new-folder")); break;
            case "source-removed-file": File.Delete(profile.InitialSave!.OriginalPath); break;
            case "scenario-copy": await File.WriteAllTextAsync(profile.Scenario.CopyPath, "changed scenario"); break;
            case "manifest-copy": await File.WriteAllTextAsync(profile.GuestManifestFile.CopyPath, "{}"); break;
            case "profile-bytes": await File.AppendAllTextAsync(profile.ProfilePath, " "); break;
        }
        using var identity = fixture.Identity(profile);
        var error = await Record.ExceptionAsync(() => profile.ValidateForRunAsync(identity.RootElement, profile.Scenario.Sha256));
        Assert.NotNull(error);
        if (change is "scenario-copy" or "manifest-copy" or "profile-bytes")
            Assert.IsType<InvalidDataException>(error);
        else
            Assert.IsAssignableFrom<IOException>(error);
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("game")]
    [InlineData("writable-game")]
    [InlineData("plugins")]
    [InlineData("relative-writes")]
    [InlineData("global-read")]
    [InlineData("global-unavailable")]
    [InlineData("game-config-loaded")]
    [InlineData("game-config-outside")]
    [InlineData("partial")]
    [InlineData("version")]
    [InlineData("guest-hash")]
    [InlineData("scenario-hash")]
    [InlineData("process-id")]
    [InlineData("process-start")]
    [InlineData("window-owner")]
    public async Task Effective_environment_requires_observed_private_routes_and_policies(string defect)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows backing-file admission.");
        using var fixture = new Fixture();
        var profile = await fixture.Prepare(false);
        var node = fixture.IdentityNode(profile);
        var env = node["effectiveEnvironment"]!.AsObject();
        switch (defect)
        {
            case "storage": env["storageRoot"] = fixture.Saves; break;
            case "game": env["gameRoot"] = fixture.Saves; break;
            case "writable-game": env["gameReadOnly"] = false; break;
            case "plugins": env["allowPlugins"] = true; break;
            case "relative-writes": env["allowGameRelativeWrites"] = true; break;
            case "global-read": env["globalConfigReadPath"] = fixture.Config; break;
            case "global-unavailable": env["globalConfigStatus"] = "unavailable"; break;
            case "game-config-loaded": env["gameConfigStatus"] = "loaded"; break;
            case "game-config-outside": env["gameConfigPath"] = Path.Combine(fixture.Saves, "title.config.toml"); break;
            case "partial": env["status"] = "partial"; break;
            case "version": env["schemaVersion"] = 2; break;
            case "guest-hash": node["guestExecutableSha256"] = new string('0', 64); break;
            case "process-id": env["processId"] = 999; break;
            case "process-start": env["processCreationFileTime"] = "1"; break;
            case "window-owner": env["windowProcessId"] = 999; break;
        }
        using var identity = JsonDocument.Parse(node.ToJsonString());
        var hash = defect == "scenario-hash" ? new string('0', 64) : profile.Scenario.Sha256;
        var error = await Record.ExceptionAsync(() => profile.ValidateForRunAsync(identity.RootElement, hash));
        Assert.True(error is InvalidOperationException or InvalidDataException, error?.ToString());
    }

    [Theory]
    [InlineData("destination-game")]
    [InlineData("destination-saves")]
    [InlineData("missing-initial")]
    [InlineData("escaped-initial")]
    [InlineData("existing-destination")]
    public async Task Preparation_rejects_overlap_and_ambiguous_initial_save_without_changing_originals(string defect)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows backing-file admission.");
        using var fixture = new Fixture();
        var options = await fixture.Options(true);
        options = defect switch
        {
            "destination-game" => options with { Destination = Path.Combine(fixture.Game, "session") },
            "destination-saves" => options with { Destination = Path.Combine(fixture.Saves, "session") },
            "missing-initial" => options with { InitialSaveRelativePath = null },
            "escaped-initial" => options with { InitialSaveRelativePath = "..\\config.toml" },
            "existing-destination" => options with { Destination = fixture.Game },
            _ => throw new InvalidOperationException()
        };
        var error = await Record.ExceptionAsync(() => RuntimeGuestRunProfile.PrepareAsync(options));
        if (defect is "missing-initial" or "escaped-initial")
            Assert.IsType<InvalidDataException>(error);
        else
            Assert.IsAssignableFrom<IOException>(error);
        Assert.Equal("source config bytes", await File.ReadAllTextAsync(fixture.Config));
        Assert.Equal("initial save bytes", await File.ReadAllTextAsync(Path.Combine(fixture.Saves, "title", "slot", "initial.sav")));
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("hardlink")]
    public async Task Writable_aliases_cannot_redirect_into_originals_after_preparation(string alias)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows filesystem alias admission.");
        using var fixture = new Fixture();
        var profile = await fixture.Prepare(true);
        var target = Path.Combine(profile.Environment.CacheRoot, "alias");
        if (alias == "symlink")
        {
            try { Directory.CreateSymbolicLink(target, fixture.Saves); }
            catch (UnauthorizedAccessException) { Assert.Skip("Symbolic-link creation requires Developer Mode or elevation."); }
        }
        else
            Assert.True(CreateHardLinkW(target, fixture.Config, nint.Zero), $"CreateHardLink error {Marshal.GetLastWin32Error()}");
        try
        {
            using var identity = fixture.Identity(profile);
            await Assert.ThrowsAsync<IOException>(() => profile.ValidateForRunAsync(identity.RootElement, profile.Scenario.Sha256));
            Assert.Equal("source config bytes", await File.ReadAllTextAsync(fixture.Config));
        }
        finally
        {
            if (alias == "symlink") Directory.Delete(target);
            else File.Delete(target);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Locked_runtime_outputs_are_checked_for_links_without_reading_contents(bool hardLink)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows backing-file admission.");
        var token = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        var profile = await fixture.Prepare(false);
        var output = Path.Combine(profile.Environment.CacheRoot, "executable_addr_flags.bin");
        await File.WriteAllTextAsync(output, "engine-owned cache bytes", token);
        if (hardLink)
            Assert.True(CreateHardLinkW(Path.Combine(profile.Environment.CacheRoot, "alias.bin"), output, nint.Zero),
                $"CreateHardLink error {Marshal.GetLastWin32Error()}");
        using var engine = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var identity = fixture.Identity(profile);
        if (hardLink)
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                profile.ValidateForRunAsync(identity.RootElement, profile.Scenario.Sha256, token));
            Assert.Contains("shared hard links", error.Message, StringComparison.Ordinal);
        }
        else
        {
            var admission = await profile.ValidateForRunAsync(identity.RootElement, profile.Scenario.Sha256, token);
            Assert.Equal(profile.ProfileSha256, admission.ProfileSha256);
        }
        Assert.Equal("source config bytes", await File.ReadAllTextAsync(fixture.Config, token));
    }

    [Fact]
    public async Task Cancelled_preparation_does_not_create_or_replace_the_destination()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows backing-file admission.");
        using var fixture = new Fixture();
        var options = await fixture.Options(false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeGuestRunProfile.PrepareAsync(options, cancelled.Token));
        Assert.False(Path.Exists(options.Destination));
        Assert.Equal("source config bytes", await File.ReadAllTextAsync(fixture.Config));
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bmt-guest-run-" + Guid.NewGuid().ToString("N"));
        public string Game => Path.Combine(_root, "game");
        public string Saves => Path.Combine(_root, "saves");
        public string Config => Path.Combine(_root, "config.toml");

        public async Task<RuntimeGuestRunOptions> Options(bool saves, bool gameConfig = false, RuntimeScenario? scenario = null)
        {
            var data = Directory.CreateDirectory(Path.Combine(Game, "Data")).FullName;
            Directory.CreateDirectory(Path.Combine(Saves, "empty-directory"));
            var emulator = Path.Combine(_root, "xenia.exe");
            var guest = Path.Combine(Game, "default.xex");
            var scenarioPath = Path.Combine(_root, "scenario.json");
            await File.WriteAllTextAsync(emulator, "emulator fixture");
            await File.WriteAllTextAsync(guest, "guest fixture");
            await File.WriteAllTextAsync(Path.Combine(data, "FalloutNV.esm"), "master fixture");
            await File.WriteAllTextAsync(Config, "source config bytes");
            await File.WriteAllTextAsync(scenarioPath, scenario is null ? "{\"actions\":[]}" :
                JsonSerializer.Serialize(scenario, RuntimeJsonContext.Default.RuntimeScenario));
            string? relative = null;
            if (saves)
            {
                relative = Path.Combine("title", "slot", "initial.sav");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Saves, relative))!);
                await File.WriteAllTextAsync(Path.Combine(Saves, relative), "initial save bytes");
            }
            var gameConfiguration = gameConfig ? Path.Combine(_root, "425307E0.config.toml") : null;
            if (gameConfiguration is not null) await File.WriteAllTextAsync(gameConfiguration, "per-game config bytes");
            var manifest = Path.Combine(_root, "guest.json");
            await RuntimeGuestManifest.PrepareAsync(emulator, guest, data, manifest);
            return new(manifest, Game, Config, Saves, scenarioPath, Path.Combine(_root, "isolated"), relative, gameConfiguration);
        }

        public async Task<RuntimeGuestRunProfile> Prepare(bool saves, bool gameConfig = false, RuntimeScenario? scenario = null) =>
            await RuntimeGuestRunProfile.PrepareAsync(await Options(saves, gameConfig, scenario));

        public JsonDocument Identity(RuntimeGuestRunProfile profile) => JsonDocument.Parse(IdentityNode(profile).ToJsonString());
        public JsonObject IdentityNode(RuntimeGuestRunProfile profile) => new()
        {
            ["backend"] = "xenia-canary", ["processId"] = 123, ["processStartedUtc"] = "2026-09-30T00:00:00.0000000Z",
            ["executablePath"] = profile.GuestManifest.Emulator.Path, ["executableFileSha256"] = profile.GuestManifest.Emulator.Sha256,
            ["guestExecutablePath"] = profile.GuestManifest.Guest.Path, ["guestExecutableSha256"] = profile.GuestManifest.Guest.Sha256,
            ["effectiveEnvironment"] = new JsonObject
            {
                ["schemaVersion"] = 1, ["status"] = "observed", ["storageRoot"] = profile.Environment.StorageRoot,
                ["processId"] = 123, ["processCreationFileTime"] = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["windowValid"] = true, ["windowProcessId"] = 123,
                ["contentRoot"] = profile.Environment.ContentRoot, ["cacheRoot"] = profile.Environment.CacheRoot,
                ["gameRoot"] = profile.Environment.GameRoot, ["gameReadOnly"] = true, ["allowPlugins"] = false,
                ["allowGameRelativeWrites"] = false, ["configPath"] = profile.Environment.ConfigPath,
                ["globalConfigReadPath"] = profile.Configuration.CopyPath, ["globalConfigStatus"] = "loaded",
                ["gameConfigPath"] = profile.GameConfiguration?.CopyPath ?? Path.Combine(profile.Environment.StorageRoot, "config", "425307E0.config.toml"),
                ["gameConfigStatus"] = profile.GameConfiguration is null ? "absent" : "loaded"
            }
        };
        public void Dispose() => Directory.Delete(_root, true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newPath, string existingPath, nint security);
}
