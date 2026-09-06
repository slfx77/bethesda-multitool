using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

public sealed class NifOblivionHairSourceReaderTests
{
    [Theory]
    [InlineData("Hair", false)]
    [InlineData("hAiR", true)]
    public void LegacySource_AdmitsOwnedStaticLightsAndRawGreenAlphaOne(string material, bool reverse)
    {
        var fixture = new NifOblivionHairTestData(material, reverse);
        var before = (byte[])fixture.Data.Clone();
        Assert.True(Read(fixture));
        Assert.Equal(before, fixture.Data);
        Assert.False(NifOblivionBodySkinSourceReader.IsEligible(fixture.Data, fixture.Info, 2));
        Assert.Null(NifOblivionOrdinarySourceReader.ReadDiffusePath(fixture.Data, fixture.Info, 2));
    }

    [Theory]
    [InlineData("green-0", 0.5f)]
    [InlineData("green-2", 1.001f)]
    [InlineData("alpha-0", 1.001f)]
    [InlineData("alpha-2", 0f)]
    [InlineData("green-1", float.NaN)]
    [InlineData("red-0", float.PositiveInfinity)]
    [InlineData("uv", float.NaN)]
    [InlineData("ambient", 0.9f)]
    [InlineData("diffuse", 0.9f)]
    [InlineData("emissive", 0.1f)]
    [InlineData("material-alpha", 0.5f)]
    [InlineData("light-1-dimmer", 2f)]
    public void LegacySource_RejectsUnprovenFloatStreamsBeforeQuantization(string field, float value)
    {
        var fixture = new NifOblivionHairTestData();
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(fixture.Offsets[field]), value);
        Assert.False(Read(fixture));
    }

    [Fact]
    public void Extraction_ClampedWhiteBytesDoNotCreateRawColorProvenance()
    {
        var fixture = new NifOblivionHairTestData();
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(fixture.Offsets["green-0"]), 1.001f);
        var part = Assert.Single(NifExportExtractor.Extract(fixture.Data, fixture.Info).MeshParts).Submesh;
        Assert.Equal(255, part.VertexColors![1]);
        Assert.False(part.HasAuthoredOblivionHairLayerInputs);
    }

    [Theory]
    [InlineData("root-extras", 0)]
    [InlineData("root-controller", 1)]
    [InlineData("root-properties", 1)]
    [InlineData("root-effects", 0)]
    [InlineData("root-first-effect", 2)]
    [InlineData("shape-controller", 1)]
    [InlineData("properties", 3)]
    [InlineData("collision", 0)]
    [InlineData("skin", 0)]
    [InlineData("apply", 3)]
    [InlineData("clamp", 0)]
    [InlineData("filter", 0)]
    [InlineData("shader-textures", 1)]
    [InlineData("vertex-mode", 0)]
    [InlineData("light-0-controller", 0)]
    [InlineData("light-1-affected", 1)]
    public void LegacySource_RejectsOverridesAndUnknownInheritance(string field, int value)
    {
        var fixture = new NifOblivionHairTestData();
        BinaryPrimitives.WriteInt32LittleEndian(fixture.Data.AsSpan(fixture.Offsets[field]), value);
        Assert.False(Read(fixture));
    }

    [Theory]
    [InlineData("shader")]
    [InlineData("texture-transform")]
    [InlineData("dark")]
    [InlineData("detail")]
    [InlineData("gloss-map")]
    [InlineData("glow")]
    [InlineData("bump")]
    [InlineData("decal")]
    [InlineData("threshold")]
    public void LegacySource_RejectsAdditionalShaderAndCoverageState(string field)
    {
        var fixture = new NifOblivionHairTestData();
        fixture.Data[fixture.Offsets[field]] = 1;
        Assert.False(Read(fixture));
    }

    [Theory]
    [InlineData("ps2-l", 1)]
    [InlineData("ps2-k", 0)]
    [InlineData("alpha-flags", 0)]
    [InlineData("index", 3)]
    public void LegacySource_RequiresFramedLegacyShortFields(string field, short value)
    {
        var fixture = new NifOblivionHairTestData();
        BinaryPrimitives.WriteInt16LittleEndian(fixture.Data.AsSpan(fixture.Offsets[field]), value);
        Assert.False(Read(fixture));
    }

    [Fact]
    public void LegacySource_RequiresExactVersionAndCompleteOwnedBlocks()
    {
        var fixture = new NifOblivionHairTestData();
        fixture.Info.IsBigEndian = true;
        Assert.False(Read(fixture));
        fixture.Info.IsBigEndian = false;
        fixture.Info.UserVersion = 11;
        Assert.False(Read(fixture));
        fixture.Info.UserVersion = 10;
        fixture.Info.BsVersion = 11;
        Assert.False(Read(fixture));
        fixture.Info.BsVersion = 9;
        fixture.Info.BinaryVersion = 0x14000004;
        Assert.False(Read(fixture));
        fixture.Info.BinaryVersion = 0x0A020000;
        fixture.Info.HasInlineStrings = false;
        Assert.False(Read(fixture));
        for (var index = 0; index < 11; index++)
        {
            var truncated = new NifOblivionHairTestData();
            truncated.Info.Blocks[index].Size--;
            Assert.False(Read(truncated));
        }
    }

    [Theory]
    [InlineData("HairVariant")]
    [InlineData("skin")]
    [InlineData("right eye")]
    [InlineData("ordinary")]
    public void LegacySource_OnlyProvesExactHairMaterial(string material)
    {
        Assert.False(Read(new NifOblivionHairTestData(material)));
    }

    [Fact]
    public void BothExtractionRoutesAndClone_KeepGeometryColorProvenance()
    {
        var fixture = new NifOblivionHairTestData();
        var exported = Assert.Single(NifExportExtractor.Extract(fixture.Data, fixture.Info).MeshParts).Submesh;
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(fixture.Data, fixture.Info, bindPoseOnly: true));
        var rendered = Assert.Single(model.Submeshes);
        Assert.True(exported.HasAuthoredOblivionHairLayerInputs);
        Assert.True(rendered.HasAuthoredOblivionHairLayerInputs);
        Assert.False(rendered.UsesClassicHairMaterial); // Source proof alone never selects an actor material.
        Assert.True(RenderableSubmeshCloner.DeepClone(exported).HasAuthoredOblivionHairLayerInputs);
        Assert.True(RenderableSubmeshCloner.CloneGeometryWithRenderState(exported, rendered).HasAuthoredOblivionHairLayerInputs);
        exported.HasAuthoredOblivionHairLayerInputs = false;
        Assert.False(RenderableSubmeshCloner.CloneGeometryWithRenderState(exported, rendered).HasAuthoredOblivionHairLayerInputs);
        exported.HasAuthoredOblivionHairLayerInputs = true;
        rendered.HasAuthoredOblivionHairLayerInputs = false;
        Assert.False(RenderableSubmeshCloner.CloneGeometryWithRenderState(exported, rendered).HasAuthoredOblivionHairLayerInputs);
    }

    private static bool Read(NifOblivionHairTestData fixture) =>
        NifOblivionHairSourceReader.IsEligible(fixture.Data, fixture.Info, 2);
}
