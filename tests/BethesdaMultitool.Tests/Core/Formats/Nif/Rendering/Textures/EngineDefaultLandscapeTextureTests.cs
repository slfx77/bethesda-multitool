using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Pins the GAME-KEYED engine-default landscape texture. Regression guard for "missing terrain
///     textures" on non-FNV games: the default (bound for no-BTXT quadrants + as the LTEX-chain fallback)
///     was hardcoded to FNV's <c>DirtWasteland01</c>, which is absent in Fallout 4 / Oblivion / Skyrim /
///     Fallout 76 archives → their default-textured terrain renders white.
/// </summary>
public class EngineDefaultLandscapeTextureTests
{
    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.Unknown)]
    public void DiffuseFor_UnmappedOrFnvFamily_UsesFnvDefault(BethesdaGame game)
    {
        var fnv = GameProfiles.For(BethesdaGame.FalloutNewVegas);
        Assert.Equal(fnv.DefaultLandscapeDiffuse, EngineDefaultLandscapeTexture.DiffuseFor(game));
        Assert.Equal(fnv.DefaultLandscapeNormal, EngineDefaultLandscapeTexture.NormalFor(game));
    }

    /// <summary>
    ///     Starfield deliberately declares NO engine-default landscape texture. It previously inherited
    ///     FNV's DirtWasteland01, which does not exist in any Starfield archive — so every unresolved
    ///     cell chased a path that could never load. Its terrain diffuse is reachable only through the
    ///     material database; an empty default lets the resolver bind the white-pixel placeholder
    ///     instead, which is the honest "not resolved yet" state.
    /// </summary>
    [Fact]
    public void DiffuseFor_Starfield_HasNoEngineDefault()
    {
        Assert.Equal(string.Empty, EngineDefaultLandscapeTexture.DiffuseFor(BethesdaGame.Starfield));
        Assert.Equal(string.Empty, EngineDefaultLandscapeTexture.NormalFor(BethesdaGame.Starfield));
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout4, @"textures\landscape\ground\CommonwealthDefault01_d.dds",
        @"textures\landscape\ground\CommonwealthDefault01_N.dds")]
    [InlineData(BethesdaGame.Fallout76, @"textures\landscape\ground\CommonwealthDefault01_d.dds",
        @"textures\landscape\ground\CommonwealthDefault01_N.dds")]
    [InlineData(BethesdaGame.Skyrim, @"textures\landscape\Dirt01.dds", @"textures\landscape\Dirt01_n.dds")]
    [InlineData(BethesdaGame.Oblivion, @"textures\landscape\TerrainHDDirt01.dds",
        @"textures\landscape\TerrainHDDirt01_n.dds")]
    // Morrowind hardcodes "_land_default.tga" (string in Morrowind.exe @ 0x3A7750, beside the
    // LandTexture error strings); the BSA ships it as .dds. No normal — the 2002 fixed-function
    // renderer predates normal mapping.
    [InlineData(BethesdaGame.Morrowind, @"textures\_land_default.dds", "")]
    public void DiffuseAndNormalFor_MappedGames_UseGameSpecificDefault(BethesdaGame game, string diffuse, string normal)
    {
        Assert.Equal(diffuse, EngineDefaultLandscapeTexture.DiffuseFor(game));
        Assert.Equal(normal, EngineDefaultLandscapeTexture.NormalFor(game));
        // Crucially NOT the FNV path (which is absent in these games' archives → white base).
        Assert.NotEqual(GameProfiles.For(BethesdaGame.FalloutNewVegas).DefaultLandscapeDiffuse,
            EngineDefaultLandscapeTexture.DiffuseFor(game));
    }
}