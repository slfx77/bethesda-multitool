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
        Assert.All(profile.PrimaryTextureCandidates, p => Assert.EndsWith(".dds", p, System.StringComparison.OrdinalIgnoreCase));
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

        Assert.Contains(profile.PrimaryTextureCandidates, p => p.Contains("tx_masser", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(profile.SecondaryTextureCandidates, p => p.Contains("tx_secunda", System.StringComparison.OrdinalIgnoreCase));
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
            p => p.Contains("secunda", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.PrimaryTextureCandidates,
            p => p.Contains("masser", System.StringComparison.OrdinalIgnoreCase));
        Assert.True(profile.PrimaryUsesSecundaSize, $"{game} moon size must come from iSecundaSize");
        Assert.Equal(@"textures\sky\secunda_full.dds", profile.PhaseTexturePath(secondary: false, 4));
    }

    [Fact]
    public void None_DrawsNoMoon()
    {
        Assert.Equal(0, SkyMoonProfile.None.MoonCount);
        Assert.False(SkyMoonProfile.None.HasMoon);
    }

    [Theory]
    // Engine-exact model: moon half-extent (iMasserSize/iSecundaSize) ÷ sky-dome radius (fSunXExtreme).
    [InlineData(85, 800f, 0.10625f)]  // FNV shipped: iMasserSize 85 / fSunXExtreme 800
    [InlineData(90, 400f, 0.225f)]    // Skyrim shipped: iMasserSize 90 / 400
    [InlineData(40, 400f, 0.10f)]     // Skyrim Secunda: iSecundaSize 40 / 400
    public void FractionFromGmst_ComputesSizeOverDome(int size, float dome, float expected)
    {
        var fraction = SkyMoonProfile.FractionFromGmst(size, dome);

        Assert.NotNull(fraction);
        Assert.Equal(expected, fraction!.Value, 5);
    }

    [Theory]
    [InlineData(null, 400f)]  // no size GMST
    [InlineData(90, null)]    // no dome GMST (e.g. Morrowind TES3)
    [InlineData(90, 0f)]      // non-positive dome → avoid divide-by-zero
    public void FractionFromGmst_MissingOrInvalid_ReturnsNull(int? size, float? dome)
    {
        Assert.Null(SkyMoonProfile.FractionFromGmst(size, dome));
    }
}
