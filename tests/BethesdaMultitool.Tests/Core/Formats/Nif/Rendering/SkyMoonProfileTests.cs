using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Locks the per-game moon contract that drives the v3 sky billboards. Each Bethesda engine draws a
///     different number of moons from different assets (verified against the retail archives), so the
///     viewer resolves the moon from <see cref="SkyMoonProfile.ForGame" /> rather than a shared constant:
///     Morrowind/Oblivion/Skyrim draw two moons (Masser + Secunda), Fallout 3 / New Vegas / 4 / 76 one,
///     Starfield/Unknown none.
/// </summary>
public sealed class SkyMoonProfileTests
{
    [Theory]
    [InlineData(BethesdaGame.Morrowind, 2)]
    [InlineData(BethesdaGame.Oblivion, 2)]
    [InlineData(BethesdaGame.Skyrim, 2)]
    [InlineData(BethesdaGame.Fallout3, 1)]
    [InlineData(BethesdaGame.FalloutNewVegas, 1)]
    [InlineData(BethesdaGame.Fallout4, 1)]
    [InlineData(BethesdaGame.Fallout76, 1)]
    [InlineData(BethesdaGame.Starfield, 0)]
    [InlineData(BethesdaGame.Unknown, 0)]
    public void ForGame_HasExpectedMoonCount(BethesdaGame game, int expectedMoons)
    {
        var profile = SkyMoonProfile.ForGame(game);

        Assert.Equal(expectedMoons, profile.MoonCount);
        Assert.Equal(expectedMoons >= 1, profile.HasMoon);
        Assert.Equal(expectedMoons >= 2, profile.HasSecondMoon);
    }

    [Theory]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void ForGame_MoonGames_HavePrimaryTextureAndPositiveSize(BethesdaGame game)
    {
        var profile = SkyMoonProfile.ForGame(game);

        Assert.NotEmpty(profile.PrimaryTextureCandidates);
        Assert.All(profile.PrimaryTextureCandidates,
            p => Assert.EndsWith(".dds", p, StringComparison.OrdinalIgnoreCase));
        Assert.True(profile.PrimaryHalfSizeFraction > 0f,
            $"{game} primary moon must have a positive size, got {profile.PrimaryHalfSizeFraction}");
    }

    [Theory]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Skyrim)]
    public void ForGame_TwoMoonGames_HaveSecondaryTextureAndSize(BethesdaGame game)
    {
        var profile = SkyMoonProfile.ForGame(game);

        Assert.NotEmpty(profile.SecondaryTextureCandidates);
        Assert.True(profile.SecondaryHalfSizeFraction > 0f);
        // Secunda is the smaller second moon in every two-moon TES game.
        Assert.True(profile.SecondaryHalfSizeFraction < profile.PrimaryHalfSizeFraction,
            $"{game} Secunda should be smaller than Masser");
    }

    [Fact]
    public void ForGame_Morrowind_UsesTes3AssetPaths()
    {
        // Morrowind moons live at textures\tx_*_full.dds (verified in Morrowind.bsa), not the Creation
        // textures\sky\ slot the later games share.
        var profile = SkyMoonProfile.ForGame(BethesdaGame.Morrowind);

        Assert.Contains(profile.PrimaryTextureCandidates,
            p => p.Contains("tx_masser", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(profile.SecondaryTextureCandidates,
            p => p.Contains("tx_secunda", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(2d)]
    [InlineData(24d)]
    public void ForGame_OblivionHidesOpaqueBlackNewMoonStub(double gameDay)
    {
        var profile = SkyMoonProfile.ForGame(BethesdaGame.Oblivion);
        var phase = MoonSky.PhaseIndex(gameDay, MoonSky.MorrowindPhaseLengthDays);

        Assert.Equal(0, phase);
        Assert.Equal(0, profile.HiddenPhaseIndex);
        Assert.Equal(@"textures\sky\masser_new.dds", profile.PhaseTexturePath(false, phase));
        Assert.Equal(@"textures\sky\secunda_new.dds", profile.PhaseTexturePath(true, phase));
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void ForGame_FalloutCreation_DrawsTheSecundaSlot(BethesdaGame game)
    {
        // FO4/FO76 ship both leftover Skyrim moon sets, but the engine's single moon is the SECUNDA
        // artwork — the small white-gray disc (in-game FO4 screenshot matched Secunda_full.DDS);
        // Masser is the red-brown unused half of the pair. The GMST size read must follow the slot.
        var profile = SkyMoonProfile.ForGame(game);

        Assert.Contains(profile.PrimaryTextureCandidates,
            p => p.Contains("secunda", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.PrimaryTextureCandidates,
            p => p.Contains("masser", StringComparison.OrdinalIgnoreCase));
        Assert.True(profile.PrimaryUsesSecundaSize, $"{game} moon size must come from iSecundaSize");
        Assert.Equal(@"textures\sky\secunda_full.dds", profile.PhaseTexturePath(false, 4));
    }

    [Fact]
    public void None_DrawsNoMoon()
    {
        Assert.Equal(0, SkyMoonProfile.None.MoonCount);
        Assert.False(SkyMoonProfile.None.HasMoon);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout3, 85, 800f, 0.166015625f)]
    [InlineData(BethesdaGame.FalloutNewVegas, 85, null, 0.166015625f)]
    [InlineData(BethesdaGame.FalloutNewVegas, 85, 1234f, 0.166015625f)]
    [InlineData(BethesdaGame.Skyrim, 90, 400f, 0.17578125f)]
    [InlineData(BethesdaGame.Skyrim, 40, 400f, 0.078125f)]
    public void HalfSizeFractionFromGmst_RotatedArmUsesRecovered512UnitTranslation(
        BethesdaGame game, int size, float? unrelatedSunRadius, float expected)
    {
        var fraction = SkyMoonProfile.ForGame(game)
            .HalfSizeFractionFromGmst(size, unrelatedSunRadius);

        Assert.NotNull(fraction);
        Assert.Equal(expected, fraction!.Value, 7);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void HalfSizeFractionFromGmst_CreationTriangleRetainsAuthoredPathRadius(BethesdaGame game)
    {
        var fraction = SkyMoonProfile.ForGame(game).HalfSizeFractionFromGmst(75, 600f);

        Assert.NotNull(fraction);
        Assert.Equal(0.125f, fraction!.Value, 7);
    }

    [Theory]
    [InlineData(null, 400f)]
    [InlineData(90, null)]
    [InlineData(90, 0f)]
    public void HalfSizeFractionFromGmst_CreationTriangleMissingOrInvalidRadius_ReturnsNull(
        int? size, float? radius)
    {
        Assert.Null(SkyMoonProfile.ForGame(BethesdaGame.Fallout4)
            .HalfSizeFractionFromGmst(size, radius));
    }

    [Fact]
    public void ForGame_RotatedArmFallbackSizesMatchRetailQuadToArmRatios()
    {
        var fallout = SkyMoonProfile.ForGame(BethesdaGame.FalloutNewVegas);
        var skyrim = SkyMoonProfile.ForGame(BethesdaGame.Skyrim);

        Assert.Equal(85f / 512f, fallout.PrimaryHalfSizeFraction, 7);
        Assert.Equal(90f / 512f, skyrim.PrimaryHalfSizeFraction, 7);
        Assert.Equal(40f / 512f, skyrim.SecondaryHalfSizeFraction, 7);
    }

    [Fact]
    public void HalfSizeFractionFromGmst_RotatedArmStillRequiresAuthoredQuadSize()
    {
        Assert.Null(SkyMoonProfile.ForGame(BethesdaGame.FalloutNewVegas)
            .HalfSizeFractionFromGmst(null, null));
    }
}