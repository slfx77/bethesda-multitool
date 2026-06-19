using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

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
    [InlineData(BethesdaGame.Morrowind)] // not mapped → FNV fallback (no regression)
    [InlineData(BethesdaGame.Starfield)]
    [InlineData(BethesdaGame.Unknown)]
    public void DiffuseFor_UnmappedOrFnvFamily_UsesFnvDefault(BethesdaGame game)
    {
        Assert.Equal(EngineDefaultLandscapeTexture.DiffusePath, EngineDefaultLandscapeTexture.DiffuseFor(game));
        Assert.Equal(EngineDefaultLandscapeTexture.NormalPath, EngineDefaultLandscapeTexture.NormalFor(game));
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout4, @"textures\landscape\ground\CommonwealthDefault01_d.dds",
        @"textures\landscape\ground\CommonwealthDefault01_N.dds")]
    [InlineData(BethesdaGame.Fallout76, @"textures\landscape\ground\CommonwealthDefault01_d.dds",
        @"textures\landscape\ground\CommonwealthDefault01_N.dds")]
    [InlineData(BethesdaGame.Skyrim, @"textures\landscape\Dirt01.dds", @"textures\landscape\Dirt01_n.dds")]
    [InlineData(BethesdaGame.Oblivion, @"textures\landscape\TerrainHDDirt01.dds",
        @"textures\landscape\TerrainHDDirt01_n.dds")]
    public void DiffuseAndNormalFor_MappedGames_UseGameSpecificDefault(BethesdaGame game, string diffuse, string normal)
    {
        Assert.Equal(diffuse, EngineDefaultLandscapeTexture.DiffuseFor(game));
        Assert.Equal(normal, EngineDefaultLandscapeTexture.NormalFor(game));
        // Crucially NOT the FNV path (which is absent in these games' archives → white base).
        Assert.NotEqual(EngineDefaultLandscapeTexture.DiffusePath, EngineDefaultLandscapeTexture.DiffuseFor(game));
    }
}