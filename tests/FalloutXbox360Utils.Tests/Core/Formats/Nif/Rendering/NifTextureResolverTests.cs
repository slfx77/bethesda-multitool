using System.Buffers.Binary;
using FalloutXbox360Utils.CLI.Rendering.Nif;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using FalloutXbox360Utils.Tests;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifTextureResolverTests
{
    [Fact]
    public void TexturePathUtility_NormalizesSeparatorsAndPrefix()
    {
        var normalized = NifTexturePathUtility.Normalize(@"characters/boone/face.dds");

        Assert.Equal(@"textures\characters\boone\face.dds", normalized);
    }

    [Fact]
    public void ResolveTextureSetPathsAndShaderFlags_FromLightingProperty()
    {
        const string diffusePath = @"textures\characters\boone\face.dds";
        const string normalPath = @"textures\characters\boone\face_n.dds";

        var textureSetOffset = 48;
        var textureSetSize = 4 + 4 + diffusePath.Length + 4 + normalPath.Length;
        var data = new byte[textureSetOffset + textureSetSize];

        WriteNiObjectNetHeader(data, 0);
        WriteUInt16(data, 12, 0);
        WriteInt32(data, 14, 2);
        WriteUInt32(data, 18, 0x20000u);
        WriteUInt32(data, 22, 1u << 5);
        WriteFloat(data, 26, 0.75f);
        WriteUInt32(data, 30, 0);
        WriteInt32(data, 34, 1);

        var pos = textureSetOffset;
        WriteUInt32(data, pos, 2);
        pos += 4;
        WriteSizedString(data, ref pos, diffusePath);
        WriteSizedString(data, ref pos, normalPath);

        var nif = CreateNifInfo(
            ("BSShaderPPLightingProperty", 0, 38),
            ("BSShaderTextureSet", textureSetOffset, textureSetSize));

        var propertyRefs = new List<int> { 0 };
        var resolvedDiffuse = NifTextureResolver.ResolveDiffusePath(data, nif, propertyRefs);
        var resolvedNormal = NifTextureResolver.ResolveNormalMapPath(data, nif, propertyRefs);
        var metadata = NifTextureResolver.ReadShaderMetadata(data, nif, propertyRefs);
        var shaderFlags2 = NifTextureResolver.ReadShaderFlags2(data, nif, propertyRefs);
        var envMapInfo = NifTextureResolver.ReadEnvMapInfo(data, nif, propertyRefs);

        Assert.Equal(diffusePath, resolvedDiffuse);
        Assert.Equal(normalPath, resolvedNormal);
        Assert.NotNull(metadata);
        Assert.Equal(8, metadata.TextureSlots.Count);
        Assert.Equal(diffusePath, metadata.GetTextureSlot(0));
        Assert.Equal(normalPath, metadata.GetTextureSlot(1));
        for (var slot = 2; slot < 8; slot++)
        {
            Assert.Null(metadata.GetTextureSlot(slot));
        }

        Assert.Equal(1u << 5, shaderFlags2);
        Assert.NotNull(envMapInfo);
        Assert.Equal(0x20000u, envMapInfo.Value.ShaderFlags);
        Assert.Equal(0.75f, envMapInfo.Value.EnvMapScale, 3);
    }

    [Fact]
    public void ReadShaderMetadata_FromNoLightingProperty_UsesFixedSlotLayout()
    {
        const string diffusePath = @"textures\effects\neon.dds";

        var data = new byte[96];
        WriteNiObjectNetHeader(data, 0);
        WriteUInt16(data, 12, 0);
        WriteUInt32(data, 14, 7);
        WriteUInt32(data, 18, 1u << 25);
        WriteUInt32(data, 22, 0);
        WriteFloat(data, 26, 0f);
        WriteUInt32(data, 30, 0);

        var pos = 34;
        WriteSizedString(data, ref pos, diffusePath);

        var nif = CreateNifInfo(("BSShaderNoLightingProperty", 0, pos));
        var metadata = NifTextureResolver.ReadShaderMetadata(data, nif, [0]);

        Assert.NotNull(metadata);
        Assert.Equal("BSShaderNoLightingProperty", metadata.PropertyType);
        Assert.True(metadata.HasRemappableTextures);
        Assert.Equal(diffusePath, metadata.DiffusePath);
        Assert.Equal(8, metadata.TextureSlots.Count);
        for (var slot = 1; slot < 8; slot++)
        {
            Assert.Null(metadata.GetTextureSlot(slot));
        }
    }

    [Fact]
    public void ResolveLooseTexture_FromUnpackedDataRoot_LoadsDecodedTexture()
    {
        var nifPath = SampleFileFixture.FindSamplePath(
            @"Sample\Unpacked_Builds\360_July_Unpacked\FalloutNV\Data\meshes\architecture\barracks\barracks01.nif");
        Assert.SkipWhen(nifPath is null, "Unpacked July NIF sample not available");

        Assert.True(NifExportPathResolver.TryDetectDataRoot(nifPath!, out var dataRoot));

        using var resolver = new NifTextureResolver(dataRoot);
        var texture = resolver.GetTexture(@"textures\architecture\barracks\barracks01.dds");

        Assert.NotNull(texture);
        Assert.True(texture.Width > 0);
        Assert.True(texture.Height > 0);
    }

    private static NifInfo CreateNifInfo(params (string TypeName, int DataOffset, int Size)[] blocks)
    {
        var nif = new NifInfo
        {
            IsBigEndian = false,
            BsVersion = 34
        };

        for (var i = 0; i < blocks.Length; i++)
        {
            nif.Blocks.Add(new BlockInfo
            {
                Index = i,
                TypeName = blocks[i].TypeName,
                DataOffset = blocks[i].DataOffset,
                Size = blocks[i].Size
            });
        }

        return nif;
    }

    private static void WriteNiObjectNetHeader(byte[] data, int offset)
    {
        WriteInt32(data, offset, 0);
        WriteUInt32(data, offset + 4, 0);
        WriteInt32(data, offset + 8, -1);
    }

    private static void WriteSizedString(byte[] data, ref int offset, string value)
    {
        WriteUInt32(data, offset, (uint)value.Length);
        offset += 4;
        for (var i = 0; i < value.Length; i++)
        {
            data[offset + i] = (byte)value[i];
        }

        offset += value.Length;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void WriteInt32(byte[] data, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void WriteUInt16(byte[] data, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);

    private static void WriteFloat(byte[] data, int offset, float value)
        => WriteInt32(data, offset, BitConverter.SingleToInt32Bits(value));
}
