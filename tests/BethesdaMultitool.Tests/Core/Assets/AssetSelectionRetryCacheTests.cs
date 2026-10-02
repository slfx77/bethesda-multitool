using System.Buffers.Binary;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Assets;

public sealed class AssetSelectionRetryCacheTests
{
    [Theory]
    [InlineData(false, ".dds")]
    [InlineData(true, ".dds")]
    [InlineData(false, ".tga")]
    [InlineData(true, ".tga")]
    public void RepeatedDecodeFailuresDoNotAccumulateNegativeKeysAndRecoveryCaches(bool gpu, string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "BMT-AssetRetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        try
        {
            var file = Path.Combine(root, "textures", "fixture.dds");
            File.WriteAllBytes(file, [1, 2, 3]);
            var plan = AssetSourcePlan.FromPaths([root]);
            using var cpu = new NifTextureResolver(plan);
            using var hardware = new NifGpuTextureResolver(plan);
            var request = "textures/fixture" + extension;
            object? Read() => gpu ? hardware.GetTexture(request) : cpu.GetTexture(request);
            int Count() => gpu ? hardware.CacheEntryCount : cpu.CacheEntryCount;

            for (var i = 0; i < 128; i++)
            {
                Assert.Null(Read());
                Assert.Equal(0, Count());
            }

            var bytes = PixelDds();
            File.WriteAllBytes(file, bytes);
            Assert.NotNull(Read());
            var cached = Read();
            Assert.NotNull(cached);
            Assert.Same(cached, Read());
            Assert.Equal(1, Count());
            var selected = (gpu ? hardware.AssetSelection : cpu.AssetSelection)!
                .LastReceipt("textures\\fixture.dds")!;
            Assert.Equal(AssetSelectionStatus.Selected, selected.Status);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), selected.PayloadSha256);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] PixelDds()
    {
        var bytes = new byte[132];
        "DDS "u8.CopyTo(bytes);
        Word(4, 124); Word(8, 0x100F); Word(12, 1); Word(16, 1); Word(20, 4); Word(28, 1);
        Word(76, 32); Word(80, 0x41); Word(88, 32);
        Word(92, 0x000000FF); Word(96, 0x0000FF00); Word(100, 0x00FF0000); Word(104, 0xFF000000);
        Word(108, 0x1000);
        bytes[128] = 31; bytes[129] = 63; bytes[130] = 95; bytes[131] = 255;
        return bytes;
        void Word(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
}
