using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     FO3/FNV heat-haze planes (BSShaderPPLighting/NoLighting with Refraction 0x8000 /
///     Fire_Refraction 0x10000) are screen-space distortion geometry — drawn opaque they occlude the
///     real effect behind them. TrapGasFire01's "RefractGas" dome rendered as a white sphere hiding
///     the additive gas billboard until the skip (previously BSLightingShaderProperty-only) covered
///     the FNV property types too.
/// </summary>
public sealed class NifRefractionSkipTests
{
    [Fact]
    public void TrapGasFire01_FnvRefractionDome_SkippedButGasBillboardKept()
    {
        var nifPath = SampleFileFixture.FindSamplePath(
            @"Sample\Unpacked_Builds\PC_Final_Unpacked\Data\meshes\traps\trapgasfire01.nif");

        Assert.SkipWhen(nifPath is null, "FNV TrapGasFire01 NIF not available");

        var data = File.ReadAllBytes(nifPath!);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));

        using var textureResolver = new NifTextureResolver();
        var model = NifGeometryExtractor.Extract(data, nif, textureResolver);

        Assert.NotNull(model);
        Assert.DoesNotContain(model.Submeshes, s => s.ShapeName?.Contains("RefractGas") == true);
        // The visible effect — the additive scrolling gas billboard — must still extract.
        Assert.Contains(model.Submeshes, s => s.ShapeName?.Contains("Plane01") == true && s.HasAlphaBlend);
    }

    [Fact]
    public void NvStripLightPollution_ExternalEmittance_IgnoresMaterialEmissiveMult()
    {
        // BSShaderFlags bit 29 (External_Emittance): the engine sources emittance from the REFR's
        // XEMI link and ignores the material's Emissive Color × Mult — this NIF authors (1,1,1)×21
        // under the flag, which blew the Strip glow ×21 into HDR when read as a glow tint.
        var nifPath = SampleFileFixture.FindSamplePath(
            @"Sample\Unpacked_Builds\PC_Final_Unpacked\Data\meshes\architecture\strip\nvstriplightpollution.nif");

        Assert.SkipWhen(nifPath is null, "FNV NVStripLightPollution NIF not available");

        var data = File.ReadAllBytes(nifPath!);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));

        using var textureResolver = new NifTextureResolver();
        var model = NifGeometryExtractor.Extract(data, nif, textureResolver);

        Assert.NotNull(model);
        var glow = Assert.Single(model.Submeshes, s => s.IsEmissive);
        Assert.Null(glow.EmissiveColor);
        Assert.Null(glow.AnimatedEmissiveColor);
    }
}
