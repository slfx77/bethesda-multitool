using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class GpuTextureCacheTests
{
    [Fact]
    public void DescribeMipUploads_ReturnsEveryMipLevel()
    {
        var decoded = DecodedTexture.FromBaseLevel(
            new byte[8 * 4 * 4],
            width: 8,
            height: 4);

        var uploads = GpuTextureCache.DescribeMipUploads(decoded);

        Assert.Collection(
            uploads,
            level =>
            {
                Assert.Equal(8u, level.Width);
                Assert.Equal(4u, level.Height);
                Assert.Equal(128, level.PixelLength);
            },
            level =>
            {
                Assert.Equal(4u, level.Width);
                Assert.Equal(2u, level.Height);
                Assert.Equal(32, level.PixelLength);
            },
            level =>
            {
                Assert.Equal(2u, level.Width);
                Assert.Equal(1u, level.Height);
                Assert.Equal(8, level.PixelLength);
            },
            level =>
            {
                Assert.Equal(1u, level.Width);
                Assert.Equal(1u, level.Height);
                Assert.Equal(4, level.PixelLength);
            });
    }
}
