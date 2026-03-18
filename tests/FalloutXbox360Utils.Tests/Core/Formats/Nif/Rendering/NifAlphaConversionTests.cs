using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifAlphaConversionTests
{
    [Fact]
    public void ConvertedVault22Grass_PreservesExplicitAlphaTest()
    {
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
}