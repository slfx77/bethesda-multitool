using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Verifies <see cref="LandscapeTexturePathResolver.ResolveDiffuse" /> walks the
///     LTEX → TXST → DiffuseTexture chain and returns null at any broken link. Shared by the
///     2D map's <c>LandscapeTexturePalette</c> and the 3D viewer's <c>TerrainTextureResolver</c>.
/// </summary>
public sealed class LandscapeTexturePathResolverTests
{
    private const uint LtexFormId = 0x100;
    private const uint TxstFormId = 0x200;
    private const string DiffusePath = "textures/landscape/desertgrass.dds";

    [Fact]
    public void ResolveDiffuse_FullChain_ReturnsDiffusePath()
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>
        {
            [LtexFormId] = new() { FormId = LtexFormId, TextureSetFormId = TxstFormId }
        };
        var txst = new Dictionary<uint, TextureSetRecord>
        {
            [TxstFormId] = new() { FormId = TxstFormId, DiffuseTexture = DiffusePath }
        };

        var result = LandscapeTexturePathResolver.ResolveDiffuse(LtexFormId, ltex, txst);
        Assert.Equal(DiffusePath, result);
    }

    [Fact]
    public void ResolveDiffuse_MissingLtex_ReturnsNull()
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>();
        var txst = new Dictionary<uint, TextureSetRecord>();

        Assert.Null(LandscapeTexturePathResolver.ResolveDiffuse(LtexFormId, ltex, txst));
    }

    [Fact]
    public void ResolveDiffuse_LtexWithoutTxstReference_ReturnsNull()
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>
        {
            [LtexFormId] = new() { FormId = LtexFormId, TextureSetFormId = null }
        };
        var txst = new Dictionary<uint, TextureSetRecord>();

        Assert.Null(LandscapeTexturePathResolver.ResolveDiffuse(LtexFormId, ltex, txst));
    }

    [Fact]
    public void ResolveDiffuse_MissingTxst_ReturnsNull()
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>
        {
            [LtexFormId] = new() { FormId = LtexFormId, TextureSetFormId = TxstFormId }
        };
        var txst = new Dictionary<uint, TextureSetRecord>(); // TxstFormId absent

        Assert.Null(LandscapeTexturePathResolver.ResolveDiffuse(LtexFormId, ltex, txst));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDiffuse_TxstWithEmptyDiffuse_ReturnsNull(string? diffusePath)
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>
        {
            [LtexFormId] = new() { FormId = LtexFormId, TextureSetFormId = TxstFormId }
        };
        var txst = new Dictionary<uint, TextureSetRecord>
        {
            [TxstFormId] = new() { FormId = TxstFormId, DiffuseTexture = diffusePath }
        };

        Assert.Null(LandscapeTexturePathResolver.ResolveDiffuse(LtexFormId, ltex, txst));
    }
}
