using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     v3 Phase 2b — verifies <see cref="TerrainTextureResolver.ResolveLtexToPath" /> walks
///     the LTEX → TXST → DiffuseTexture chain and returns null at any broken link. The actual
///     GPU upload step is exercised separately by the existing GpuTextureCache tests.
/// </summary>
public sealed class TerrainTextureResolverTests
{
    private const uint LtexFormId = 0x100;
    private const uint TxstFormId = 0x200;
    private const string DiffusePath = "textures/landscape/desertgrass.dds";

    [Fact]
    public void ResolveLtexToPath_FullChain_ReturnsDiffusePath()
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>
        {
            [LtexFormId] = new() { FormId = LtexFormId, TextureSetFormId = TxstFormId }
        };
        var txst = new Dictionary<uint, TextureSetRecord>
        {
            [TxstFormId] = new() { FormId = TxstFormId, DiffuseTexture = DiffusePath }
        };

        var result = TerrainTextureResolver.ResolveLtexToPath(LtexFormId, ltex, txst);
        Assert.Equal(DiffusePath, result);
    }

    [Fact]
    public void ResolveLtexToPath_MissingLtex_ReturnsNull()
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>();
        var txst = new Dictionary<uint, TextureSetRecord>();

        Assert.Null(TerrainTextureResolver.ResolveLtexToPath(LtexFormId, ltex, txst));
    }

    [Fact]
    public void ResolveLtexToPath_LtexWithoutTxstReference_ReturnsNull()
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>
        {
            [LtexFormId] = new() { FormId = LtexFormId, TextureSetFormId = null }
        };
        var txst = new Dictionary<uint, TextureSetRecord>();

        Assert.Null(TerrainTextureResolver.ResolveLtexToPath(LtexFormId, ltex, txst));
    }

    [Fact]
    public void ResolveLtexToPath_MissingTxst_ReturnsNull()
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>
        {
            [LtexFormId] = new() { FormId = LtexFormId, TextureSetFormId = TxstFormId }
        };
        var txst = new Dictionary<uint, TextureSetRecord>(); // TxstFormId absent

        Assert.Null(TerrainTextureResolver.ResolveLtexToPath(LtexFormId, ltex, txst));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveLtexToPath_TxstWithEmptyDiffuse_ReturnsNull(string? diffusePath)
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>
        {
            [LtexFormId] = new() { FormId = LtexFormId, TextureSetFormId = TxstFormId }
        };
        var txst = new Dictionary<uint, TextureSetRecord>
        {
            [TxstFormId] = new() { FormId = TxstFormId, DiffuseTexture = diffusePath }
        };

        Assert.Null(TerrainTextureResolver.ResolveLtexToPath(LtexFormId, ltex, txst));
    }
}
