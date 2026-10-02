using System.Text;
using BCnEncoder.Encoder;
using BCnEncoder.ImageSharp;
using BCnEncoder.Shared;
using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Ddx;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Tests.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Pins the live-viewer half of the Xbox normal/specular conversion. Fallout NV's shipped 360
///     billboard corpus (FancyLads, RepConMuseum, RitasCafe, SunsetSarsaparilla, and the baked ads)
///     carries paired <c>*_n.ddx</c> BC5 normals and <c>*_s.ddx</c> BC4 masks, while the equivalent
///     PC <c>*_n.dds</c> carries the mask in DXT5 alpha.
/// </summary>
public sealed class XboxNormalSpecularPairTests
{
    [Fact]
    public void ConvertDdxNormalPairIfNeeded_Bc5AndBc4_ProducesDxt5WithAuthoredMask()
    {
        var normal = EncodeSolid(8, 8, CompressionFormat.Bc5, new Rgba32(128, 128, 255, 255));
        var specular = EncodeSolid(8, 8, CompressionFormat.Bc4, new Rgba32(48, 0, 0, 255));
        string? requestedCompanion = null;

        var merged = NifTextureLoader.ConvertDdxNormalPairIfNeeded(
            normal,
            @"textures\clutter\billboards\fancylads_n.ddx",
            path =>
            {
                requestedCompanion = path;
                return specular;
            });

        Assert.Equal(@"textures\clutter\billboards\fancylads_s.ddx", requestedCompanion);
        Assert.Equal("DXT5", Encoding.ASCII.GetString(merged, 84, 4));
        var decoded = Assert.IsType<DecodedTexture>(DdsTextureDecoder.Decode(merged));
        var meanAlpha = decoded.Pixels.Where((_, index) => index % 4 == 3).Average(static value => value);
        Assert.InRange(meanAlpha, 40, 56);
    }

    [Fact]
    public void ConvertDdxNormalPairIfNeeded_MissingCompanion_PreservesBc5ZeroMaskSpelling()
    {
        var normal = EncodeSolid(8, 8, CompressionFormat.Bc5, new Rgba32(128, 128, 255, 255));

        var result = NifTextureLoader.ConvertDdxNormalPairIfNeeded(
            normal,
            @"textures\clutter\billboards\fancylads_n.ddx",
            _ => null);

        Assert.Same(normal, result);
        Assert.True(NormalMapMerge.IsAti2(result));
    }

    [Fact]
    public void ConvertDdxNormalPairIfNeeded_MismatchedCompanion_PreservesBc5InsteadOfAcceptingBc1()
    {
        var normal = EncodeSolid(8, 8, CompressionFormat.Bc5, new Rgba32(128, 128, 255, 255));
        var wrongSize = EncodeSolid(4, 4, CompressionFormat.Bc4, new Rgba32(48, 0, 0, 255));

        var result = NifTextureLoader.ConvertDdxNormalPairIfNeeded(
            normal,
            @"textures\clutter\billboards\fancylads_n.ddx",
            _ => wrongSize);

        Assert.Same(normal, result);
        Assert.True(NormalMapMerge.IsAti2(result));
    }

    [Fact]
    public void ConvertDdxNormalPairIfNeeded_ModernDdsPair_RemainsSeparateBc5AndSpecularSlots()
    {
        var normal = EncodeSolid(8, 8, CompressionFormat.Bc5, new Rgba32(128, 128, 255, 255));
        var companionRequested = false;

        var result = NifTextureLoader.ConvertDdxNormalPairIfNeeded(
            normal,
            @"textures\architecture\walls_n.dds",
            _ =>
            {
                companionRequested = true;
                return EncodeSolid(8, 8, CompressionFormat.Bc4, new Rgba32(200, 0, 0, 255));
            });

        Assert.Same(normal, result);
        Assert.True(NormalMapMerge.IsAti2(result));
        Assert.False(companionRequested);
    }

    [Fact]
    public void CacheRevision_ExactDdsDoesNotHideAUsablePairedDdxFallback()
    {
        const string requested = @"textures\clutter\billboards\fancylads_n.dds";
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            requested,
            @"textures\clutter\billboards\fancylads_n.ddx",
            @"textures\clutter\billboards\fancylads_s.ddx"
        };
        var probes = new List<string>();

        var needsRevision = NifGpuTextureResolver.CanDecodePairedXboxNormal(
            requested,
            path =>
            {
                probes.Add(path);
                return present.Contains(path);
            });

        Assert.True(needsRevision);
        Assert.DoesNotContain(
            probes,
            path => string.Equals(path, requested, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            [
                @"textures\clutter\billboards\fancylads_n.ddx",
                @"textures\clutter\billboards\fancylads_s.ddx"
            ],
            probes);
    }

    [Fact]
    public void CacheRevision_ModernDdsWithoutExactDdxPair_IsNotInvalidated()
    {
        const string requested = @"textures\architecture\walls_n.dds";
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            requested,
            @"textures\architecture\walls_s.dds"
        };

        var needsRevision = NifGpuTextureResolver.CanDecodePairedXboxNormal(
            requested,
            present.Contains);

        Assert.False(needsRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GpuResolver_DdsFallbackUsesThePhysicalNormalAndCompanion(bool malformedExactDds)
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        const string requested = @"textures\fixture_n.dds";
        const string normalPath = @"textures\fixture_n.ddx";
        const string specularPath = @"textures\fixture_s.ddx";
        Directory.CreateDirectory(Path.Combine(directory.Path, "textures"));
        var normal = EncodeSolid(8, 8, CompressionFormat.Bc5, new Rgba32(128, 128, 255, 255));
        var specular = EncodeSolid(8, 8, CompressionFormat.Bc4, new Rgba32(48, 0, 0, 255));
        File.WriteAllBytes(Path.Combine(directory.Path, normalPath), normal);
        File.WriteAllBytes(Path.Combine(directory.Path, specularPath), specular);
        if (malformedExactDds)
            File.WriteAllBytes(Path.Combine(directory.Path, requested), [1, 2, 3]);

        using var resolver = new NifGpuTextureResolver(AssetSourcePlan.FromPaths([directory.Path]));
        var payload = Assert.IsType<GpuTexturePayload>(resolver.GetTexture(requested));
        Assert.Equal(GpuTexturePayloadFormat.BC3, payload.Format);
        Assert.Equal("normal-specular-paired", payload.Derivation);
        var normalRead = Assert.Single(payload.AssetReadReceipts, read => read.RequestedPath == normalPath);
        var specularRead = Assert.Single(payload.AssetReadReceipts, read => read.RequestedPath == specularPath);
        Assert.Equal(AssetSelectionStatus.Selected, normalRead.Status);
        Assert.Equal(AssetSelectionStatus.Selected, specularRead.Status);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(normal)), normalRead.PayloadSha256);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(specular)), specularRead.PayloadSha256);

        // Compare actual compressed output against the independently exercised pair converter.
        // This catches a fallback decoded under the original .dds spelling (which skips the mask).
        var expectedDds = NifTextureLoader.ConvertDdxNormalPairIfNeeded(normal, normalPath, _ => specular);
        Assert.Equal(expectedDds.AsSpan(128).ToArray(), payload.MipLevels.SelectMany(level => level.Bytes).ToArray());
    }

    private static byte[] EncodeSolid(int width, int height, CompressionFormat format, Rgba32 color)
    {
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = color;
            }
        }

        var encoder = new BcEncoder
        {
            OutputOptions =
            {
                GenerateMipMaps = false,
                Format = format,
                FileFormat = OutputFileFormat.Dds,
                Quality = CompressionQuality.Fast
            }
        };

        using var stream = new MemoryStream();
        encoder.EncodeToStream(image, stream);
        return stream.ToArray();
    }
}
