using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Nif.Decoding.NifDecodingTestSupport;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Decoding;

/// <summary>
///     Property blocks through the schema-driven decoder: NiAlphaProperty, BSShaderPPLightingProperty (with its
///     BSShaderTextureSet) and NiMaterialProperty, whose field presence moves with the BS version.
/// </summary>
public class NifBlockDecoderPropertyTests
{
    private static readonly string[] Rgb = ["r", "g", "b"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiAlphaProperty_DecodesFlagsAndThreshold(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        builder.AddBlock("NiAlphaProperty", w =>
        {
            NifTestBlockLayouts.ObjectNet(w, -1);
            w.U16(0x12ED).U8(0x80); // Flags (AlphaFlags, ushort), Threshold (byte)
        });

        var block = DecodeStrict(builder, 0);

        Assert.True(block.IsComplete);
        var flags = block.Root.Get<NifIntegerValue>("Flags");
        Assert.Equal("AlphaFlags", flags.TypeName);
        Assert.Equal("ushort", flags.StorageType);
        Assert.Equal(0x12EDL, flags.Value);
        AssertInteger(block.Root, "Threshold", 0x80);
        Assert.True(block.Root.Get<NifStringValue>("Name").IsNone);
    }

    [Theory]
    [InlineData(false, 14u)]
    [InlineData(false, 21u)]
    [InlineData(false, 26u)]
    [InlineData(false, 32u)]
    [InlineData(false, 34u)]
    [InlineData(true, 14u)]
    [InlineData(true, 21u)]
    [InlineData(true, 26u)]
    [InlineData(true, 32u)]
    [InlineData(true, 34u)]
    public void BSShaderPPLightingProperty_FieldPresenceFollowsTheBsVersion(bool bigEndian, uint bs)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        builder.AddBlock("BSShaderPPLightingProperty", w =>
        {
            NifTestBlockLayouts.ObjectNet(w, -1);
            NifTestBlockLayouts.ShaderLightingPrefix(w, 1, 29, 0x82000001, 0x00000021, 1.25f, 3);
            w.Ref(1); // Texture Set
            if (bs > 14)
            {
                w.F32(0.75f).I32(-5); // Refraction Strength, Refraction Fire Period (int)
            }

            if (bs > 24)
            {
                w.F32(4f).F32(0.04f); // Parallax Max Passes, Parallax Scale
            }
        });
        builder.AddBlock("BSShaderTextureSet", w =>
        {
            w.U32(6);
            w.SizedString(@"textures\clutter\cup.dds").SizedString(@"textures\clutter\cup_n.dds");
            w.SizedString("").SizedString("").SizedString("").SizedString("");
        });

        var decoder = NifDecodingTestSupport.Open(builder.Build());
        var property = decoder.Decode(0, NifDecodeMode.Strict);
        var root = property.Root;

        Assert.True(property.IsComplete);
        AssertInteger(root, "Flags", 1);
        AssertInteger(root, "Shader Type", 29);
        AssertInteger(root, "Shader Flags", 0x82000001);
        AssertInteger(root, "Shader Flags 2", 0x21);
        AssertFloat(root, "Environment Map Scale", 1.25f);
        AssertInteger(root, "Texture Clamp Mode", 3);
        var textureSet = root.Get<NifRefValue>("Texture Set");
        Assert.Equal(1, textureSet.Index);
        Assert.Equal("BSShaderTextureSet", textureSet.Template);

        Assert.Equal(bs > 14, root.Contains("Refraction Strength"));
        Assert.Equal(bs > 14, root.Contains("Refraction Fire Period"));
        Assert.Equal(bs > 24, root.Contains("Parallax Max Passes"));
        Assert.Equal(bs > 24, root.Contains("Parallax Scale"));
        if (bs > 14)
        {
            AssertFloat(root, "Refraction Strength", 0.75f);
            var firePeriod = root.Get<NifIntegerValue>("Refraction Fire Period");
            Assert.True(firePeriod.IsSigned);
            Assert.Equal(-5L, firePeriod.Value);
        }

        if (bs > 24)
        {
            AssertFloat(root, "Parallax Max Passes", 4f);
            AssertFloat(root, "Parallax Scale", 0.04f);
        }

        var set = decoder.Decode(1, NifDecodeMode.Strict);
        Assert.True(set.IsComplete);
        var textures = set.Root.Get<NifArrayValue>("Textures").Items.Cast<NifSizedStringValue>().ToList();
        Assert.Equal(6, textures.Count);
        Assert.Equal(@"textures\clutter\cup.dds", textures[0].Text);
        Assert.Equal(@"textures\clutter\cup_n.dds", textures[1].Text);
        Assert.All(textures.Skip(2), t => Assert.Equal(0, t.RawBytes.Length));
    }

    private static NifTestFileBuilder Material(bool bigEndian, uint bs, bool writeAmbientDiffuse, bool writeEmitMult)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        builder.AddBlock("NiMaterialProperty", w =>
        {
            NifTestBlockLayouts.ObjectNet(w, -1);
            if (writeAmbientDiffuse)
            {
                w.F32s(0.1f, 0.2f, 0.3f); // Ambient Color (BS < 26)
                w.F32s(0.4f, 0.5f, 0.6f); // Diffuse Color (BS < 26)
            }

            w.F32s(0.7f, 0.8f, 0.9f); // Specular Color
            w.F32s(0.05f, 0.1f, 0.15f); // Emissive Color
            w.F32(12.5f).F32(0.75f); // Glossiness, Alpha
            if (writeEmitMult)
            {
                w.F32(2.5f); // Emissive Mult: nif.xml vercond "#BSVER# #GT# 21" (nif.xml:10904-10905)
            }
        });
        return builder;
    }

    [Theory]
    [InlineData(false, 21u)]
    [InlineData(false, 26u)]
    [InlineData(false, 34u)]
    [InlineData(true, 21u)]
    [InlineData(true, 26u)]
    [InlineData(true, 34u)]
    public void NiMaterialProperty_FieldPresenceFollowsTheBsVersion(bool bigEndian, uint bs)
    {
        var block = DecodeStrict(Material(bigEndian, bs, bs < 26, bs > 21), 0);
        var root = block.Root;

        Assert.True(block.IsComplete);
        Assert.Equal(bs < 26, root.Contains("Ambient Color"));
        Assert.Equal(bs < 26, root.Contains("Diffuse Color"));
        Assert.Equal(bs > 21, root.Contains("Emissive Mult"));
        Assert.False(root.Contains("Flags")); // until 10.0.1.2
        if (bs < 26)
        {
            AssertTriple(root, "Ambient Color", Rgb, 0.1f, 0.2f, 0.3f);
            AssertTriple(root, "Diffuse Color", Rgb, 0.4f, 0.5f, 0.6f);
        }

        AssertTriple(root, "Specular Color", Rgb, 0.7f, 0.8f, 0.9f);
        AssertTriple(root, "Emissive Color", Rgb, 0.05f, 0.1f, 0.15f);
        AssertFloat(root, "Glossiness", 12.5f);
        AssertFloat(root, "Alpha", 0.75f);
    }

    /// <summary>
    ///     Required control: at BS 26 Emit Mult is present (nif.xml gates it on BS &gt; 21). NifRenderPropertyReader
    ///     gates it on BS &gt; 26 (Parser/NifRenderPropertyReader.cs:226), which would leave these four bytes unread.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiMaterialProperty_Bs26_DecodesEmitMult(bool bigEndian)
    {
        var block = DecodeStrict(Material(bigEndian, 26, false, true), 0);

        Assert.True(block.IsComplete);
        AssertFloat(block.Root, "Emissive Mult", 2.5f);
        var span = Assert.Single(block.Spans, s => s.Path == "Emissive Mult");
        Assert.Equal(block.Offset + block.Size - 4, span.Offset);
        Assert.Equal(4, span.Length);
    }

    /// <summary>
    ///     The discriminating half of the Emit Mult control: a BS 26 block written by a BS &gt; 26 gate (no Emit Mult)
    ///     is rejected, and so is a BS 21 block written with it. A decoder with the wrong gate would accept exactly
    ///     these two files and reject the correct ones above.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiMaterialProperty_WrongEmitMultGate_IsRejected(bool bigEndian)
    {
        var missing = Assert.Throws<NifDecodeException>(() => DecodeStrict(Material(bigEndian, 26, false, false), 0));
        Assert.Equal(NifDecodeFailureKind.Data, missing.Failure.Kind);
        Assert.Equal("Emissive Mult", missing.Failure.FieldPath);

        var extra = Assert.Throws<NifDecodeException>(() => DecodeStrict(Material(bigEndian, 21, true, true), 0));
        Assert.Equal(NifDecodeFailureKind.Size, extra.Failure.Kind);
    }
}
