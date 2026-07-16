using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     TES4 parallax materials (NiTexturingProperty Apply Mode HILIGHT=3 / HILIGHT2=4) repurpose the
///     diffuse alpha channel as a parallax height map. Oblivion ships them WITH a blend-enabled
///     NiAlphaProperty (0x00ED) that the engine does not blend with — SEIsland's rock faces rendered
///     see-through when the mid-gray height data fed SRC_ALPHA blending. The property reader must
///     demote blend for that combination, and ONLY that combination.
/// </summary>
public sealed class Tes4ParallaxAlphaDemotionTests
{
    private const ushort BlendEnabledFlags = 0x00ED; // blend on, src 6 / dst 7, test off
    private const ushort AdditiveBlendFlags = 0x100D; // blend on, src 6 / dst 0, test off

    [Theory]
    [InlineData(3u)] // APPLY_HILIGHT — TES4 parallax
    [InlineData(4u)] // APPLY_HILIGHT2 — TES4 parallax + specular
    public void Tes4ParallaxApplyMode_DemotesBlend(uint applyMode)
    {
        var (data, nif) = BuildTes4Nif(applyMode);

        NifBlockParsers.ReadAlphaProperty(data, nif, [0, 1],
            out var hasAlphaBlend, out var hasAlphaTest, out _, out _, out var src, out var dst);

        Assert.False(hasAlphaBlend);
        Assert.False(hasAlphaTest);
        Assert.Equal(6, src);
        Assert.Equal(7, dst);
    }

    [Theory]
    [InlineData(0u)] // REPLACE
    [InlineData(2u)] // MODULATE — the ordinary textured case
    public void Tes4NonParallaxApplyMode_KeepsBlend(uint applyMode)
    {
        var (data, nif) = BuildTes4Nif(applyMode);

        NifBlockParsers.ReadAlphaProperty(data, nif, [0, 1],
            out var hasAlphaBlend, out _, out _, out _, out _, out _);

        Assert.True(hasAlphaBlend);
    }

    [Theory]
    [InlineData(3u)] // LandscapeWaterfallFoam01: HILIGHT plus additive blending
    [InlineData(4u)]
    public void Tes4AdditiveHilite_KeepsAuthoredBlend(uint applyMode)
    {
        var (data, nif) = BuildTes4Nif(applyMode, AdditiveBlendFlags);

        NifBlockParsers.ReadAlphaProperty(data, nif, [0, 1],
            out var hasAlphaBlend, out _, out _, out _, out var src, out var dst);

        Assert.True(hasAlphaBlend);
        Assert.Equal(6, src);
        Assert.Equal(0, dst);
    }

    [Fact]
    public void Tes4ApplyModeReader_ReturnsAuthoredMode()
    {
        var (data, nif) = BuildTes4Nif(4u);
        Assert.Equal(4u, NifTexturingPropertyReader.ReadApplyMode(data, nif, [0, 1]));
    }

    [Fact]
    public void Fo3EraNif_IsNotTouched()
    {
        // FO3+/FNV NiTexturingProperty stores a flags ushort where TES4 stores Apply Mode; the
        // demotion must never key off that. String-table era layout: name is a 4-byte index.
        var alpha = BuildFo3AlphaProperty();
        var nif = new NifInfo
        {
            IsBigEndian = false,
            BinaryVersion = 0x14020007, // 20.2.0.7 (FNV)
            BsVersion = 34,
            HasInlineStrings = false
        };
        nif.Blocks.Add(new BlockInfo { Index = 0, TypeName = "NiAlphaProperty", DataOffset = 0, Size = alpha.Length });

        NifBlockParsers.ReadAlphaProperty(alpha, nif, [0],
            out var hasAlphaBlend, out _, out _, out _, out _, out _);

        Assert.True(hasAlphaBlend);
        Assert.Null(NifTexturingPropertyReader.ReadApplyMode(alpha, nif, [0]));
    }

    // TES4 (20.0.0.5, inline strings) property block header: name SizedString(len=0) + numExtra(0)
    // + controllerRef(-1) — 12 bytes — then the block's own fields.
    private static (byte[] Data, NifInfo Nif) BuildTes4Nif(uint applyMode, ushort alphaFlags = BlendEnabledFlags)
    {
        // Block 0 @ 0: NiAlphaProperty = header(12) + flags(2) + threshold(1) = 15 bytes
        // Block 1 @ 16: NiTexturingProperty = header(12) + applyMode(4) = 16 bytes
        var data = new byte[40];
        WriteInlineObjectNetHeader(data, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), alphaFlags);
        data[14] = 0; // threshold

        WriteInlineObjectNetHeader(data, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), applyMode);

        var nif = new NifInfo
        {
            IsBigEndian = false,
            BinaryVersion = 0x14000005, // 20.0.0.5 (Oblivion)
            BsVersion = 11,
            HasInlineStrings = true
        };
        nif.Blocks.Add(new BlockInfo { Index = 0, TypeName = "NiAlphaProperty", DataOffset = 0, Size = 15 });
        nif.Blocks.Add(new BlockInfo { Index = 1, TypeName = "NiTexturingProperty", DataOffset = 16, Size = 16 });
        return (data, nif);
    }

    private static byte[] BuildFo3AlphaProperty()
    {
        // String-table era NiObjectNET: nameIndex(4, -1) + numExtra(4, 0) + controllerRef(4, -1),
        // then flags(2) + threshold(1).
        var data = new byte[15];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), -1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), -1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), BlendEnabledFlags);
        return data;
    }

    private static void WriteInlineObjectNetHeader(byte[] data, int offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), 0); // name SizedString len = 0
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4), 0); // extra data count
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 8), -1); // controller ref
    }
}
