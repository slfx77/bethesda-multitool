using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Hand-laid legacy DDS files for texture-descriptor tests, written from Microsoft's DDS_HEADER and DDS_PIXELFORMAT
///     layout (magic, 124-byte header, 32-byte pixel format at offset 76, caps at 108, caps2 at 112) without any BMT or
///     Shared DDS code, so a descriptor is never compared with the reader that produced it.
/// </summary>
internal static class SyntheticDds
{
    /// <summary>A DXT1 block in four-color mode (color0 &gt; color1): selector 3 is a color, never transparent.</summary>
    public static readonly byte[] OpaqueDxt1Block = [0xFF, 0xFF, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>
    ///     A DXT1 block in three-color mode (color0 &lt;= color1) whose every texel selects 3: transparent black.
    /// </summary>
    public static readonly byte[] TransparentDxt1Block = [0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>
    ///     Control for <see cref="TransparentDxt1Block" />: the same three-color endpoints, but every texel selects 0, so
    ///     no visible texel is transparent.
    /// </summary>
    public static readonly byte[] ThreeColorOpaqueDxt1Block = [0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Builds a FourCC DDS: header plus <paramref name="data" /> (every stored level, concatenated).</summary>
    /// <param name="fourCc">DXT1, DXT5, ATI1, ATI2...</param>
    /// <param name="width">Level-zero width.</param>
    /// <param name="height">Level-zero height.</param>
    /// <param name="data">The block data.</param>
    /// <param name="mipCount">The stored mip count.</param>
    /// <param name="cube">Declare a full cube map (caps2 cubemap plus all six faces).</param>
    public static byte[] FourCc(string fourCc, int width, int height, byte[] data, int mipCount = 1,
        bool cube = false)
    {
        var header = new byte[128];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(header, 0);
        Write(header, 4, 124);
        var blockBytes = fourCc is "DXT1" or "ATI1" ? 8 : 16;
        var linear = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;
        Write(header, 8, 0x1u | 0x2u | 0x4u | 0x1000u | 0x80000u | (mipCount > 1 ? 0x20000u : 0u));
        Write(header, 12, (uint)height);
        Write(header, 16, (uint)width);
        Write(header, 20, (uint)linear);
        Write(header, 28, (uint)mipCount);
        Write(header, 76, 32);
        Write(header, 80, 0x4);
        Encoding.ASCII.GetBytes(fourCc).CopyTo(header, 84);
        Write(header, 108, 0x1000u | (mipCount > 1 ? 0x400008u : 0u) | (cube ? 0x8u : 0u));
        Write(header, 112, cube ? 0x200u | 0xFC00u : 0u);
        return [.. header, .. data];
    }

    /// <summary>A 4x4 DXT1 DDS holding one block.</summary>
    public static byte[] Dxt1Single(byte[] block)
    {
        return FourCc("DXT1", 4, 4, block);
    }

    /// <summary>
    ///     Builds a DX10-extension DDS: the 128-byte header, the 20-byte DDS_HEADER_DXT10 (both hand-laid from
    ///     Microsoft's layout: dxgiFormat at 128, resourceDimension 3, miscFlag, arraySize 1, miscFlags2 at 144), then
    ///     <paramref name="data" /> (every stored level, concatenated).
    /// </summary>
    /// <param name="dxgiFormat">The DXGI_FORMAT value (98 BC7_UNORM, 99 BC7_UNORM_SRGB, 95 BC6H_UF16, 10 half-float RGBA...).</param>
    /// <param name="width">Level-zero width.</param>
    /// <param name="height">Level-zero height.</param>
    /// <param name="data">The stored surface bytes.</param>
    /// <param name="mipCount">The stored mip count.</param>
    /// <param name="alphaMode">The DDS_HEADER_DXT10 miscFlags2 alpha mode (0 unknown, 1 straight).</param>
    public static byte[] Dx10(uint dxgiFormat, int width, int height, byte[] data, int mipCount = 1,
        uint alphaMode = 0)
    {
        var header = new byte[148];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(header, 0);
        Write(header, 4, 124);
        Write(header, 8, 0x1u | 0x2u | 0x4u | 0x1000u | (mipCount > 1 ? 0x20000u : 0u));
        Write(header, 12, (uint)height);
        Write(header, 16, (uint)width);
        Write(header, 28, (uint)mipCount);
        Write(header, 76, 32);
        Write(header, 80, 0x4);
        Encoding.ASCII.GetBytes("DX10").CopyTo(header, 84);
        Write(header, 108, 0x1000u | (mipCount > 1 ? 0x400008u : 0u));
        Write(header, 128, dxgiFormat);
        Write(header, 132, 3); // resourceDimension: texture 2D
        Write(header, 136, 0); // miscFlag
        Write(header, 140, 1); // arraySize
        Write(header, 144, alphaMode);
        return [.. header, .. data];
    }

    /// <summary>Repeats a block <paramref name="count" /> times.</summary>
    public static byte[] Repeat(byte[] block, int count)
    {
        var data = new byte[block.Length * count];
        for (var i = 0; i < count; i++)
        {
            block.CopyTo(data, i * block.Length);
        }

        return data;
    }

    private static void Write(byte[] buffer, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);
    }
}
