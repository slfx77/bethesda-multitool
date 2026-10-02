using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeLiveConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_live_configuration_uses_the_same_private_save_route_in_all_game_inis(bool customIni)
    {
        var root = Path.Combine(Path.GetTempPath(), "bmt-live-config-" + Guid.NewGuid().ToString("N"));
        var documents = Directory.CreateDirectory(Path.Combine(root, "documents")).FullName;
        try
        {
            if (customIni) await File.WriteAllTextAsync(Path.Combine(documents, "FalloutCustom.ini"), "[General]\nSLocalSavePath=Saves\\\n[Other]\nkeep=7\n");
            var generated = await RuntimeLiveConfiguration.StageAsync(root, Path.Combine(root, "game"), documents, TestContext.Current.CancellationToken);
            Assert.Equal(customIni ? 4 : 3, generated.Count);
            foreach (var (source, target) in generated.Where(file => Path.GetDirectoryName(file.Target) == documents))
            {
                Assert.True(RuntimeLiveLauncher.HasIniValue(await File.ReadAllTextAsync(source), "General", "SLocalSavePath", RuntimeLiveSaveIsolation.IniValue(root)), target);
                if (Path.GetFileName(source) == "FalloutCustom.ini") Assert.Contains("keep=7", await File.ReadAllTextAsync(source));
            }
            Assert.False(Directory.Exists(Path.Combine(documents, RuntimeLiveSaveIsolation.DirectoryName(root))));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("", "[General]\r\nbAlwaysActive=1\r\n")]
    [InlineData("[general]\nbalwaysactive=0\nother=7", "[general]\r\nbAlwaysActive=1\r\nother=7\r\n")]
    [InlineData("[General]\nbAlwaysActive=0\nbAlwaysActive=0\n[Other]\nbAlwaysActive=0", "[General]\r\nbAlwaysActive=1\r\n[Other]\r\nbAlwaysActive=0\r\n")]
    [InlineData("[General]\n;note\n[Display]\nx=1", "[General]\r\n;note\r\nbAlwaysActive=1\r\n[Display]\r\nx=1\r\n")]
    public void SettingPreservesOtherSectionsAndRemovesDuplicateValues(string input, string expected)
    {
        Assert.Equal(expected, RuntimeLiveConfiguration.SetValue(input, "General", "bAlwaysActive", "1"));
    }
}
