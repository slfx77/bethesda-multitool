using System.Text.Json;
using Xunit;

namespace BethesdaMultitool.Tests.Helpers;

public sealed class SampleCorpusAliasTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "bethesda-corpus-alias-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Builds\\Historic\\system\\game.app", "Builds\\Example (2004-10, N-Gage - Final)\\system\\game.app")]
    [InlineData("Media\\Historic\\game.zip", "Media\\Example (2004-10, N-Gage - Final)\\game.zip")]
    [InlineData("Sample/Builds/Historic/system/game.app", "Sample\\Builds\\Example (2004-10, N-Gage - Final)\\system\\game.app")]
    [InlineData("Sample\\Media\\historic\\game.zip", "Sample\\Media\\Example (2004-10, N-Gage - Final)\\game.zip")]
    public void PreviousNames_ResolveBuildsAndMediaWithoutChangingInnerPaths(string original, string expected)
    {
        var profile = Profile(new { game = "Example", date = "2004-10", platform = "N-Gage", kind = "Final", previousNames = new[] { "Historic" } });
        Assert.Equal(new[] { original, expected }, SampleCorpus.CandidatesForProfile(original, profile));
    }

    [Fact]
    public void MissingProfile_PreservesLegacyFallback()
    {
        const string original = @"Builds\Historic\game.app";
        Assert.Equal(original, Assert.Single(SampleCorpus.CandidatesForProfile(original, Path.Combine(_scratch, "absent.json"))));
    }

    [Fact]
    public void NamingTemplatesAndVariant_UseDeclaredCanonicalName()
    {
        Directory.CreateDirectory(_scratch);
        var profile = Path.Combine(_scratch, "profile.json");
        File.WriteAllText(profile, """
            {"naming":{"undated":"{platform} - {game} ({kind})"},"builds":[
              {"game":"Example: title","platform":"Card","kind":"Final","variant":"en-us","previousNames":["Historic"]}
            ]}
            """);
        Assert.Contains(@"Media\Card - Example_ title (Final; en-us)\game.zip",
            SampleCorpus.CandidatesForProfile(@"Media\Historic\game.zip", profile));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..")]
    [InlineData("C:\\outside")]
    public void UnsafeProfileAliases_FailClosed(string previous)
    {
        var profile = Profile(new { game = "Example", platform = "Card", kind = "Final", previousNames = new[] { previous } });
        Assert.Throws<InvalidDataException>(() => SampleCorpus.CandidatesForProfile(@"Media\Historic\game.zip", profile).ToArray());
    }

    [Fact]
    public void UnsafeCanonicalName_FailsClosed()
    {
        Directory.CreateDirectory(_scratch);
        var profile = Path.Combine(_scratch, "profile.json");
        File.WriteAllText(profile, """{"naming":{"undated":".."},"builds":[{"previousNames":["Historic"]}]}""");
        Assert.Throws<InvalidDataException>(() => SampleCorpus.CandidatesForProfile(@"Media\Historic\game.zip", profile).ToArray());
    }

    [Fact]
    public void AmbiguousAlias_FailsClosed()
    {
        var profile = Profile(
            new { game = "One", platform = "Card", kind = "Final", previousNames = new[] { "Historic" } },
            new { game = "Two", platform = "Card", kind = "Final", previousNames = new[] { "Historic" } });
        Assert.Throws<InvalidDataException>(() => SampleCorpus.CandidatesForProfile(@"Media\Historic\game.zip", profile).ToArray());
    }

    [Fact]
    public void UnrelatedPaths_AreNotRenamed()
    {
        var profile = Profile(new { game = "Example", platform = "Card", kind = "Final", previousNames = new[] { "Historic" } });
        const string original = @"MemoryDumps\Historic\game.dmp";
        Assert.Equal(original, Assert.Single(SampleCorpus.CandidatesForProfile(original, profile)));
    }

    private string Profile(params object[] builds)
    {
        Directory.CreateDirectory(_scratch);
        var path = Path.Combine(_scratch, "profile.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { builds }));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true);
    }
}
