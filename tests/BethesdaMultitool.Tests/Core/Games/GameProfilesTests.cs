using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Games;

/// <summary>
///     Pins the game registry: every game has a profile (so a new <see cref="BethesdaGame" /> value
///     can't be added without a matching entry), and the two pure resolvers behave as the detector
///     relies on. The HEDR-version switch mirrors the historical <c>PluginFormat.DetectTes4Game</c>
///     thresholds — changing it shifts coarse detection for every 24-byte game.
/// </summary>
public class GameProfilesTests
{
    [Theory]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    [InlineData(BethesdaGame.Starfield)]
    public void For_EveryKnownGame_ReturnsMatchingProfile(BethesdaGame game)
    {
        var profile = GameProfiles.For(game);
        Assert.Equal(game, profile.Game);
        Assert.False(string.IsNullOrWhiteSpace(profile.DisplayName));
    }

    [Fact]
    public void Registry_CoversEveryEnumValueExceptUnknown()
    {
        var expected = Enum.GetValues<BethesdaGame>().Where(g => g != BethesdaGame.Unknown).ToHashSet();
        var actual = GameProfiles.All.Select(p => p.Game).ToHashSet();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void For_Unknown_ReturnsNeutral24ByteDefault()
    {
        var profile = GameProfiles.For(BethesdaGame.Unknown);
        Assert.Equal(BethesdaGame.Unknown, profile.Game);
        Assert.Equal(24, profile.RecordHeaderSize);
    }

    [Theory]
    [InlineData(263.0f, BethesdaGame.Fallout76)] // SeventySix.esm — unambiguously high
    [InlineData(1.34f, BethesdaGame.FalloutNewVegas)]
    [InlineData(1.0f, BethesdaGame.Starfield)] // [0.955, 1.30) band
    [InlineData(0.96f, BethesdaGame.Starfield)]
    [InlineData(0.95f, BethesdaGame.Fallout4)] // [0.945, 0.955)
    [InlineData(0.94f, BethesdaGame.Fallout3)] // [0.93, 0.945) — overlaps Skyrim; name refinement disambiguates
    [InlineData(0.5f, BethesdaGame.FalloutNewVegas)] // default
    public void ResolveByHedrVersion_MatchesHistoricalThresholds(float version, BethesdaGame expected)
        => Assert.Equal(expected, GameProfiles.ResolveByHedrVersion(version));

    [Theory]
    [InlineData(BethesdaGame.Skyrim, "Skyrim.esm")]
    [InlineData(BethesdaGame.FalloutNewVegas, "FalloutNV.esm")]
    [InlineData(BethesdaGame.Fallout76, "SeventySix.esm")]
    [InlineData(BethesdaGame.Starfield, "Starfield.esm")]
    public void ResolveByNames_MatchesGameByHint(BethesdaGame expected, string name)
        => Assert.Equal(expected, GameProfiles.ResolveByNames([name]));

    [Fact]
    public void ResolveByNames_PrefersNewerGameWhenMultipleMatch()
    {
        // A plugin mastered on both FO4 and FNV is an FO4 plugin (FO4 outranks FNV in name priority).
        Assert.Equal(BethesdaGame.Fallout4, GameProfiles.ResolveByNames(["FalloutNV.esm", "Fallout4.esm"]));
    }

    [Theory]
    [InlineData("RandomMod.esp")]
    [InlineData("")]
    public void ResolveByNames_NoMatch_ReturnsNull(string name)
        => Assert.Null(GameProfiles.ResolveByNames([name]));

    [Fact]
    public void Profiles_PinKeyCapabilities()
    {
        // The FNV-only armor Damage Threshold capability (replaces the old `game == FNV` branch).
        Assert.True(GameProfiles.For(BethesdaGame.FalloutNewVegas).HasArmorDamageThreshold);
        Assert.False(GameProfiles.For(BethesdaGame.Fallout3).HasArmorDamageThreshold);

        // Only Morrowind is the TES3 family.
        Assert.True(GameProfiles.For(BethesdaGame.Morrowind).IsTes3);
        Assert.False(GameProfiles.For(BethesdaGame.Oblivion).IsTes3);
    }
}
