using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

public sealed class NifOblivionBodySkinAmbientSourceTests
{
    [Theory]
    [InlineData(0.588f, 0.588f, 0.588f)]
    [InlineData(0f, 0.25f, 1f)]
    [InlineData(-0.5f, 2f, 0.75f)]
    public void Read_FiniteAmbientIsPreservedForTheStrictStaticScene(float red, float green, float blue)
    {
        var fixture = new NifOblivionBodySkinSceneTestData();
        SetAmbient(fixture, red, green, blue);
        var before = fixture.Data.ToArray();
        var context = Assert.IsType<NifOblivionBodySkinSourceReader>(
            NifOblivionBodySkinSourceReader.Create(fixture.Data, fixture.Info));

        Assert.Equal((red, green, blue), context.ReadAmbientColor(0));
        Assert.Equal((red, green, blue), context.ReadAmbientColor(0));
        Assert.True(NifOblivionBodySkinSourceReader.IsEligible(fixture.Data, fixture.Info, 0));
        Assert.Equal(before, fixture.Data);
    }

    [Theory]
    [InlineData("root-extras", 1)]
    [InlineData("root-controller", 0)]
    [InlineData("root-properties", 1)]
    [InlineData("root-collision", 0)]
    [InlineData("root-effects", 1)]
    [InlineData("root-children", 100)]
    [InlineData("root-child", 2)]
    [InlineData("root-name", -1)]
    [InlineData("bone-extras", 1)]
    [InlineData("bone-controller", 0)]
    [InlineData("bone-properties", 1)]
    [InlineData("bone-collision", 0)]
    [InlineData("bone-effects", 1)]
    public void Read_InheritedOrDisconnectedStateRejectsOnlyTheNewAmbientCohort(string field, int value)
    {
        var fixture = new NifOblivionBodySkinSceneTestData();
        BinaryPrimitives.WriteInt32LittleEndian(fixture.Data.AsSpan(fixture.Offsets[field]), value);
        AssertWhiteAcceptedAndNonwhiteRejected(fixture);
    }

    [Theory]
    [InlineData("root-flags", 0x11)]
    [InlineData("bone-flags", 0x11)]
    [InlineData("root-flags", 0)]
    [InlineData("bone-flags", 0xFFFF)]
    public void Read_NodeFlagsOutsideTheProvenCohortDoNotBroadenAdmission(string field, int flags)
    {
        var fixture = new NifOblivionBodySkinSceneTestData();
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.Data.AsSpan(fixture.Offsets[field]), (ushort)flags);
        AssertWhiteAcceptedAndNonwhiteRejected(fixture);
    }

    [Theory]
    [InlineData("root-transform", float.NaN)]
    [InlineData("bone-transform", float.PositiveInfinity)]
    public void Read_NonfiniteNodeTransformsDoNotAdmitNonwhiteAmbient(string field, float value)
    {
        var fixture = new NifOblivionBodySkinSceneTestData();
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(fixture.Offsets[field]), value);
        AssertWhiteAcceptedAndNonwhiteRejected(fixture);
    }

    [Theory]
    [InlineData("NiBillboardNode")]
    [InlineData("NiDirectionalLight")]
    [InlineData("NiMaterialColorController")]
    [InlineData("BSShaderPPLightingProperty")]
    public void Read_UnknownSceneBlocksDoNotAcquireTheNewAdmission(string type)
    {
        var fixture = new NifOblivionBodySkinSceneTestData();
        fixture.Info.Blocks.Add(new BlockInfo { TypeName = type });
        AssertWhiteAcceptedAndNonwhiteRejected(fixture);
    }

    [Fact]
    public void Read_TextureEffectsStillRejectWhiteAndNonwhiteMaterials()
    {
        var fixture = new NifOblivionBodySkinSceneTestData();
        fixture.Info.Blocks.Add(new BlockInfo { TypeName = "NiTextureEffect" });
        Assert.False(Read(fixture));
        SetAmbient(fixture, 0.588f, 0.588f, 0.588f);
        Assert.False(Read(fixture));
    }

    [Fact]
    public void Read_AbsentOrTruncatedSceneDoesNotAdmitNonwhiteAmbient()
    {
        var noScene = new NifOblivionBodySkinTestData();
        BinaryPrimitives.WriteSingleLittleEndian(noScene.Data.AsSpan(noScene.Offsets["ambient"]), 0.588f);
        Assert.False(NifOblivionBodySkinSourceReader.IsEligible(noScene.Data, noScene.Info, 0));

        foreach (var node in new[] { 6, 7 })
        {
            var fixture = new NifOblivionBodySkinSceneTestData();
            fixture.Info.Blocks[node].Size--;
            AssertWhiteAcceptedAndNonwhiteRejected(fixture);
        }
    }

    [Theory]
    [InlineData(0, float.NaN)]
    [InlineData(1, float.PositiveInfinity)]
    [InlineData(2, float.NegativeInfinity)]
    public void Read_AnyNonfiniteAmbientChannelFailsClosed(int channel, float value)
    {
        var fixture = new NifOblivionBodySkinSceneTestData();
        BinaryPrimitives.WriteSingleLittleEndian(
            fixture.Data.AsSpan(fixture.Source.Offsets["ambient"] + channel * 4), value);
        Assert.False(Read(fixture));
    }

    [Theory]
    [InlineData("diffuse", 0.5f)]
    [InlineData("emissive", 0.1f)]
    [InlineData("alpha", 0.5f)]
    [InlineData("specular", float.NaN)]
    [InlineData("gloss", float.PositiveInfinity)]
    public void Read_NonwhiteAmbientNeverRelaxesOtherMaterialRestrictions(string field, float value)
    {
        var fixture = new NifOblivionBodySkinSceneTestData();
        SetAmbient(fixture, 0.588f, 0.588f, 0.588f);
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(fixture.Source.Offsets[field]), value);
        Assert.False(Read(fixture));
    }

    [Fact]
    public void ExtractAndClone_RetainTheAuthoredAmbientWithoutChangingDiffuseOrSelectingTheActorShader()
    {
        var fixture = new NifOblivionBodySkinSceneTestData();
        SetAmbient(fixture, 0.588f, 0.25f, 0.75f);
        var before = fixture.Data.ToArray();
        var cpu = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            fixture.Data, fixture.Info, bindPoseOnly: true));
        var rendered = Assert.Single(cpu.Submeshes);
        var exported = Assert.Single(NifExportExtractor.Extract(fixture.Data, fixture.Info).MeshParts).Submesh;
        foreach (var part in new[] { rendered, exported, RenderableSubmeshCloner.DeepClone(exported),
                     RenderableSubmeshCloner.CloneGeometryWithRenderState(rendered, exported) })
        {
            Assert.True(part.HasAuthoredOblivionBodySkinInputs);
            Assert.Equal((0.588f, 0.25f, 0.75f), part.AuthoredOblivionBodySkinAmbientColor);
            Assert.Equal(@"textures\synthetic\body.dds", part.AuthoredOblivionBodySkinDiffusePath);
            Assert.Equal((1f, 1f, 1f), part.MaterialDiffuse);
            Assert.False(part.IsFaceGen);
            Assert.Equal(3, part.VertexCount);
        }
        Assert.Equal(before, fixture.Data);
    }

    private static void AssertWhiteAcceptedAndNonwhiteRejected(NifOblivionBodySkinSceneTestData fixture)
    {
        Assert.True(Read(fixture));
        SetAmbient(fixture, 0.588f, 0.588f, 0.588f);
        Assert.False(Read(fixture));
    }

    private static bool Read(NifOblivionBodySkinSceneTestData fixture) =>
        NifOblivionBodySkinSourceReader.IsEligible(fixture.Data, fixture.Info, 0);

    private static void SetAmbient(NifOblivionBodySkinSceneTestData fixture, float red, float green, float blue)
    {
        var offset = fixture.Source.Offsets["ambient"];
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(offset), red);
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(offset + 4), green);
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(offset + 8), blue);
    }
}
