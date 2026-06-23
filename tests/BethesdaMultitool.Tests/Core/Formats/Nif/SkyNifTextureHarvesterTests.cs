using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif;

/// <summary>
///     Unit coverage for the FO3/FNV <c>SkyShaderProperty</c> block reader that
///     <see cref="SkyNifTextureHarvester" /> uses to derive a climate's sky-element textures from its
///     MODL sky-dome NIF. The byte layout is the load-bearing risk (hand-parsed), so it's exercised with
///     synthetic blocks built to the exact field order confirmed against the FNV PC <c>Sky\Stars.nif</c>
///     (<c>FileName=textures\sky\SkyStars.dds</c>, <c>SkyObjectType=5/STARS</c>).
/// </summary>
public sealed class SkyNifTextureHarvesterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryReadSkyShaderProperty_ReadsFileNameAndType(bool bigEndian)
    {
        var block = BuildSkyShaderProperty(@"textures\sky\SkyStars.dds", (uint)SkyObjectType.Stars, bigEndian);

        var ok = SkyNifTextureHarvester.TryReadSkyShaderProperty(
            block, 0, block.Length, bigEndian, out var type, out var fileName);

        Assert.True(ok);
        Assert.Equal(SkyObjectType.Stars, type);
        Assert.Equal(@"textures\sky\SkyStars.dds", fileName);
    }

    [Fact]
    public void TryReadSkyShaderProperty_HonorsExtraDataRefs()
    {
        // NumExtraDataList > 0 must skip that many int32 refs before the rest of the fields.
        var block = BuildSkyShaderProperty(@"textures\sky\clouds.dds", (uint)SkyObjectType.Clouds, false, 3);

        var ok = SkyNifTextureHarvester.TryReadSkyShaderProperty(
            block, 0, block.Length, false, out var type, out var fileName);

        Assert.True(ok);
        Assert.Equal(SkyObjectType.Clouds, type);
        Assert.Equal(@"textures\sky\clouds.dds", fileName);
    }

    [Fact]
    public void TryReadSkyShaderProperty_ParsesWithinAnEnclosingBuffer()
    {
        // The harvester passes block.DataOffset into a larger NIF buffer; verify a non-zero offset works
        // and that trailing bytes (the next block) don't bleed into the read.
        var block = BuildSkyShaderProperty(@"textures\sky\SkyStars.dds", (uint)SkyObjectType.Stars, false);
        var buffer = new byte[16 + block.Length + 32];
        block.CopyTo(buffer, 16);

        var ok = SkyNifTextureHarvester.TryReadSkyShaderProperty(
            buffer, 16, block.Length, false, out var type, out var fileName);

        Assert.True(ok);
        Assert.Equal(SkyObjectType.Stars, type);
        Assert.Equal(@"textures\sky\SkyStars.dds", fileName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryReadSkyShaderProperty_ReadsBsSkyShaderPropertyLayout(bool bigEndian)
    {
        // Skyrim's BSSkyShaderProperty has a DIFFERENT header (no 2-byte NiProperty.Flags; shader-flag
        // uints + UV offset/scale) but the SAME trailing FileName + SkyObjectType. The end-anchored parse
        // must read it without knowing the header. Layout confirmed against Skyrim LE Sky\Stars.nif.
        var block = BuildBsSkyShaderProperty(@"textures\sky\SkyStars.dds", (uint)SkyObjectType.Stars, bigEndian);

        var ok = SkyNifTextureHarvester.TryReadSkyShaderProperty(
            block, 0, block.Length, bigEndian, out var type, out var fileName);

        Assert.True(ok);
        Assert.Equal(SkyObjectType.Stars, type);
        Assert.Equal(@"textures\sky\SkyStars.dds", fileName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadSkyObjectType_ReadsTrailingTypeEvenWithoutATexture(bool bigEndian)
    {
        // The gradient atmosphere layer has an EMPTY FileName (SkyObjectType.Sky), so TryReadSkyShaderProperty
        // finds no path — but ReadSkyObjectType still recovers the type from the trailing uint32, which is
        // how the geometry extractor classifies a gradient layer.
        var block = BuildSkyShaderProperty(fileName: "", (uint)SkyObjectType.Sky, bigEndian);

        Assert.Equal(SkyObjectType.Sky, SkyNifTextureHarvester.ReadSkyObjectType(block, 0, block.Length, bigEndian));
        Assert.False(SkyNifTextureHarvester.TryReadSkyShaderProperty(block, 0, block.Length, bigEndian, out _, out _));
    }

    [Fact]
    public void TryReadSkyShaderProperty_RejectsTruncatedBlock()
    {
        var block = BuildSkyShaderProperty(@"textures\sky\SkyStars.dds", (uint)SkyObjectType.Stars, false);
        // Chop the trailing SkyObjectType + into the file-name tail (past the ".dds"): the end-anchored
        // parse can no longer find a length prefix bridging to a path-like ('.') payload, so it bails.
        var truncated = block.AsSpan(0, block.Length - 12).ToArray();

        var ok = SkyNifTextureHarvester.TryReadSkyShaderProperty(
            truncated, 0, truncated.Length, false, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Harvest_ReturnsEmpty_ForNonNifBytes()
    {
        Assert.Empty(SkyNifTextureHarvester.Harvest(new byte[64]));
        Assert.Empty(SkyNifTextureHarvester.Harvest(null));
    }

    // Builds the content of a FO3/FNV SkyShaderProperty block (everything BlockInfo.DataOffset points
    // at) for the given texture + type, in the requested endianness.
    private static byte[] BuildSkyShaderProperty(string fileName, uint type, bool bigEndian, uint numExtra = 0)
    {
        var ms = new MemoryStream();

        void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            }

            ms.Write(b);
        }

        void U16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(b, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            }

            ms.Write(b);
        }

        U32(0xFFFFFFFF); // NiObjectNET.Name = -1
        U32(numExtra); // NiObjectNET.NumExtraDataList
        for (var i = 0; i < numExtra; i++)
        {
            U32(0xFFFFFFFF); // Extra Data ref
        }

        U32(0xFFFFFFFF); // NiObjectNET.Controller = -1
        U16(1); // NiShadeProperty.Flags
        U32(10); // BSShaderProperty.ShaderType (SKY)
        U32(0); // ShaderFlags
        U32(0); // ShaderFlags2
        U32(0x3F800000); // EnvironmentMapScale = 1.0f (bit pattern)
        U32(3); // BSShaderLightingProperty.TextureClampMode

        var name = Encoding.ASCII.GetBytes(fileName);
        U32((uint)name.Length); // FileName SizedString length
        ms.Write(name);
        U32(type); // Sky Object Type

        return ms.ToArray();
    }

    // Builds the content of a Skyrim BSSkyShaderProperty block. Header differs from FO3/FNV (no NiProperty
    // Flags; Shader Flags 1/2 uints + UV offset/scale), but the trailing Source Texture + Sky Object Type
    // are the same. Layout confirmed against Skyrim LE Sky\Stars.nif.
    private static byte[] BuildBsSkyShaderProperty(string fileName, uint type, bool bigEndian)
    {
        var ms = new MemoryStream();

        void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            }

            ms.Write(b);
        }

        U32(0xFFFFFFFF); // NiObjectNET.Name = -1
        U32(0); // NiObjectNET.NumExtraDataList
        U32(0xFFFFFFFF); // NiObjectNET.Controller = -1
        U32(0x80000000); // BSSkyShaderProperty.ShaderFlags1
        U32(0x21); // BSSkyShaderProperty.ShaderFlags2
        U32(0); // UV Offset.u (float 0)
        U32(0); // UV Offset.v
        U32(0x3F800000); // UV Scale.u (float 1.0)
        U32(0x3F800000); // UV Scale.v
        var name = Encoding.ASCII.GetBytes(fileName);
        U32((uint)name.Length); // Source Texture SizedString length
        ms.Write(name);
        U32(type); // Sky Object Type

        return ms.ToArray();
    }
}
