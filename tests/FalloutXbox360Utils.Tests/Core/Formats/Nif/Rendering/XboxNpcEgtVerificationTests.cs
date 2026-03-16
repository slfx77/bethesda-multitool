using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

[Collection(LoggerSerialTestGroup.Name)]
public sealed class XboxNpcEgtVerificationTests(SampleFileFixture samples)
{
    [Theory]
    [InlineData(0x00092BD2u, "Craig Boone", 2.5)]
    [InlineData(0x00112640u, "Dennis Crocker", 2.1)]
    [InlineData(0x0010C681u, "Jean-Baptiste Cutting", 3.0)]
    public void VerifyRepresentativeNpcFaceGenTextures_Xbox360UseNativeComparisonMode(
        uint formId,
        string displayName,
        double maxMae)
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var meshesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Meshes.bsa");
        var texturesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Textures.bsa");
        var textures2Bsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Textures2.bsa");

        Assert.SkipWhen(meshesBsa is null, "Xbox 360 final meshes BSA not available");
        Assert.SkipWhen(texturesBsa is null, "Xbox 360 final textures BSA not available");

        var esm = EsmFileLoader.Load(samples.Xbox360FinalEsm!, false);
        Assert.NotNull(esm);

        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var texturePaths = textures2Bsa == null
            ? [texturesBsa!]
            : new[] { texturesBsa!, textures2Bsa! };
        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        var appearance = resolver.ResolveHeadOnly(formId, pluginName);

        Assert.NotNull(appearance);

        var shippedTextures = NpcFaceGenTextureVerifier.DiscoverShippedFaceTextures(
            texturePaths,
            pluginName);
        Assert.True(shippedTextures.TryGetValue(formId, out var shippedTexture));
        Assert.NotNull(shippedTexture);

        using var meshArchives = NpcMeshArchiveSet.Open(meshesBsa!, null);
        using var textureResolver = new NifTextureResolver(texturePaths);

        var result = NpcFaceGenTextureVerifier.Verify(
            appearance!,
            shippedTexture!,
            meshArchives,
            textureResolver,
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase));

        Assert.True(result.Verified, result.FailureReason);
        Assert.Equal("native_egt", result.ComparisonMode);
        Assert.Equal(256, result.Width);
        Assert.Equal(256, result.Height);
        Assert.True(
            result.MeanAbsoluteRgbError < maxMae,
            $"Expected {displayName} EGT MAE < {maxMae:F1}, got {result.MeanAbsoluteRgbError:F4}");
    }
}
