using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

[Collection(SequentialIntegrationGroup.Name)]
public sealed class FnvLighting30EmissionRetailTests
{
    private const string MeshesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";

    private const string ProspectorNeonPath =
        @"meshes\architecture\goodsprings\NV_ProspectorSaloon-Neon_Lights.NIF";

    private const string ProspectorGlowPath =
        @"textures\architecture\goodsprings\NV_ProspectorSaloon-Neon_g.dds";

    public FnvLighting30EmissionRetailTests()
    {
        BucketBTestGuard.SkipUnlessEnabled();
    }

    [Fact]
    public void ProspectorNeon_SeparatesLighting30GlowFromNoLightingControls()
    {
        using var archives = OpenRetailMeshes();
        Assert.True(archives.TryExtractFile(ProspectorNeonPath, out var data, out _),
            $"Retail NIF missing: {ProspectorNeonPath}");
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data, nif,
            skipSkinning: true,
            collectBillboards: true,
            dropBoneAttachedShapes: true,
            treatRootsAsIdentity: true));

        var lighting30 = Assert.Single(model.Submeshes,
            static submesh => submesh.Lighting30GlowMapTexturePath is not null);
        Assert.Equal("BSShaderPPLightingProperty", lighting30.ShaderMetadata?.PropertyType);
        Assert.Equal(ProspectorGlowPath, lighting30.Lighting30GlowMapTexturePath);
        Assert.Equal((1f, 1f, 1f), lighting30.Lighting30EmissionColor);
        Assert.Equal(1f, lighting30.Lighting30EmissionMultiplier);
        Assert.True(lighting30.IsLighting30);
        Assert.False(lighting30.IsEmissive);

        var noLighting = model.Submeshes
            .Where(static submesh =>
                submesh.ShaderMetadata?.PropertyType == "BSShaderNoLightingProperty")
            .ToArray();
        Assert.NotEmpty(noLighting);
        Assert.All(noLighting, static submesh =>
        {
            Assert.True(submesh.IsEmissive);
            Assert.False(submesh.IsLighting30);
            Assert.Null(submesh.Lighting30GlowMapTexturePath);
            Assert.Null(submesh.Lighting30EmissionColor);
        });
    }

    [Fact]
    public void ProspectorNeon_ExplicitLighting30AndExternalEmittanceFlowThroughReaderAndExtractor()
    {
        using var archives = OpenRetailMeshes();
        Assert.True(archives.TryExtractFile(ProspectorNeonPath, out var data, out _),
            $"Retail NIF missing: {ProspectorNeonPath}");
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));

        // The exact retail glow property is block 92. Lighting30ShaderProperty inherits the PP
        // property byte-for-byte, so relabeling this parsed block gives an integration fixture for
        // the explicit type without manufacturing metadata or changing its layout.
        var property = nif.Blocks[92];
        Assert.Equal("BSShaderPPLightingProperty", property.TypeName);
        property.TypeName = "Lighting30ShaderProperty";

        // Common classic shader data starts after NiObjectNET + NiShadeProperty flags: shader type
        // at +14, flags1 at +18. Turn on External_Emittance and ensure XEMI replaces only RGB.
        var flagsOffset = property.DataOffset + 18;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(flagsOffset, sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(flagsOffset, sizeof(uint)),
            flags | 0x20000000u);
        var externalColor = new Vector3(0.25f, 0.5f, 0.75f);

        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data, nif,
            skipSkinning: true,
            collectBillboards: true,
            dropBoneAttachedShapes: true,
            treatRootsAsIdentity: true,
            externalEmittanceColor: externalColor));

        var lighting30 = Assert.Single(model.Submeshes,
            static submesh => submesh.Lighting30GlowMapTexturePath is not null);
        Assert.Equal("Lighting30ShaderProperty", lighting30.ShaderMetadata?.PropertyType);
        Assert.Equal(ProspectorGlowPath, lighting30.Lighting30GlowMapTexturePath);
        Assert.Equal((externalColor.X, externalColor.Y, externalColor.Z),
            lighting30.Lighting30EmissionColor);
        Assert.Equal(1f, lighting30.Lighting30EmissionMultiplier);
        Assert.True(lighting30.IsLighting30);
        Assert.False(lighting30.IsEmissive);
    }

    private static MeshArchiveSet OpenRetailMeshes()
    {
        var bsaPath = SampleFileFixture.FindSamplePath(MeshesBsaRelative);
        Assert.SkipWhen(bsaPath is null, "FNV PC-final meshes BSA not available");
        return MeshArchiveSet.Open(bsaPath!, null, false, false);
    }
}