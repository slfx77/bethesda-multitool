using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifRenderPropertyTests
{
    [Fact]
    public void RenderProperties_ReadStencilAlphaAndModernMaterialValues()
    {
        var data = new byte[128];

        WriteNiObjectNetHeader(data, 0, be: true);
        WriteUInt16(data, 12, (ushort)(3 << 10), be: true);

        WriteNiObjectNetHeader(data, 16, be: true);
        WriteUInt16(data, 28, 4844, be: true);
        data[30] = 96;

        WriteNiObjectNetHeader(data, 32, be: true);
        WriteFloat(data, 72, 0.35f, be: true);

        var nif = CreateNifInfo(
            [("NiStencilProperty", 0, 14),
             ("NiAlphaProperty", 16, 15),
             ("NiMaterialProperty", 32, 48)],
            binaryVersion: 0x14020007,
            bsVersion: 34,
            isBigEndian: true);

        var propertyRefs = new List<int> { 0, 1, 2 };
        var isDoubleSided = NifBlockParsers.ReadIsDoubleSided(data, nif, propertyRefs);
        NifBlockParsers.ReadAlphaProperty(
            data,
            nif,
            propertyRefs,
            out var hasAlphaBlend,
            out var hasAlphaTest,
            out var alphaTestThreshold,
            out var alphaTestFunction,
            out var srcBlendMode,
            out var dstBlendMode);
        var materialAlpha = NifBlockParsers.ReadMaterialAlpha(data, nif, propertyRefs);

        Assert.True(isDoubleSided);
        Assert.False(hasAlphaBlend);
        Assert.True(hasAlphaTest);
        Assert.Equal((byte)96, alphaTestThreshold);
        Assert.Equal((byte)4, alphaTestFunction);
        Assert.Equal((byte)6, srcBlendMode);
        Assert.Equal((byte)7, dstBlendMode);
        Assert.Equal(0.35f, materialAlpha, 3);
    }

    [Fact]
    public void MaterialAlpha_ReadsLegacyLayout()
    {
        var data = new byte[128];

        WriteNiObjectNetHeader(data, 0, be: false);
        WriteUInt16(data, 12, 0, be: false);
        WriteFloat(data, 66, 0.6f, be: false);

        var nif = CreateNifInfo(
            [("NiMaterialProperty", 0, 70)],
            binaryVersion: 0x0A000100,
            bsVersion: 20,
            isBigEndian: false);

        var materialAlpha = NifBlockParsers.ReadMaterialAlpha(data, nif, [0]);

        Assert.Equal(0.6f, materialAlpha, 3);
    }

    private static NifInfo CreateNifInfo(
        (string TypeName, int DataOffset, int Size)[] blocks,
        uint binaryVersion,
        uint bsVersion,
        bool isBigEndian)
    {
        var nif = new NifInfo
        {
            BinaryVersion = binaryVersion,
            IsBigEndian = isBigEndian,
            BsVersion = bsVersion
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

    private static void WriteNiObjectNetHeader(byte[] data, int offset, bool be)
    {
        WriteInt32(data, offset, -1, be);
        WriteUInt32(data, offset + 4, 0, be);
        WriteInt32(data, offset + 8, -1, be);
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value, bool be)
    {
        if (be)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), value);
            return;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value, bool be)
    {
        if (be)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    }

    private static void WriteInt32(byte[] data, int offset, int value, bool be)
    {
        if (be)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset, 4), value);
            return;
        }

        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);
    }

    private static void WriteFloat(byte[] data, int offset, float value, bool be)
        => WriteInt32(data, offset, BitConverter.SingleToInt32Bits(value), be);
}
