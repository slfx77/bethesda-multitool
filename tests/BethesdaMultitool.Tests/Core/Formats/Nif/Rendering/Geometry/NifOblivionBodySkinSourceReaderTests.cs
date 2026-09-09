using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

public sealed class NifOblivionBodySkinSourceReaderTests
{
    [Theory]
    [InlineData("skin", "Hand", false)]
    [InlineData("Skin", "LowerBody:0", true)]
    [InlineData("sKiN", "Arms", false)]
    [InlineData("skin", "UnrecognizedBodyPart", false)]
    public void Read_ExactStaticSubsetHasSourceProvenance(string material, string shape, bool reverse)
    {
        var fixture = new NifOblivionBodySkinTestData(material, shape, reverse);
        var before = (byte[])fixture.Data.Clone();

        Assert.True(Read(fixture));
        Assert.Equal(before, fixture.Data);
    }

    [Theory]
    [InlineData("foot", "Foot")]
    [InlineData("Iron Boots", "Foot")]
    [InlineData("IronFCuriass", "Arms")]
    [InlineData("skins", "Hand")]
    [InlineData("skin", "")]
    public void Read_MaterialOwnerAndNonemptyShapeAreRequired(string material, string shape)
    {
        Assert.False(Read(new NifOblivionBodySkinTestData(material, shape)));
    }

    [Fact]
    public void Read_OnlyPinnedPcHeaderAndVersionsAreAccepted()
    {
        var fixture = new NifOblivionBodySkinTestData();
        fixture.Info.IsBigEndian = true;
        Assert.False(Read(fixture));
        fixture.Info.IsBigEndian = false;
        fixture.Info.HasInlineStrings = false;
        Assert.False(Read(fixture));
        fixture.Info.HasInlineStrings = true;
        fixture.Info.UserVersion = 12;
        Assert.False(Read(fixture));
        fixture.Info.UserVersion = 11;
        fixture.Info.BsVersion = 34;
        Assert.False(Read(fixture));
        fixture.Info.BsVersion = 11;
        fixture.Info.BinaryVersion = 0x14000005;
        Assert.False(Read(fixture));
        fixture.Info.BinaryVersion = 0x14000004;
        fixture.Info.HeaderString = "Gamebryo File Format, Version 20.0.0.5";
        Assert.False(Read(fixture));
    }

    [Theory]
    [InlineData("shape-controller", 0)]
    [InlineData("shape-extra-count", 2)]
    [InlineData("shape-extra", 5)]
    [InlineData("property-count", 3)]
    [InlineData("property-second", 2)]
    [InlineData("collision", 1)]
    [InlineData("geometry-ref", 1)]
    [InlineData("material-extra-count", 1)]
    [InlineData("material-controller", 0)]
    [InlineData("texturing-extra-count", 1)]
    [InlineData("texturing-controller", 0)]
    [InlineData("source-controller", 0)]
    [InlineData("apply-mode", 3)]
    [InlineData("texture-count", 8)]
    [InlineData("source-ref", 2)]
    [InlineData("clamp", 0)]
    [InlineData("filter", 0)]
    [InlineData("uv-set", 1)]
    [InlineData("shader-textures", 1)]
    [InlineData("pixel-data", 1)]
    [InlineData("additional-data", 1)]
    [InlineData("triangle-points", 6)]
    [InlineData("tangent-length", 71)]
    [InlineData("shape-name", -1)]
    public void Read_UnsupportedOrMalformedIntegerFieldsFailClosed(string field, int value)
    {
        var fixture = new NifOblivionBodySkinTestData();
        BinaryPrimitives.WriteInt32LittleEndian(fixture.Data.AsSpan(fixture.Offsets[field]), value);
        Assert.False(Read(fixture));
    }

    [Theory]
    [InlineData("has-shader", 1)]
    [InlineData("has-base", 0)]
    [InlineData("texture-transform", 1)]
    [InlineData("dark", 1)]
    [InlineData("detail", 1)]
    [InlineData("gloss-map", 1)]
    [InlineData("glow", 1)]
    [InlineData("bump", 1)]
    [InlineData("decal", 1)]
    [InlineData("external", 0)]
    [InlineData("static", 0)]
    [InlineData("direct", 0)]
    [InlineData("has-vertices", 0)]
    [InlineData("has-normals", 0)]
    [InlineData("has-colors", 1)]
    public void Read_ExtraSlotsControllersAndGeometryVariantsFailClosed(string field, byte value)
    {
        var fixture = new NifOblivionBodySkinTestData();
        fixture.Data[fixture.Offsets[field]] = value;
        Assert.False(Read(fixture));
    }

    [Theory]
    [InlineData("ambient", 0.5f)]
    [InlineData("diffuse", 0.5f)]
    [InlineData("specular", float.NaN)]
    [InlineData("emissive", 0.1f)]
    [InlineData("gloss", float.PositiveInfinity)]
    [InlineData("alpha", 0.5f)]
    [InlineData("position", float.NaN)]
    [InlineData("normal", float.NaN)]
    [InlineData("uv", float.NegativeInfinity)]
    [InlineData("tangent", float.PositiveInfinity)]
    [InlineData("bitangent", float.MaxValue)]
    public void Read_UnsupportedColorAndNonfiniteStreamsFailClosed(string field, float value)
    {
        var fixture = new NifOblivionBodySkinTestData();
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(fixture.Offsets[field]), value);
        Assert.False(Read(fixture));
    }

    [Fact]
    public void Read_ZeroAuthoredVectorsAreNotRepairedOrInferredFromOtherGeometry()
    {
        foreach (var field in new[] { "normal", "tangent", "bitangent" })
        {
            var fixture = new NifOblivionBodySkinTestData();
            fixture.Data.AsSpan(fixture.Offsets[field], 12).Clear();
            Assert.False(Read(fixture));
        }
    }

    [Fact]
    public void Read_StaleLegacyMatchIndicesDoNotReplaceOrDisqualifyAuthoredNormals()
    {
        var fixture = new NifOblivionBodySkinTestData();
        var indexOffset = fixture.Offsets["match-group"] + 2;
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.Data.AsSpan(indexOffset), ushort.MaxValue);
        Assert.True(Read(fixture));
        BinaryPrimitives.WriteUInt16LittleEndian(
            fixture.Data.AsSpan(fixture.Offsets["match-group"]), ushort.MaxValue);
        Assert.False(Read(fixture)); // Framing still must stay inside the owning block.
    }

    [Fact]
    public void Read_TruncatedBlocksAndUnexpectedPropertyTypesFailClosed()
    {
        for (var index = 0; index < 6; index++)
        {
            var fixture = new NifOblivionBodySkinTestData();
            fixture.Info.Blocks[index].Size--;
            Assert.False(Read(fixture));
        }

        var unsupported = new NifOblivionBodySkinTestData();
        unsupported.Info.Blocks[2].TypeName = "NiAlphaProperty";
        Assert.False(Read(unsupported));
        unsupported.Info.Blocks[2].TypeName = "NiMaterialProperty";
        unsupported.Info.Blocks.Add(new BlockInfo { TypeName = "NiTextureEffect" });
        Assert.False(Read(unsupported));
    }

    [Fact]
    public void ExtractAndClone_PreserveProvenanceWithoutPromotingTheRawMaterial()
    {
        var fixture = new NifOblivionBodySkinTestData();
        var exported = Assert.Single(NifExportExtractor.Extract(fixture.Data, fixture.Info).MeshParts).Submesh;
        var cpu = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            fixture.Data, fixture.Info, bindPoseOnly: true));
        var rendered = Assert.Single(cpu.Submeshes);

        Assert.True(exported.HasAuthoredOblivionBodySkinInputs);
        Assert.True(rendered.HasAuthoredOblivionBodySkinInputs);
        Assert.Equal(@"textures\synthetic\body.dds", exported.AuthoredOblivionBodySkinDiffusePath);
        Assert.Equal(@"textures\synthetic\body.dds", rendered.AuthoredOblivionBodySkinDiffusePath);
        Assert.False(exported.IsFaceGen);
        Assert.False(rendered.IsFaceGen);
        Assert.True(RenderableSubmeshCloner.DeepClone(exported).HasAuthoredOblivionBodySkinInputs);
        Assert.True(RenderableSubmeshCloner.CloneGeometryWithRenderState(
            exported, rendered).HasAuthoredOblivionBodySkinInputs);
        exported.DiffuseTexturePath = @"body_egt\synthetic.dds";
        Assert.Equal(@"textures\synthetic\body.dds",
            RenderableSubmeshCloner.DeepClone(exported).AuthoredOblivionBodySkinDiffusePath);
        Assert.Equal(NifOblivionTangentTestData.Normals, exported.Normals);
        Assert.Equal(NifOblivionTangentTestData.Tangents[1],
            NifOblivionTangentTestData.VectorAt(exported.Tangents!, 1));
    }

    private static bool Read(NifOblivionBodySkinTestData fixture)
    {
        return NifOblivionBodySkinSourceReader.IsEligible(fixture.Data, fixture.Info, 0);
    }
}