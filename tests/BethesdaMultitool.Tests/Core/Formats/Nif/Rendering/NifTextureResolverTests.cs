using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

[Trait("Category", TestCategories.BucketB)]
public sealed class NifTextureResolverTests
{
    [Fact]
    public void TexturePathUtility_NormalizesSeparatorsAndPrefix()
    {
        var normalized = NifTexturePathUtility.Normalize(@"characters/boone/face.dds");

        Assert.Equal(@"textures\characters\boone\face.dds", normalized);
    }

    [Fact]
    public void GpuTextureCache_UsesOneKeyForRootedAndArchiveRelativePaths()
    {
        var archiveRelative = GpuTextureCache12.NormalizeCacheKey(
            @"SetDressing\NewsStand\NewStand01_n.dds");
        var textureRooted = GpuTextureCache12.NormalizeCacheKey(
            @"textures/SetDressing/NewsStand/NewStand01_n.dds");

        Assert.Equal(@"textures\setdressing\newsstand\newstand01_n.dds", archiveRelative);
        Assert.Equal(archiveRelative, textureRooted);
    }

    [Fact]
    public void GpuTextureCache_UsesDistinctCanonicalKeysForStarfieldDiffuseAndNormalSlots()
    {
        const string materialPath = @"Data/Materials/Test/Thing.MAT";

        var diffuse = GpuTextureCache12.NormalizeCacheKey(materialPath);
        var normal = GpuTextureCache12.NormalizeCacheKey(materialPath, isNormalMap: true);

        Assert.Equal(@"materials\test\thing.mat", diffuse);
        Assert.Equal(diffuse + MaterialTexturePathResolver.StarfieldNormalMapSuffix, normal);
        Assert.Equal(normal, NifGpuTextureResolver.NormalizeKey(normal));
        Assert.NotEqual(diffuse, normal);

        var whitespaceNormal = GpuTextureCache12.NormalizeCacheKey(
            "  Data/Materials/Test/Thing.MAT  ",
            isNormalMap: true);
        Assert.Equal(normal, whitespaceNormal);
    }

    [Fact]
    public void StarfieldNormalMapRequest_RoundTripsItsMaterialPath()
    {
        var request = MaterialTexturePathResolver.BuildStarfieldNormalMapRequest(
            @"Materials/Test/Thing.MAT");

        Assert.True(MaterialTexturePathResolver.TrySplitStarfieldNormalMapRequest(
            request, out var materialPath));
        Assert.Equal(@"materials\test\thing.mat", materialPath);
        Assert.False(MaterialTexturePathResolver.TrySplitStarfieldNormalMapRequest(
            @"textures\test\thing_n.dds|sfmat-normal", out _));
    }

    [Fact]
    public void StarfieldOpacityMapRequest_RoundTripsAndKeepsADistinctCacheIdentity()
    {
        var request = MaterialTexturePathResolver.BuildStarfieldOpacityMapRequest(
            @"Data/Materials/Test/Thing.MAT");

        Assert.Equal(@"materials\test\thing.mat|sfmat-opacity", request);
        Assert.True(MaterialTexturePathResolver.TrySplitStarfieldOpacityMapRequest(
            request, out var materialPath));
        Assert.Equal(@"materials\test\thing.mat", materialPath);
        Assert.Equal(request, NifGpuTextureResolver.NormalizeKey(request));
        Assert.Equal(request, GpuTextureCache12.NormalizeCacheKey(request));
        Assert.NotEqual(
            GpuTextureCache12.NormalizeCacheKey(materialPath),
            GpuTextureCache12.NormalizeCacheKey(request));
        Assert.False(MaterialTexturePathResolver.TrySplitStarfieldOpacityMapRequest(
            @"textures\test\thing_opacity.dds|sfmat-opacity", out _));
    }

    [Fact]
    public void GpuTextureCacheAliasTrace_MatchesTheOldCaseInsensitiveKeyExactly()
    {
        const string archiveRelative = @"SetDressing\NewsStand\NewStand01_n.dds";
        var aliases = new GpuTextureCache12.LegacyAliasTrace(archiveRelative);

        Assert.Equal(
            archiveRelative,
            GpuTextureCache12.NormalizeLegacyCacheKeyForTrace(
                @"  SetDressing/NewsStand/NewStand01_n.dds  "));
        Assert.False(aliases.Observe(@"setdressing/newsstand/newstand01_N.DDS"));
        Assert.False(aliases.Observe(@"  SETDRESSING\NEWSSTAND\NEWSTAND01_n.dds "));

        var spellingOnlySummary = GpuTextureCache12.BuildAliasTraceSummary(
        [
            new GpuTextureCache12.AliasTraceEntry(
                GpuTextureCache12.NormalizeCacheKey(archiveRelative),
                true,
                4_096,
                aliases)
        ]);
        Assert.Empty(spellingOnlySummary.Groups);
        Assert.Equal(0, spellingOnlySummary.LegacyExtraKeys);

        Assert.True(aliases.Observe(@"textures\SetDressing\NewsStand\NewStand01_n.dds"));
        Assert.Equal(2, aliases.LegacyKeyCount);
    }

    [Fact]
    public void GpuTextureCacheAliasTrace_CountsOnlyCurrentResidentNodes()
    {
        const string residentKey = @"textures\setdressing\newsstand\newstand01_n.dds";
        var residentAliases = new GpuTextureCache12.LegacyAliasTrace(
            @"SetDressing\NewsStand\NewStand01_n.dds");
        residentAliases.Observe(@"textures\SetDressing\NewsStand\NewStand01_n.dds");

        const string pendingKey = @"textures\architecture\pending_d.dds";
        var pendingAliases = new GpuTextureCache12.LegacyAliasTrace(
            @"Architecture\Pending_d.dds");
        pendingAliases.Observe(@"textures\Architecture\Pending_d.dds");

        var summary = GpuTextureCache12.BuildAliasTraceSummary(
        [
            new GpuTextureCache12.AliasTraceEntry(
                residentKey,
                true,
                4_096,
                residentAliases),
            new GpuTextureCache12.AliasTraceEntry(
                pendingKey,
                false,
                8_192,
                pendingAliases),
            new GpuTextureCache12.AliasTraceEntry(
                @"textures\architecture\single_d.dds",
                true,
                2_048,
                new GpuTextureCache12.LegacyAliasTrace(
                    @"textures\Architecture\Single_d.dds"))
        ]);

        Assert.Equal(2, summary.ResidentEntries);
        Assert.Equal(1, summary.NonResidentEntries);
        Assert.Equal(1, summary.LegacyExtraKeys);
        Assert.Equal(4_096L, summary.EstimatedAliasResidentBytesAvoided);
        var group = Assert.Single(summary.Groups);
        Assert.Equal(residentKey, group.CanonicalKey);
        Assert.Equal(4_096L, group.ResidentPayloadBytes);
        Assert.Equal(4_096L, group.EstimatedAvoidedPayloadBytes);
        Assert.Equal(2, group.LegacyKeys.Length);
    }

    [Fact]
    public void GpuTextureCacheAliasTrace_DoesNotCarryAnEvictedNodesHistoryIntoItsReplacement()
    {
        const string canonicalKey = @"textures\setdressing\newsstand\newstand01_n.dds";
        var evictedLifetime = new GpuTextureCache12.LegacyAliasTrace(
            @"SetDressing\NewsStand\NewStand01_n.dds");
        evictedLifetime.Observe(@"textures\SetDressing\NewsStand\NewStand01_n.dds");

        // A replacement TextureUploadNode owns a fresh trace just as production does after the old
        // node is removed from _cache. The retired lifetime is intentionally not part of the live
        // teardown snapshot.
        var replacementLifetime = new GpuTextureCache12.LegacyAliasTrace(
            @"textures\SetDressing\NewsStand\NewStand01_n.dds");
        var summary = GpuTextureCache12.BuildAliasTraceSummary(
        [
            new GpuTextureCache12.AliasTraceEntry(
                canonicalKey,
                true,
                4_096,
                replacementLifetime)
        ]);

        Assert.Empty(summary.Groups);
        Assert.Equal(0, summary.LegacyExtraKeys);
        Assert.Equal(0L, summary.EstimatedAliasResidentBytesAvoided);
    }

    [Fact]
    public void GpuTextureCacheAliasTrace_TraceDisabledNodeNeedsNoTracker()
    {
        var summary = GpuTextureCache12.BuildAliasTraceSummary(
        [
            new GpuTextureCache12.AliasTraceEntry(
                @"textures\architecture\single_d.dds",
                true,
                2_048,
                null)
        ]);

        Assert.Equal(1, summary.ResidentEntries);
        Assert.Empty(summary.Groups);
        Assert.Equal(0L, summary.EstimatedAliasResidentBytesAvoided);
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
    public void TexturePathUtility_StripsAbsoluteFallout4BuildPath()
    {
        // FO4/FO76 ship the developer's absolute build path baked into a NIF's BSLightingShaderProperty
        // Name (and some material texture entries), e.g.
        // "C:\Projects\Fallout4\Build\PC\Data\Materials\Architecture\AsylumDoor01.BGSM". Archives index
        // relative to Data\, so the peel must reach the "...\Data\" segment — without it ~97% of FO76
        // statics resolve no material and render white (the untextured-worldspace bug).
        var normalized = NifTexturePathUtility.Normalize(
            @"C:\Projects\Fallout4\Build\PC\Data\Materials\Architecture\AsylumDoor01.BGSM");

        Assert.Equal(@"materials\architecture\asylumdoor01.bgsm", normalized);
    }

    [Fact]
    public void TexturePathUtility_StripsAbsoluteFallout76BuildPath_ForwardSlashes()
    {
        var normalized = NifTexturePathUtility.Normalize(
            @"C:/Projects/76/Build/PC/Data/Materials/Landscape/Roads/ConcreteHWTrim01.BGSM");

        Assert.Equal(@"materials\landscape\roads\concretehwtrim01.bgsm", normalized);
    }

    [Fact]
    public void TexturePathUtility_StripsAbsoluteBuildPath_TextureUnderData()
    {
        // The same peel applies to texture entries, not just .bgsm material names.
        var normalized = NifTexturePathUtility.Normalize(
            @"C:\Projects\Fallout4\Build\PC\Data\Textures\Architecture\Brick_d.dds");

        Assert.Equal(@"textures\architecture\brick_d.dds", normalized);
    }

    [Fact]
    public void MaterialTexturePathResolver_ResolvesNormalizedDiffuse_FromBgsm()
    {
        // The shared helper both texture resolvers (CPU NifTextureResolver + GPU NifGpuTextureResolver)
        // use to follow a FO4/FO76 .bgsm material to its diffuse texture. Locks that a material loaded
        // from a source resolves to its diffuse path, normalized for archive lookup.
        const string materialKey = @"materials\test\thing.bgsm";
        var source = new FakeMaterialSource(materialKey,
            BuildBgsm(22, 60, "SetDressing/Test/thing_d.dds", "Shared/flat_n.dds"));

        var diffuse = MaterialTexturePathResolver.ResolveDiffuseTexturePath(materialKey, [source]);

        Assert.Equal(@"textures\setdressing\test\thing_d.dds", diffuse);
    }

    [Fact]
    public void MaterialTexturePathResolver_ReturnsNull_WhenMaterialAbsent()
    {
        var source = new FakeMaterialSource(@"materials\present.bgsm", [1, 2, 3]);

        Assert.Null(MaterialTexturePathResolver.ResolveDiffuseTexturePath(@"materials\missing.bgsm", [source]));
    }

    [Theory]
    [InlineData("BSShaderPPLightingProperty")]
    [InlineData("Lighting30ShaderProperty")]
    public void ResolveTextureSetPathsAndShaderFlags_FromClassicLightingProperty(string propertyType)
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
            (propertyType, 0, 38),
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
        Assert.Equal(propertyType, metadata.PropertyType);
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
    public void ReadShaderMetadata_FromClassicParallaxPropertyReadsSlot3AndPomTailSeparately()
    {
        const string diffusePath = @"textures\landscape\RubblePile05.dds";
        const string normalPath = @"textures\landscape\RubblePile05_n.dds";
        const string heightPath = @"textures\landscape\RubblePile05_p.dds";
        const int textureSetOffset = 64;
        var textureSetSize = 4 +
                             4 + diffusePath.Length +
                             4 + normalPath.Length +
                             4 +
                             4 + heightPath.Length;
        var data = new byte[textureSetOffset + textureSetSize];

        WriteNiObjectNetHeader(data, 0);
        WriteUInt16(data, 12, 0);
        WriteUInt32(data, 14, 1); // shader type
        WriteUInt32(data, 18, NifClassicParallaxPolicy.ParallaxFlag);
        WriteUInt32(data, 22, 0);
        WriteFloat(data, 26, 1f); // environment-map scale
        WriteUInt32(data, 30, 0); // texture clamp
        WriteInt32(data, 34, 1); // texture-set ref
        WriteFloat(data, 38, 0f); // refraction strength
        WriteInt32(data, 42, 0); // refraction fire period
        WriteFloat(data, 46, 4f); // POM max passes
        WriteFloat(data, 50, 1f); // POM scale

        var pos = textureSetOffset;
        WriteUInt32(data, pos, 4);
        pos += 4;
        WriteSizedString(data, ref pos, diffusePath);
        WriteSizedString(data, ref pos, normalPath);
        WriteSizedString(data, ref pos, string.Empty);
        WriteSizedString(data, ref pos, heightPath);

        var nif = CreateNifInfo(
            ("BSShaderPPLightingProperty", 0, 54),
            ("BSShaderTextureSet", textureSetOffset, textureSetSize));
        var metadata = NifTextureResolver.ReadShaderMetadata(data, nif, [0]);

        Assert.NotNull(metadata);
        Assert.Equal(NifClassicParallaxPolicy.ParallaxFlag, metadata.ShaderFlags);
        Assert.Equal(heightPath, metadata.HeightMapPath);
        Assert.Equal(4f, metadata.ParallaxMaxPasses);
        Assert.Equal(1f, metadata.ParallaxScale);

        nif.BsVersion = 24;
        var preTailMetadata = NifTextureResolver.ReadShaderMetadata(data, nif, [0]);
        Assert.NotNull(preTailMetadata);
        Assert.Null(preTailMetadata.ParallaxMaxPasses);
        Assert.Null(preTailMetadata.ParallaxScale);
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
    public void ReadShaderMetadata_FromTallGrassProperty_ReadsInlineTextureWithoutClampField()
    {
        // FNV's TallGrassShaderProperty inherits BSShaderProperty directly. Its File Name begins
        // immediately after the common 16-byte shader payload; the four-byte texture-clamp field
        // present in BSShaderNoLightingProperty does not exist here.
        const string diffusePath = @"textures\landscape\grass\NVGreenGrass.dds";

        var data = new byte[96];
        WriteNiObjectNetHeader(data, 0);
        WriteUInt16(data, 12, 0);
        WriteUInt32(data, 14, 7);
        WriteUInt32(data, 18, 0x03234567u);
        WriteUInt32(data, 22, 0x89ABCDEFu);
        WriteFloat(data, 26, 0.5f);

        var pos = 30;
        WriteSizedString(data, ref pos, diffusePath);

        var nif = CreateNifInfo(("TallGrassShaderProperty", 0, pos));
        var metadata = NifTextureResolver.ReadShaderMetadata(data, nif, [0]);

        Assert.NotNull(metadata);
        Assert.Equal("TallGrassShaderProperty", metadata.PropertyType);
        Assert.True(metadata.HasRemappableTextures);
        Assert.Equal(7u, metadata.ShaderType);
        Assert.Equal(0x03234567u, metadata.ShaderFlags);
        Assert.Equal(0x89ABCDEFu, metadata.ShaderFlags2);
        Assert.Equal(0.5f, metadata.EnvMapScale);
        Assert.Equal(diffusePath, metadata.DiffusePath);
        Assert.Equal(8, metadata.TextureSlots.Count);
        for (var slot = 1; slot < 8; slot++)
        {
            Assert.Null(metadata.GetTextureSlot(slot));
        }

        Assert.Equal(diffusePath, NifTextureResolver.ResolveDiffusePath(data, nif, [0]));
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
        const uint shaderFlags1 = 0xEC000000; // Z-test + Soft_Effect + External_Emittance + Dynamic_Decal + Decal
        const uint shaderFlags2 = 0x40000029; // Effect_Lighting + Vertex_Colors + No_Fade + Z-write
        WriteUInt32(data, 12, shaderFlags1);
        WriteUInt32(data, 16, shaderFlags2);
        WriteFloat(data, 20, 0.125f); // UV Offset.u
        WriteFloat(data, 24, -0.25f); // UV Offset.v
        WriteFloat(data, 28, 2f); // UV Scale.u
        WriteFloat(data, 32, 0.5f); // UV Scale.v
        var pos = 36;
        WriteSizedString(data, ref pos, sourceTexture);

        WriteUInt32(data, pos, 0x0000FF03); // clamp=3, LightingInfluence=255
        pos += 4;
        WriteFloat(data, pos, 1f); // Falloff Start Angle
        WriteFloat(data, pos + 4, 1f); // Falloff Stop Angle
        WriteFloat(data, pos + 8, 0f); // Falloff Start Opacity
        WriteFloat(data, pos + 12, 0f); // Falloff Stop Opacity
        pos += 16;
        WriteFloat(data, pos, 0.25f); // Base Color R
        WriteFloat(data, pos + 4, 0.5f); // Base Color G
        WriteFloat(data, pos + 8, 0.75f); // Base Color B
        WriteFloat(data, pos + 12, 0.25f); // Base Color A (distinct authored opacity)
        WriteFloat(data, pos + 16, 2f); // Base Color Scale
        WriteFloat(data, pos + 20, 128f); // authored Soft Falloff Depth
        pos += 24;

        var nif = CreateNifInfo(("BSEffectShaderProperty", 0, pos));
        nif.BsVersion = 83;
        var metadata = NifTextureResolver.ReadShaderMetadata(data, nif, [0]);

        Assert.NotNull(metadata);
        Assert.Equal("BSEffectShaderProperty", metadata.PropertyType);
        Assert.Equal(sourceTexture, metadata.DiffusePath);
        Assert.Equal(shaderFlags1, metadata.ShaderFlags);
        Assert.Equal(shaderFlags2, metadata.ShaderFlags2);
        Assert.Equal(new Vector2(0.125f, -0.25f), metadata.UvOffset);
        Assert.Equal(new Vector2(2f, 0.5f), metadata.UvScale);
        Assert.Equal(1f, metadata.EffectLightingInfluence);
        Assert.Equal(2f, metadata.EffectBaseColorScale);
        Assert.Equal((0.25f, 0.5f, 0.75f, 0.25f), metadata.EffectBaseColor);
        Assert.Equal((1f, 1f, 0f, 0f), metadata.EffectFalloff);
        Assert.Equal(128f, metadata.SoftEffectFalloffDepth);
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
        BucketBTestGuard.SkipUnlessEnabled();

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
            BinaryVersion = 0x14020007, // 20.2.0.7 (FNV, bsVersion 34) — non-legacy NiTexturingProperty layout
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

    /// <summary>
    ///     Minimal FO4/FO76 BGSM with the gradient flag zeroed (→ the non-gradient texture-path map,
    ///     whose first two slots are diffuse then normal), then the supplied paths. Mirrors
    ///     <c>BgsmMaterialTests.BuildBgsm</c>.
    /// </summary>
    private static byte[] BuildBgsm(byte version, int headerLength, params string[] paths)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("BGSM"));
        bw.Write((uint)version);
        bw.Write(new byte[headerLength - 8]);
        foreach (var path in paths)
        {
            var bytes = Encoding.ASCII.GetBytes(path);
            bw.Write((uint)(bytes.Length + 1));
            bw.Write(bytes);
            bw.Write((byte)0);
        }

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Single-entry texture source that returns fixed bytes for one path (case-insensitive).</summary>
    private sealed class FakeMaterialSource(string key, byte[] bytes) : INifTextureSource
    {
        public DecodedTexture? TryLoad(string path)
        {
            return null;
        }

        public byte[]? TryLoadRaw(string path)
        {
            return string.Equals(path, key, StringComparison.OrdinalIgnoreCase) ? bytes : null;
        }

        public bool Exists(string path)
        {
            return string.Equals(path, key, StringComparison.OrdinalIgnoreCase);
        }

        public bool TryGetAssetMetadata(string path, out NifTextureSourceAssetMetadata metadata)
        {
            metadata = default;
            return false;
        }

        public void Dispose()
        {
        }
    }
}
