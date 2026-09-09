using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

public sealed class SkyrimDefaultNormalMapSentinelTests
{
    [Fact]
    public void LightingTextureSet_DoesNotExposeDefaultNormalSentinelAsAFilePath()
    {
        const string diffusePath = @"textures\plants\Potato01.dds";
        const string defaultNormalSentinel = "\bNOR";
        const int textureSetOffset = 64;
        var textureSetSize = sizeof(uint) +
                             sizeof(uint) + diffusePath.Length +
                             sizeof(uint) + defaultNormalSentinel.Length +
                             7 * sizeof(uint);
        var data = new byte[textureSetOffset + textureSetSize];

        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), 0); // Shader Type
        WriteNiObjectNetHeader(data, 4);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), 1); // Texture Set block

        var position = textureSetOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(position), 9);
        position += sizeof(uint);
        WriteSizedString(data, ref position, diffusePath);
        WriteSizedString(data, ref position, defaultNormalSentinel);
        for (var slot = 2; slot < 9; slot++)
        {
            WriteSizedString(data, ref position, string.Empty);
        }

        var nif = new NifInfo
        {
            BinaryVersion = 0x14020007,
            BsVersion = 83
        };
        nif.Blocks.Add(new BlockInfo
        {
            Index = 0,
            TypeName = "BSLightingShaderProperty",
            DataOffset = 0,
            Size = 44
        });
        nif.Blocks.Add(new BlockInfo
        {
            Index = 1,
            TypeName = "BSShaderTextureSet",
            DataOffset = textureSetOffset,
            Size = textureSetSize
        });

        var metadata = Assert.IsType<NifShaderTextureMetadata>(
            NifTextureResolver.ReadShaderMetadata(data, nif, [0]));

        Assert.Equal(diffusePath, metadata.DiffusePath);
        Assert.Null(metadata.NormalMapPath);
        Assert.DoesNotContain(metadata.TextureSlots, static path => path?.Any(char.IsControl) == true);
    }

    private static void WriteNiObjectNetHeader(byte[] data, int offset)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), -1); // Name
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4), 0); // Extra Data count
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 8), -1); // Controller
    }

    private static void WriteSizedString(byte[] data, ref int offset, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), (uint)bytes.Length);
        offset += sizeof(uint);
        bytes.CopyTo(data, offset);
        offset += bytes.Length;
    }
}

[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class SkyrimDefaultNormalMapSentinelRetailTests
{
    private static readonly string? ArchivePath =
        RealAssetPaths.SteamGameFile("Skyrim", @"Data\Skyrim - Meshes.bsa");

    [Fact]
    public void SkyrimLePotato_DoesNotQueueItsDefaultNormalSentinelAsAnArchivePath()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = Environment.GetEnvironmentVariable("SKYRIM_MESHES_BSA") ?? ArchivePath;
        Assert.SkipWhen(!File.Exists(archivePath),
            "Skyrim LE Meshes BSA not installed (set SKYRIM_MESHES_BSA to run this probe)");

        using var archive = ArchiveReader.Open(archivePath);
        var data = Assert.IsType<byte[]>(archive.ReadFile(@"meshes\plants\potato01.nif"));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(data, nif));
        var potato = Assert.Single(model.Submeshes);

        Assert.EndsWith(@"plants\Potato01.dds", potato.DiffuseTexturePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(potato.NormalMapTexturePath);
    }
}