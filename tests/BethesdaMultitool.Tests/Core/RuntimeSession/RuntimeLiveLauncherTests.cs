using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeLiveLauncherTests
{
    [Theory]
    [InlineData("[General]\nbAlwaysActive=1", true)]
    [InlineData("[general]\n balwaysactive = 1 \r\n", true)]
    [InlineData("[General]\nbAlwaysActive=1\nbAlwaysActive=1", true)]
    [InlineData("[General]\nbAlwaysActive=1\nbAlwaysActive=0", false)]
    [InlineData("[Other]\nbAlwaysActive=1", false)]
    [InlineData("[General]\n;bAlwaysActive=1", false)]
    public void Required_live_setting_handles_sections_and_conflicting_duplicates(string content, bool expected)
        => Assert.Equal(expected, RuntimeLiveLauncher.HasIniValue(content, "General", "bAlwaysActive", "1"));

    [Theory]
    [InlineData("prepared-not-engine-verified")]
    [InlineData("verified-usvfs")]
    [InlineData("engine-exited")]
    public async Task Reusable_live_profile_accepts_prior_activation_and_private_display_edits(string activation)
    {
        using var fixture = new Fixture();
        var profile = fixture.Profile with { Activation = activation };
        Assert.Same(profile.Isolation, RuntimeLiveLauncher.ValidateProfile(profile, fixture.ProfilePath));
        await File.AppendAllTextAsync(Path.Combine(profile.Isolation!.DocumentsCopy, "FalloutPrefs.ini"),
            "\r\n[Display]\r\niSize W=1280\r\niSize H=720\r\n", TestContext.Current.CancellationToken);
        await RuntimeLiveLauncher.ValidateConfigurationAsync(profile, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("root")]
    [InlineData("documents-escape")]
    [InlineData("bridge-escape")]
    [InlineData("not-prepared-for-live")]
    public void Live_profile_rejects_unknown_identity_or_unprepared_private_routes(string defect)
    {
        using var fixture = new Fixture();
        var profile = fixture.Profile;
        profile = defect switch
        {
            "schema" => profile with { Schema = "unknown" },
            "root" => profile with { Root = Path.Combine(fixture.Root, "elsewhere") },
            "documents-escape" => profile with { Isolation = profile.Isolation! with { DocumentsCopy = Path.Combine(fixture.Root, "outside") } },
            "bridge-escape" => profile with { Isolation = profile.Isolation! with { BridgePath = Path.Combine(fixture.Root, "other.dll") } },
            _ => profile with { Files = [] }
        };
        Assert.Throws<InvalidDataException>(() => RuntimeLiveLauncher.ValidateProfile(profile, fixture.ProfilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_route_upgrade_preserves_original_and_private_saves_and_overrides_custom_ini(bool customOverride)
    {
        using var fixture = new Fixture();
        var profile = fixture.Profile;
        var layout = profile.Isolation!;
        var originals = Directory.CreateDirectory(Path.Combine(layout.DocumentsSource, "Saves")).FullName;
        var copies = Directory.CreateDirectory(Path.Combine(layout.DocumentsCopy, "Saves")).FullName;
        foreach (var name in new[] { "quicksave.fos", "quicksave.nvse", "quicksave.fos.bak", "autosave.fos" })
        {
            await File.WriteAllTextAsync(Path.Combine(originals, name), "original " + name);
            await File.WriteAllTextAsync(Path.Combine(copies, name), "private " + name);
        }
        await File.WriteAllTextAsync(Path.Combine(copies, "baseline.fos"), "baseline");
        foreach (var name in new[] { "Fallout.ini", "FalloutPrefs.ini" })
        {
            var path = Path.Combine(layout.DocumentsCopy, name);
            await File.WriteAllTextAsync(path, RuntimeLiveConfiguration.SetValue(await File.ReadAllTextAsync(path), "General", "SLocalSavePath", "Saves\\"));
        }
        if (customOverride)
            await File.WriteAllTextAsync(Path.Combine(layout.DocumentsCopy, "FalloutCustom.ini"), "[General]\nSLocalSavePath=Saves\\\nSLocalSavePath=Other\\\nkeep=7\n");

        await RuntimeLiveSaveIsolation.PrepareAsync(profile, TestContext.Current.CancellationToken);
        await RuntimeLiveLauncher.ValidateConfigurationAsync(profile, TestContext.Current.CancellationToken);
        var route = RuntimeLiveSaveIsolation.Resolve(profile);
        Assert.Equal(copies, route.Physical);
        Assert.Equal(Path.Combine(layout.DocumentsSource, RuntimeLiveSaveIsolation.DirectoryName(profile.Root)), route.Logical);
        Assert.False(Path.Exists(route.Logical));
        Assert.NotEqual(originals, route.Logical);
        foreach (var name in new[] { "quicksave.fos", "quicksave.nvse", "quicksave.fos.bak", "autosave.fos" })
        {
            Assert.Equal("original " + name, await File.ReadAllTextAsync(Path.Combine(originals, name)));
            Assert.Equal("private " + name, await File.ReadAllTextAsync(Path.Combine(copies, name)));
        }
        Assert.Equal("baseline", await File.ReadAllTextAsync(Path.Combine(copies, "baseline.fos")));
        if (customOverride) Assert.Contains("keep=7", await File.ReadAllTextAsync(Path.Combine(layout.DocumentsCopy, "FalloutCustom.ini")));
        var first = await File.ReadAllTextAsync(Path.Combine(layout.DocumentsCopy, "Fallout.ini"));
        await RuntimeLiveSaveIsolation.PrepareAsync(profile, TestContext.Current.CancellationToken);
        Assert.Equal(first, await File.ReadAllTextAsync(Path.Combine(layout.DocumentsCopy, "Fallout.ini")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_physical_save_route_is_rejected_before_private_configuration_changes(bool directory)
    {
        using var fixture = new Fixture();
        var profile = fixture.Profile;
        var route = RuntimeLiveSaveIsolation.Resolve(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(route.Logical)!);
        if (directory) Directory.CreateDirectory(route.Logical);
        else await File.WriteAllTextAsync(route.Logical, "existing file");
        var ini = Path.Combine(profile.Isolation!.DocumentsCopy, "Fallout.ini");
        var before = await File.ReadAllTextAsync(ini);
        await Assert.ThrowsAsync<IOException>(() => RuntimeLiveSaveIsolation.PrepareAsync(profile, TestContext.Current.CancellationToken));
        Assert.Equal(before, await File.ReadAllTextAsync(ini));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "bmt-live-launch-" + Guid.NewGuid().ToString("N"));
        internal string ProfilePath => Path.Combine(Profile.Root, RuntimeRunProfile.FileName);
        internal RuntimeRunProfile Profile { get; }

        internal Fixture()
        {
            var profileRoot = Path.Combine(Root, "profile");
            var installation = Path.Combine(Root, "installation");
            var documents = Path.Combine(profileRoot, "documents");
            var local = Path.Combine(profileRoot, "local-data");
            foreach (var path in new[] { installation, documents, local, Path.Combine(profileRoot, "live-config"), Path.Combine(installation, "Data", "NVSE") })
                Directory.CreateDirectory(path);
            var files = new List<RuntimeProfileFile>();
            foreach (var name in new[] { "Fallout.ini", "FalloutPrefs.ini", "nvse_config.ini" })
            {
                var content = name == "nvse_config.ini" ? "[RELEASE]\nbNoScriptRunnerCaching=1\n"
                    : "[General]\nbAlwaysActive=1\nSLocalSavePath=" + RuntimeLiveSaveIsolation.IniValue(profileRoot) + "\n[Display]\nbFull Screen=0\n[Controls]\nbBackground Mouse=1\nbBackground Keyboard=1\nbUse Joystick=0\n";
                var source = Path.Combine(profileRoot, "live-config", name);
                var target = name == "nvse_config.ini" ? Path.Combine(installation, "Data", "NVSE", name) : Path.Combine(documents, name);
                File.WriteAllText(source, content);
                File.WriteAllText(target, content);
                files.Add(new(source, target, new string('0', 64)));
            }
            var layout = new RuntimeIsolationLayout(Path.Combine(Root, "original-game"), installation,
                Path.Combine(Root, "original-documents"), documents, Path.Combine(Root, "original-local"), local,
                Path.Combine(Root, "usvfs"), Path.Combine(Root, "probe.exe"), new string('1', 64),
                Path.Combine(installation, "Data", "NVSE", "Plugins", "NvseRuntimeBridge.dll"), new string('2', 64), [], [],
                ReusedInstallation: installation);
            Profile = new("bmt/runtime-profile", 1, profileRoot, "prepared-not-engine-verified", null, new string('3', 64), files, layout);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
