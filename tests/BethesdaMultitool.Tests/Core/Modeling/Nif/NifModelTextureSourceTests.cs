using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifMaterialFixtures;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Slice 6 texture resolution through the real reader: every texture-set path becomes one image named by its
///     authored string, resolved through the read context's companion resolver (BMT's over an in-memory game file system,
///     or Shared's default basename resolver), with DDS descriptors from inspection alone, SHA-256 deduplication, missing
///     and ambiguous images, and the Xbox specular companion rule. Each test names its control.
/// </summary>
public class NifModelTextureSourceTests
{
    private static readonly byte[][] Dxt1Blocks =
    [
        SyntheticDds.OpaqueDxt1Block, SyntheticDds.TransparentDxt1Block, SyntheticDds.ThreeColorOpaqueDxt1Block
    ];

    /// <summary>
    ///     A DXT1 descriptor's channels follow Shared's BC1 selector scan without decoding: a visible transparent texel
    ///     makes it RGBA. Control: the same three-color endpoints with no texel selecting 3 stay RGB. The original bytes are
    ///     kept exactly, and the image is named by the authored string.
    /// </summary>
    [Theory]
    [InlineData(0, SceneTextureChannels.Rgb)]
    [InlineData(1, SceneTextureChannels.Rgba)]
    [InlineData(2, SceneTextureChannels.Rgb)]
    public void Dxt1Descriptor_ChannelsFollowTheSelectorScan(int block, SceneTextureChannels expected)
    {
        var dds = SyntheticDds.Dxt1Single(Dxt1Blocks[block]);
        var document = ReadTextures(PerPixel(0, @"Textures\T\A.dds"), (@"textures\t\a.dds", dds)).Document;
        var image = Assert.Single(document.Images);
        var source = image.Source!;

        Assert.Equal(@"Textures\T\A.dds", image.Name);
        Assert.Equal("dds", source.Container);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(dds)), source.Original!.Sha256);
        Assert.Equal(expected, source.Descriptor.Channels);
        Assert.Equal("FourCC", source.Descriptor.PixelFormatNamespace);
        Assert.Equal("DXT1", source.Descriptor.PixelFormatCode);
        Assert.Equal(4d, source.Descriptor.BitsPerPixel);
        Assert.Equal(SceneTextureMipPresence.Authored, source.Descriptor.MipLevels[0].Presence);
        Assert.Null(source.StandardPayload);
        Assert.Empty(source.DisplayTransforms);
        Assert.Equal("textures/t/a.dds", source.Location!.AssetReference!.Path);
        Assert.Equal(SceneImageOrigin.SourceReference, source.Origin);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>A BC5 DDS declares Z reconstruction (unsigned input, positive hemisphere, Assumed); control: DXT1 does not.</summary>
    [Theory]
    [InlineData("ATI2", true)]
    [InlineData("DXT1", false)]
    public void Bc5_DeclaresNormalZReconstruction(string fourCc, bool expected)
    {
        var dds = SyntheticDds.FourCc(fourCc, 4, 4, new byte[fourCc == "ATI2" ? 16 : 8]);
        var source = Assert.Single(ReadTextures(PerPixel(0, @"textures\n.dds"), (@"textures\n.dds", dds))
            .Document.Images).Source!;

        if (expected)
        {
            var rule = Assert.IsType<SceneNormalZReconstruction>(Assert.Single(source.DisplayTransforms));
            Assert.False(rule.InputSigned);
            Assert.True(rule.PositiveZ);
            Assert.Equal(SceneValueProvenance.Assumed, rule.Provenance);
            Assert.Equal(SceneTextureChannels.Rg, source.Descriptor.Channels);
        }
        else
        {
            Assert.Empty(source.DisplayTransforms);
        }
    }

    /// <summary>
    ///     A cube map, which Shared's inspection refuses, gets its descriptor from BMT's header path (IsCube, mips
    ///     Unknown) and keeps its bytes. Control: the same data declared as a plain surface is not a cube.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CubeMap_UsesTheHeaderPath(bool cube)
    {
        var dds = SyntheticDds.FourCc("DXT1", 4, 4, SyntheticDds.Repeat(SyntheticDds.OpaqueDxt1Block, 6), cube: cube);
        var document = ReadTextures(PerPixel(0, "", "", "", "", @"textures\env.dds"), (@"textures\env.dds", dds))
            .Document;
        var source = Assert.Single(document.Images).Source!;

        Assert.Equal(cube, source.Descriptor.IsCube);
        Assert.NotNull(source.Original);
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == NifModelTextureSource.DdsHeaderDiagnostic);
        if (cube)
        {
            Assert.Equal(SceneTextureMipPresence.Unknown, source.Descriptor.MipLevels[0].Presence);
        }

        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A typed BC7 DDS (DXGI 98) is admitted by Shared's inspection since its BC7 adoption: no header-fallback
    ///     diagnostic, a DXGI descriptor with RGBA channels at 8 bits per pixel, and lower-mip presence measured from
    ///     the stored bytes (a BC7 block is 16 bytes). Control: the same declared chain truncated after level 1 reads
    ///     Missing for levels 2 and 3, which a reader that did not know BC7's block size would call Unknown.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bc7Descriptor_MipPresenceFollowsTheStoredBytes(bool complete)
    {
        // 8x8 with 4 declared mips: level sizes 64, 16, 16, 16; the truncated variant stores only levels 0 and 1.
        var data = new byte[complete ? 112 : 80];
        var dds = SyntheticDds.Dx10(98, 8, 8, data, 4);
        var document = ReadTextures(PerPixel(0, @"textures\t\bc7.dds"), (@"textures\t\bc7.dds", dds)).Document;
        var source = Assert.Single(document.Images).Source!;

        Assert.DoesNotContain(document.Diagnostics, d => d.Code == NifModelTextureSource.DdsHeaderDiagnostic);
        Assert.Equal("BC7", source.Descriptor.Compression);
        Assert.Equal("DXGI", source.Descriptor.PixelFormatNamespace);
        Assert.Equal("98", source.Descriptor.PixelFormatCode);
        Assert.Equal(8d, source.Descriptor.BitsPerPixel);
        Assert.Equal(SceneTextureChannels.Rgba, source.Descriptor.Channels);
        Assert.Empty(source.DisplayTransforms);
        Assert.Equal(4, source.Descriptor.MipLevels.Count);
        Assert.Equal(SceneTextureMipPresence.Authored, source.Descriptor.MipLevels[0].Presence);
        Assert.Equal(SceneTextureMipPresence.Authored, source.Descriptor.MipLevels[1].Presence);
        var lower = complete ? SceneTextureMipPresence.Authored : SceneTextureMipPresence.Missing;
        Assert.Equal(lower, source.Descriptor.MipLevels[2].Presence);
        Assert.Equal(lower, source.Descriptor.MipLevels[3].Presence);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     BC7_UNORM_SRGB (DXGI 99) declares sRGB with Authored provenance, straight from the format; control:
    ///     BC7_UNORM (98) leaves the color space Unknown.
    /// </summary>
    [Theory]
    [InlineData(98u, false)]
    [InlineData(99u, true)]
    public void Bc7ColorSpace_FollowsTheDxgiFormat(uint dxgiFormat, bool srgb)
    {
        var dds = SyntheticDds.Dx10(dxgiFormat, 4, 4, new byte[16]);
        var source = Assert.Single(ReadTextures(PerPixel(0, @"textures\t\a.dds"), (@"textures\t\a.dds", dds))
            .Document.Images).Source!;

        Assert.Equal(srgb ? SceneColorSpace.Srgb : SceneColorSpace.Unknown, source.Descriptor.ColorSpace);
        Assert.Equal(srgb ? SceneValueProvenance.Authored : SceneValueProvenance.Unknown,
            source.Descriptor.ColorSpaceProvenance);
    }

    /// <summary>
    ///     A DX10 format Shared's inspection refuses (BC6H_UF16, DXGI 95, and half-float RGBA, DXGI 10) keeps its
    ///     original bytes with BMT's header-only descriptor (mips Unknown, the format named) and the dds-header
    ///     diagnostic, so the row stays honest without a pixel path. Control: typed BC7 (98), the same shape through
    ///     the same reader, takes the inspected path with no diagnostic and an Authored level zero.
    /// </summary>
    [Theory]
    [InlineData(95u, 16, "BC6H", true)]
    [InlineData(10u, 128, null, true)]
    [InlineData(98u, 16, "BC7", false)]
    public void SharedRefusedFormats_UseTheHeaderPathWithADiagnostic(uint dxgiFormat, int dataBytes,
        string? compression, bool refused)
    {
        var dds = SyntheticDds.Dx10(dxgiFormat, 4, 4, new byte[dataBytes]);
        var document = ReadTextures(PerPixel(0, @"textures\t\a.dds"), (@"textures\t\a.dds", dds)).Document;
        var source = Assert.Single(document.Images).Source!;

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(dds)), source.Original!.Sha256);
        Assert.Equal("DXGI", source.Descriptor.PixelFormatNamespace);
        Assert.Equal(dxgiFormat.ToString(CultureInfo.InvariantCulture), source.Descriptor.PixelFormatCode);
        Assert.Equal(compression, source.Descriptor.Compression);
        Assert.Equal(refused,
            document.Diagnostics.Any(d => d.Code == NifModelTextureSource.DdsHeaderDiagnostic));
        Assert.Equal(refused ? SceneTextureMipPresence.Unknown : SceneTextureMipPresence.Authored,
            source.Descriptor.MipLevels[0].Presence);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A legacy numeric-FourCC float DDS (D3DFMT 113, the header BMT's own BA2 extraction writes for
    ///     R16G16B16A16_FLOAT) keeps its original bytes, names the code in hex under the FourCC namespace and carries
    ///     the dds-header diagnostic. Control in the same run: a DXT1 of the same extent takes the inspected path with
    ///     no diagnostic.
    /// </summary>
    [Fact]
    public void LegacyFloatFourCc_UsesTheHeaderPathWithADiagnostic()
    {
        var half = SyntheticDds.FourCc("q", 4, 4, new byte[128]); // "q" = 0x71 = D3DFMT_A16B16G16R16F, NUL-padded
        var control = SyntheticDds.Dxt1Single(SyntheticDds.OpaqueDxt1Block);
        var document = ReadTextures(PerPixel(0, @"textures\t\half.dds", "", @"textures\t\plain.dds"),
            (@"textures\t\half.dds", half), (@"textures\t\plain.dds", control)).Document;
        var floatSource = document.Images[0].Source!;
        var controlSource = document.Images[1].Source!;

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(half)), floatSource.Original!.Sha256);
        Assert.Equal("FourCC", floatSource.Descriptor.PixelFormatNamespace);
        Assert.Equal("0x00000071", floatSource.Descriptor.PixelFormatCode);
        Assert.Null(floatSource.Descriptor.Compression);
        Assert.Single(document.Diagnostics, d => d.Code == NifModelTextureSource.DdsHeaderDiagnostic);
        Assert.Equal("DXT1", controlSource.Descriptor.PixelFormatCode);
        Assert.Equal(SceneTextureMipPresence.Authored, controlSource.Descriptor.MipLevels[0].Presence);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A path that resolves nowhere is a missing image with a reason and a diagnostic; control: the same path present
    ///     is an original.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTexture_IsAMissingImageWithAReason(bool present)
    {
        var files = present
            ? new[] { (@"textures\a.dds", SyntheticDds.Dxt1Single(SyntheticDds.OpaqueDxt1Block)) }
            : [];
        var document = ReadTextures(PerPixel(0, @"textures\a.dds"), files).Document;
        var source = Assert.Single(document.Images).Source!;

        Assert.Equal(present, source.Original is not null);
        Assert.Equal(present, source.MissingReason is null);
        Assert.Equal(!present, document.Diagnostics.Any(d => d.Code == NifModelTextureSource.MissingDiagnostic));
        if (!present)
        {
            Assert.Contains(@"textures\a.dds", source.MissingReason, StringComparison.Ordinal);
        }

        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     Shared's default resolver refuses a path (ArgumentException), so the reader retries with the basename: two
    ///     same-named files and no sibling are ambiguous (a missing image). Control: a sibling of the model wins, and the
    ///     native row records the basename retry.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultResolver_RetriesTheBasename_AndReportsAmbiguity(bool sibling)
    {
        var dds = SyntheticDds.Dxt1Single(SyntheticDds.OpaqueDxt1Block);
        var source = new InMemoryAssetSource(SourceId);
        source.Add("meshes/x/a.dds", dds);
        source.Add("meshes/y/a.dds", [.. dds, 0]);
        if (sibling)
        {
            source.Add("meshes/test/a.dds", dds);
        }

        var document = ReadWith(PerPixel(0, @"textures\whatever\a.dds"), null, source: source).Document;
        var image = Assert.Single(document.Images).Source!;
        var payload = JsonNode.Parse(Rows(document, NifModelTextureSource.TextureKind)[0].PayloadJson)!;

        if (sibling)
        {
            Assert.NotNull(image.Original);
            Assert.Equal("meshes/test/a.dds", image.Location!.AssetReference!.Path);
            Assert.Equal("a.dds", (string)payload["resolution"]!["basenameRetry"]!);
        }
        else
        {
            Assert.Null(image.Original);
            Assert.Contains("ambiguous across 2", image.MissingReason, StringComparison.Ordinal);
        }

        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     Two slots naming different files with identical bytes share one image (deduplicated by SHA-256), and two
    ///     spellings of one path share it too. Control: different bytes make two images.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdenticalContent_IsOneImage(bool identical)
    {
        var first = SyntheticDds.Dxt1Single(SyntheticDds.OpaqueDxt1Block);
        var second = identical ? first : SyntheticDds.Dxt1Single(SyntheticDds.TransparentDxt1Block);
        var document = ReadTextures(PerPixel(0, @"textures\a.dds", "", @"textures\b.dds"),
            (@"textures\a.dds", first), (@"textures\b.dds", second)).Document;
        var material = MaterialOf(document);

        Assert.Equal(identical ? 1 : 2, document.Images.Count);
        Assert.Equal(identical, material.Layers[0].Binding.ImageIndex == material.Layers[1].Binding.ImageIndex);

        var spelled = ReadTextures(PerPixel(0, @"textures\a.dds", "", "Textures/A.DDS"), (@"textures\a.dds", first))
            .Document;
        Assert.Single(spelled.Images);
    }

    /// <summary>
    ///     An Xbox normal map (a <c>_n</c> file that resolved to a DDX) with SF1 Specular set gets its <c>_s</c> companion
    ///     as its own image (origin PlatformCompanion) bound to a Specular layer reading red into alpha; BC5 and BC4 are
    ///     never merged. The <c>.dds</c> spelling resolved to the <c>.ddx</c> (the resolver's fallback). Control: without
    ///     the companion the BC5 normal gets no specular layer and a diagnostic.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void XboxSpecularCompanion_IsItsOwnImage(bool companionPresent)
    {
        var normal = SyntheticDdxFiles.Build("3XDR", 64, 64, SyntheticDdxFiles.Ati2,
            SyntheticDdxFiles.IndexStampedBlocks(256, 16));
        var specular = SyntheticDdxFiles.Build("3XDR", 64, 64, SyntheticDdxFiles.Ati1,
            SyntheticDdxFiles.IndexStampedBlocks(256, 8));
        var files = companionPresent
            ? new[] { (@"textures\t\wall_n.ddx", normal), (@"textures\t\wall_s.ddx", specular) }
            : [(@"textures\t\wall_n.ddx", normal)];
        var document = ReadTextures(PerPixel(1, "", @"textures\t\wall_n.dds"), files).Document;
        var material = MaterialOf(document);

        var normalLayer = Assert.Single(material.Layers, l => l.Role == SceneTextureLayerRole.Normal);
        Assert.Equal("ddx", ImageOf(document, normalLayer).Source!.Container);
        Assert.EndsWith("wall_n.ddx", ImageOf(document, normalLayer).Source!.Location!.AssetReference!.Path,
            StringComparison.Ordinal);
        if (companionPresent)
        {
            var specularLayer = Assert.Single(material.Layers, l => l.Role == SceneTextureLayerRole.Specular);
            var companion = ImageOf(document, specularLayer);
            Assert.NotEqual(normalLayer.Binding.ImageIndex, specularLayer.Binding.ImageIndex);
            Assert.Equal(SceneImageOrigin.PlatformCompanion, companion.Source!.Origin);
            Assert.Equal("ddx", companion.Source.Container);
            Assert.Equal(SceneTextureChannel.Red, specularLayer.Swizzle.Alpha);
            Assert.Equal(2, document.Images.Count);
        }
        else
        {
            Assert.DoesNotContain(material.Layers, l => l.Role == SceneTextureLayerRole.Specular);
            Assert.Single(document.Images);
            Assert.Contains(document.Diagnostics,
                d => d.Code == NifModelMaterialReader.SpecularCompanionDiagnostic);
        }

        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     PC never takes the companion path: a <c>_n.dds</c> normal supplies specular from its own alpha even when a
    ///     <c>_s.dds</c> sits beside it, which is never loaded.
    /// </summary>
    [Fact]
    public void PcNormalMap_UsesItsAlpha_NotAnSCompanion()
    {
        var normal = SyntheticDds.FourCc("DXT5", 4, 4, new byte[16]);
        var neighbor = SyntheticDds.FourCc("ATI1", 4, 4, new byte[8]);
        var document = ReadTextures(PerPixel(1, "", @"textures\t\wall_n.dds"),
            (@"textures\t\wall_n.dds", normal), (@"textures\t\wall_s.dds", neighbor)).Document;
        var material = MaterialOf(document);

        var normalLayer = Assert.Single(material.Layers, l => l.Role == SceneTextureLayerRole.Normal);
        var specularLayer = Assert.Single(material.Layers, l => l.Role == SceneTextureLayerRole.Specular);
        Assert.Equal(normalLayer.Binding, specularLayer.Binding);
        Assert.Equal(SceneTextureSwizzle.Identity, specularLayer.Swizzle);
        Assert.Single(document.Images);
    }

    /// <summary>A texture-set shader over the given paths (slot order), with SF1 as given.</summary>
    internal static byte[] PerPixel(uint shaderFlags1, params string[] paths)
    {
        return Shape(34, [3], b =>
        {
            b.AddBlock("BSShaderPPLightingProperty",
                w => NifTestBlockLayouts.PerPixelLightingProperty(w, 34, shaderFlags1, 1u, 1f, 3, 4));
            b.AddBlock("BSShaderTextureSet", w => NifTestBlockLayouts.TextureSet(w, paths));
        });
    }

    /// <summary>Reads a fixture whose textures come from an in-memory game file system through BMT's resolver.</summary>
    internal static ModelReadResult ReadTextures(byte[] nif, params (string Path, byte[] Bytes)[] files)
    {
        return ReadTextures(nif, ModelReadPurpose.Conversion, null, files);
    }

    /// <summary>The same, with an explicit purpose and texture codec.</summary>
    internal static ModelReadResult ReadTextures(byte[] nif, ModelReadPurpose purpose, INifTextureCodec? codec,
        params (string Path, byte[] Bytes)[] files)
    {
        var companions = Companions(files);
        try
        {
            return ReadWith(nif, companions.ResolveAsync, purpose, codec);
        }
        finally
        {
            companions.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
