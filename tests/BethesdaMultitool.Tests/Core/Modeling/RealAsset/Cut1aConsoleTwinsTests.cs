using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The checked-in console-twin table (<see cref="Cut1aConsoleTwins" />) against its own contract, without any
///     real asset: every record names the resolution step that found it with one of the two step names the C# reader
///     resolves through, and the parser refuses a record that names another step or none. Control: a generator that
///     wrote any other token, or dropped the member, fails the load instead of passing silently.
/// </summary>
public sealed class Cut1aConsoleTwinsTests
{
    private const string TwinJson = """
        {
         "schema": 1,
         "twins": {
          "00": {
           "entry": "meshes/a.nif", "platform": "x360", "pcPath": "meshes/a.nif",
           "pcSha256": "ab", "pcSize": 1, "pcLayer": "Fallout - Meshes.bsa", "pcStep": "STEP"
          }
         },
         "missing": {}
        }
        """;

    [Fact]
    public void EveryCheckedInTwin_NamesAKnownResolutionStep()
    {
        var path = Cut1aCoverManifest.RepoFile(Cut1aConsoleTwins.RelativePath);
        Assert.SkipWhen(path is null || !File.Exists(path),
            $"{Cut1aConsoleTwins.RelativePath} is not present; run tools/scripts/nif_console_twins.py to generate it.");
        var twins = Cut1aConsoleTwins.Twins;
        Assert.True(twins.Count > 0, $"{Cut1aConsoleTwins.RelativePath} records no twin.");
        foreach (var twin in twins.Values)
        {
            Assert.Contains(twin.PcStep, Cut1aConsoleTwins.StepNames);
            Assert.Equal(64, twin.ConsoleSha256.Length);
        }
    }

    [Theory]
    [InlineData("steamFinalBuild")]
    [InlineData("steamInstall")]
    public void Parse_ReadsTheStep(string step)
    {
        var (twins, missing) = Cut1aConsoleTwins.Parse(TwinJson.Replace("STEP", step, StringComparison.Ordinal));
        var twin = Assert.Single(twins).Value;
        Assert.Equal(step, twin.PcStep);
        Assert.Equal("meshes/a.nif", twin.Entry);
        Assert.Equal("Fallout - Meshes.bsa", twin.PcLayer);
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("elsewhere")]
    [InlineData("SteamFinalBuild")]
    [InlineData("")]
    public void Parse_RejectsAnUnknownStep(string step)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            Cut1aConsoleTwins.Parse(TwinJson.Replace("STEP", step, StringComparison.Ordinal)));
        Assert.Contains("steamFinalBuild, steamInstall", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAMissingStep()
    {
        var json = TwinJson.Replace(", \"pcStep\": \"STEP\"", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("pcStep", json, StringComparison.Ordinal);
        var exception = Assert.Throws<InvalidDataException>(() => Cut1aConsoleTwins.Parse(json));
        Assert.Contains("names no pcStep", exception.Message, StringComparison.Ordinal);
    }
}
