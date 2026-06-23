using System.Buffers.Binary;
using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifTextureResolverTests
{
    [Fact]
    public void TexturePathUtility_NormalizesSeparatorsAndPrefix()
    {
        var normalized = NifTexturePathUtility.Normalize(@"characters/boone/face.dds");

        Assert.Equal(@"textures\characters\boone\face.dds", normalized);
    }

    [Fact]
    public void TexturePathUtility_StripsLeadingDataPrefix()
    {
        // Vanilla FNV's WATR DefaultWater (FormID 0x00000018) stores its NNAM as
        // "data\textures\water\genaratednoise01.dds" — the engine strips the "data\"
        // before BSA lookup (entries are rooted at Data\). Without the strip we double
        // the prefix into "textures\data\textures\…" and every load misses.
        var normalized = NifTexturePathUtility.Normalize(@"data\textures\water\genaratednoise01.dds");

        Assert.Equal(@"textures\water\genaratednoise01.dds", normalized);
    }

    [Fact]
    public void TexturePathUtility_StripsDataPrefixCaseInsensitive()
    {
        var normalized = NifTexturePathUtility.Normalize(@"Data\textures\water\foo.dds");

        Assert.Equal(@"textures\water\foo.dds", normalized);
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
    public void ReadShaderMetadata_FromEffectShaderProperty_ReadsSourceTextureAsDiffuse()
    {
        // Skyrim/SE/FO4 BSEffectShaderProperty (fire, magic, glow, some ice): the effect texture is the
        // inline "Source Texture" after NiObjectNET + Shader Flags 1(4) + Shader Flags 2(4) + UV Offset(8)
        // + UV Scale(8) = +24 — NOT a BSShaderTextureSet. Regression guard for effect shapes resolving no
        // diffuse and rendering untextured (the "fire shows a floating normal map" look).
        const string sourceTexture = @"textures\effects\FXFireScrollTile02.dds";

        var data = new byte[128];
        WriteNiObjectNetHeader(data, 0); // 0..11
        WriteUInt32(data, 12, 0); // Shader Flags 1
        WriteUInt32(data, 16, 0); // Shader Flags 2
        WriteFloat(data, 20, 0f); // UV Offset.u
        WriteFloat(data, 24, 0f); // UV Offset.v
        WriteFloat(data, 28, 1f); // UV Scale.u
        WriteFloat(data, 32, 1f); // UV Scale.v
        var pos = 36;
        WriteSizedString(data, ref pos, sourceTexture);

        var nif = CreateNifInfo(("BSEffectShaderProperty", 0, pos));
        var metadata = NifTextureResolver.ReadShaderMetadata(data, nif, [0]);

        Assert.NotNull(metadata);
        Assert.Equal("BSEffectShaderProperty", metadata.PropertyType);
        Assert.Equal(sourceTexture, metadata.DiffusePath);
        Assert.Equal(sourceTexture, NifTextureResolver.ResolveDiffusePath(data, nif, [0]));
    }

    [Fact]
    public void NifTexturingProperty_ResolvesBaseTextureFromSourceTexture()
    {
        // Legacy NiTexturingProperty (block 0) → base map TexDesc.Source → NiSourceTexture (block 1)
        // → File Name (string-table index 0). This is the path BSShader* readers ignore; the fallback
        // lets meshes that texture only through NiTexturingProperty resolve a diffuse instead of white.
        const string texturePath = @"textures\effects\vulture01.dds";

        var data = new byte[64];
        // Block 0: NiTexturingProperty @ 0
        WriteNiObjectNetHeader(data, 0); // 0..11
        WriteUInt16(data, 12, 0); // Flags (TexturingFlags)
        WriteUInt32(data, 14, 1); // Texture Count
        data[18] = 1; // Has Base Texture
        WriteInt32(data, 19, 1); // Base Texture TexDesc.Source = block 1
        // Block 1: NiSourceTexture @ 32
        WriteNiObjectNetHeader(data, 32); // 32..43
        data[44] = 1; // Use External
        WriteInt32(data, 45, 0); // File Name = string index 0

        var nif = CreateNifInfo(
            ("NiTexturingProperty", 0, 23),
            ("NiSourceTexture", 32, 17));
        nif.Strings.Add(texturePath);

        var resolved = NifTexturingPropertyReader.ResolveBaseTexturePath(data, nif, [0]);

        Assert.Equal(texturePath, resolved);
    }

    [Fact]
    public void NifTexturingProperty_ReturnsNull_WhenNoBaseTexture()
    {
        var data = new byte[32];
        WriteNiObjectNetHeader(data, 0);
        WriteUInt16(data, 12, 0);
        WriteUInt32(data, 14, 0);
        data[18] = 0; // Has Base Texture = false

        var nif = CreateNifInfo(("NiTexturingProperty", 0, 19));

        Assert.Null(NifTexturingPropertyReader.ResolveBaseTexturePath(data, nif, [0]));
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

    [Fact]
    public void ReadShaderMetadata_Fo4LightingProperty_EmptyInlineSet_FallsBackToBgsmMaterial()
    {
        // FO4 (bsVer 130) BSLightingShaderProperty: most retail meshes leave the inline texture set empty
        // and point the NiObjectNET Name at an external .bgsm material. Layout:
        //   [Shader Type(4)][NiObjectNET(12): Name idx=0, numExtra=0, ctrl=-1]
        //   [Shader Flags1(4)+Flags2(4)+UVoffset(8)+UVscale(8)=24][Texture Set ref(4) = -1 (none)]
        // The fix must read the Name at DataOffset + 4 (past the leading Shader Type) and resolve the .bgsm.
        const string material = @"materials\architecture\building\building01.bgsm";

        var data = new byte[44];
        WriteInt32(data, 0, 7); // Shader Type (skipped)
        WriteNiObjectNetHeader(data, 4); // Name index (0) at +4 → Strings[0]
        WriteInt32(data, 40, -1); // Texture Set ref = none → empty inline set

        var nif = new NifInfo { IsBigEndian = false, BsVersion = 130 };
        nif.Blocks.Add(new BlockInfo
            { Index = 0, TypeName = "BSLightingShaderProperty", DataOffset = 0, Size = 44 });
        nif.Strings.Add(material);

        Assert.Equal(material, NifTextureResolver.ResolveDiffusePath(data, nif, [0]));
    }

    [Fact]
    public void ReadShaderMetadata_SkyrimLightingProperty_EmptyInlineSet_DoesNotFallBackToName()
    {
        // Guard the gate: the material-Name fallback is FO4-only (bsVer 130..<155). A Skyrim LE (bsVer 83)
        // lighting property with an empty inline set must NOT mistake its NiObjectNET Name for a material —
        // it should resolve no diffuse rather than fabricate one.
        const string material = @"materials\architecture\building\building01.bgsm";

        var data = new byte[44];
        WriteInt32(data, 0, 7);
        WriteNiObjectNetHeader(data, 4);
        WriteInt32(data, 40, -1);

        var nif = new NifInfo { IsBigEndian = false, BsVersion = 83 };
        nif.Blocks.Add(new BlockInfo
            { Index = 0, TypeName = "BSLightingShaderProperty", DataOffset = 0, Size = 44 });
        nif.Strings.Add(material);

        Assert.Null(NifTextureResolver.ResolveDiffusePath(data, nif, [0]));
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
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    }

    private static void WriteInt32(byte[] data, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
    }

    private static void WriteFloat(byte[] data, int offset, float value)
    {
        WriteInt32(data, offset, BitConverter.SingleToInt32Bits(value));
    }
}
