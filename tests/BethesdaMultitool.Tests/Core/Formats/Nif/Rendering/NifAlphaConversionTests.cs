using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifAlphaConversionTests
{
    [Fact]
    public void ConvertedVault22Grass_PreservesExplicitAlphaTest()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var xboxNifPath = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_360_final\meshes\landscape\plants\vault22\vault22grass.nif");

        Assert.SkipWhen(xboxNifPath is null, "Xbox vault22grass NIF not available");

        var xboxData = File.ReadAllBytes(xboxNifPath!);
        var converted = NifConverter.Convert(xboxData);

        Assert.True(converted.Success, converted.ErrorMessage);

        var convertedData = Assert.IsType<byte[]>(converted.OutputData);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(convertedData));

        using var textureResolver = new NifTextureResolver();
        var model = NifGeometryExtractor.Extract(convertedData, nif, textureResolver);

        Assert.NotNull(model);
        Assert.Contains(
            model.Submeshes,
            submesh =>
                submesh.HasAlphaTest &&
                !submesh.HasAlphaBlend &&
                submesh.AlphaTestThreshold == 80 &&
                submesh.AlphaTestFunction == 4);
    }

    // Synthetic sibling of the vault22grass fact above: same convert → parse → extract path,
    // same alpha-test-without-blend assertion, but against the hand-authored big-endian
    // fixture so the regression runs without retail assets (not Bucket-B-gated).
    [Fact]
    public void ConvertedSyntheticBigEndianNif_PreservesExplicitAlphaTest()
    {
        var xboxData = BigEndianNifBuilder.Build(alphaFlags: 0x12EC, alphaThreshold: 80);

        var converted = NifConverter.Convert(xboxData);

        Assert.True(converted.Success, converted.ErrorMessage);
        var convertedData = Assert.IsType<byte[]>(converted.OutputData);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(convertedData));

        // Byte-exact: flags 0x12EC (test on, blend off, function 4) and threshold 80 must
        // land little-endian at the same in-block offsets.
        var alphaBlock = nif.Blocks[BigEndianNifBuilder.NiAlphaPropertyBlockIndex];
        Assert.Equal(0x12EC, BitConverter.ToUInt16(
            convertedData, alphaBlock.DataOffset + BigEndianNifBuilder.AlphaFlagsOffsetInBlock));
        Assert.Equal(80, convertedData[alphaBlock.DataOffset + BigEndianNifBuilder.AlphaThresholdOffsetInBlock]);

        using var textureResolver = new NifTextureResolver();
        var model = NifGeometryExtractor.Extract(convertedData, nif, textureResolver);

        Assert.NotNull(model);
        Assert.Contains(
            model.Submeshes,
            submesh =>
                submesh.HasAlphaTest &&
                !submesh.HasAlphaBlend &&
                submesh.AlphaTestThreshold == 80 &&
                submesh.AlphaTestFunction == 4);
    }
}
