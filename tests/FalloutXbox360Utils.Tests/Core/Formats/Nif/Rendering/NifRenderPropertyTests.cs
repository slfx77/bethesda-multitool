using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifRenderPropertyTests
{
    [Fact]
    public void RenderProperties_ReadStencilAlphaAndMaterialValues()
    {
        var data = new byte[128];

        WriteNiObjectNetHeader(data, 0);
        WriteUInt16(data, 12, (ushort)(3 << 11));

        WriteNiObjectNetHeader(data, 16);
        WriteUInt16(data, 28, 4845);
        data[30] = 96;

        WriteNiObjectNetHeader(data, 32);
        WriteFloat(data, 96, 0.35f);

        var nif = CreateNifInfo(
            ("NiStencilProperty", 0, 14),
            ("NiAlphaProperty", 16, 15),
            ("NiMaterialProperty", 32, 68));

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
        Assert.True(hasAlphaBlend);
        Assert.True(hasAlphaTest);
        Assert.Equal((byte)96, alphaTestThreshold);
        Assert.Equal((byte)4, alphaTestFunction);
        Assert.Equal((byte)6, srcBlendMode);
        Assert.Equal((byte)7, dstBlendMode);
        Assert.Equal(0.35f, materialAlpha, 3);
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

    private static void WriteUInt16(byte[] data, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void WriteInt32(byte[] data, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void WriteFloat(byte[] data, int offset, float value)
        => WriteInt32(data, offset, BitConverter.SingleToInt32Bits(value));
}
