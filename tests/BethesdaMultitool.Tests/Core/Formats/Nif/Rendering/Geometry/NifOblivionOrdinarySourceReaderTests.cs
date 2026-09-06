using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

public sealed class NifOblivionOrdinarySourceReaderTests
{
    private const string AuthoredPath = @"textures\synthetic\body.dds";

    [Theory]
    [InlineData("foot", "Foot", false)]
    [InlineData("Iron Boots", "Anything", true)]
    [InlineData("cloth", "Hand", false)]
    public void Read_OrdinaryIdentityComesFromPropertiesAndStreams(string name, string shape, bool reverse)
    {
        var fixture = new NifOblivionBodySkinTestData(name, shape, reverse);
        var before = (byte[])fixture.Data.Clone();

        Assert.Equal(AuthoredPath, Read(fixture));
        Assert.False(NifOblivionBodySkinSourceReader.IsEligible(fixture.Data, fixture.Info, 0));
        Assert.Equal(before, fixture.Data);
    }

    [Theory]
    [InlineData("skin")]
    [InlineData("SKIN")]
    [InlineData("hair")]
    [InlineData("HaIrVariant")]
    [InlineData("right eye")]
    [InlineData("LEFT EYE")]
    [InlineData("envmap2")]
    [InlineData("envmap variant")]
    [InlineData("refract")]
    [InlineData("dynalpha42")]
    [InlineData("HideSecretDoor")]
    [InlineData("")]
    public void Read_RetailSpecialFamiliesCannotAcquireOrdinaryProvenance(string name)
    {
        Assert.Null(Read(new NifOblivionBodySkinTestData(name)));
    }

    [Fact]
    public void Read_PreservesExistingSkinEligibilityAndRejectsNonwhiteTail()
    {
        var skin = new NifOblivionBodySkinTestData();
        Assert.True(NifOblivionBodySkinSourceReader.IsEligible(skin.Data, skin.Info, 0));
        Assert.Null(Read(skin));
        BinaryPrimitives.WriteSingleLittleEndian(skin.Data.AsSpan(skin.Offsets["ambient"]), 0.588f);
        Assert.Null(Read(skin));
    }

    [Fact]
    public void Read_RequiresExactPcTes4Header()
    {
        var fixture = new NifOblivionBodySkinTestData("foot");
        fixture.Info.IsBigEndian = true;
        Assert.Null(Read(fixture));
        fixture.Info.IsBigEndian = false;
        fixture.Info.HasInlineStrings = false;
        Assert.Null(Read(fixture));
        fixture.Info.HasInlineStrings = true;
        fixture.Info.UserVersion = 12;
        Assert.Null(Read(fixture));
        fixture.Info.UserVersion = 11;
        fixture.Info.BsVersion = 34;
        Assert.Null(Read(fixture));
        fixture.Info.BsVersion = 11;
        fixture.Info.BinaryVersion = 0x14000005;
        Assert.Null(Read(fixture));
        fixture.Info.BinaryVersion = 0x14000004;
        fixture.Info.HeaderString = "Gamebryo File Format, Version 20.0.0.5";
        Assert.Null(Read(fixture));
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
    public void Read_UnsupportedOwnershipModesAndControllersFailClosed(string field, int value)
    {
        var fixture = new NifOblivionBodySkinTestData("foot");
        BinaryPrimitives.WriteInt32LittleEndian(fixture.Data.AsSpan(fixture.Offsets[field]), value);
        Assert.Null(Read(fixture));
    }

    [Theory]
    [InlineData("has-shader")]
    [InlineData("texture-transform")]
    [InlineData("dark")]
    [InlineData("detail")]
    [InlineData("gloss-map")]
    [InlineData("glow")]
    [InlineData("bump")]
    [InlineData("decal")]
    [InlineData("has-colors")]
    public void Read_ExtraInputsCannotMasqueradeAsBaseOnly(string field)
    {
        var fixture = new NifOblivionBodySkinTestData("foot");
        fixture.Data[fixture.Offsets[field]] = 1;
        Assert.Null(Read(fixture));
    }

    [Theory]
    [InlineData("has-base")]
    [InlineData("external")]
    [InlineData("static")]
    [InlineData("direct")]
    [InlineData("has-vertices")]
    [InlineData("has-normals")]
    public void Read_AbsentRequiredInputsFailClosed(string field)
    {
        var fixture = new NifOblivionBodySkinTestData("foot");
        fixture.Data[fixture.Offsets[field]] = 0;
        Assert.Null(Read(fixture));
    }

    [Theory]
    [InlineData("ambient", 0.5f)]
    [InlineData("diffuse", 0.5f)]
    [InlineData("specular", float.NaN)]
    [InlineData("emissive", 0.1f)]
    [InlineData("gloss", float.PositiveInfinity)]
    [InlineData("alpha", 0.5f)]
    [InlineData("normal", float.NaN)]
    [InlineData("uv", float.NegativeInfinity)]
    [InlineData("tangent", float.PositiveInfinity)]
    public void Read_UnsupportedMaterialAndGeometryValuesFailClosed(string field, float value)
    {
        var fixture = new NifOblivionBodySkinTestData("foot");
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(fixture.Offsets[field]), value);
        Assert.Null(Read(fixture));
    }

    [Theory]
    [InlineData("NiTextureEffect")]
    [InlineData("NiAlphaProperty")]
    [InlineData("NiSpecularProperty")]
    [InlineData("BSShaderPPLightingProperty")]
    [InlineData("NiBillboardNode")]
    [InlineData("NiMaterialColorController")]
    public void Read_UnknownSceneStateAndPreparedPropertiesFailClosed(string type)
    {
        var fixture = new NifOblivionBodySkinTestData("foot");
        fixture.Info.Blocks.Add(new BlockInfo { TypeName = type });
        Assert.Null(Read(fixture));
    }

    [Fact]
    public void Read_TruncatedOwnedBlocksFailClosed()
    {
        for (var index = 0; index < 6; index++)
        {
            var fixture = new NifOblivionBodySkinTestData("foot");
            fixture.Info.Blocks[index].Size--;
            Assert.Null(Read(fixture));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0x11)]
    public void Read_EmptyStaticSceneAndSkeletonNodesDoNotSupplyOverrides(ushort flags)
    {
        var fixture = new NifOblivionBodySkinTestData("foot");
        var node = AppendNode(fixture, flags);
        Assert.Equal(AuthoredPath, NifOblivionOrdinarySourceReader.ReadDiffusePath(node.Data, fixture.Info, 0));
    }

    [Theory]
    [InlineData("extra", 1)]
    [InlineData("controller", 0)]
    [InlineData("properties", 1)]
    [InlineData("collision", 0)]
    [InlineData("children", 100)]
    [InlineData("child", 2)]
    [InlineData("effects", 1)]
    public void Read_AncestorOverridesAndMalformedReferencesFailClosed(string field, int value)
    {
        var fixture = new NifOblivionBodySkinTestData("foot");
        var node = AppendNode(fixture, 2);
        BinaryPrimitives.WriteInt32LittleEndian(node.Data.AsSpan(node.Offsets[field]), value);
        Assert.Null(NifOblivionOrdinarySourceReader.ReadDiffusePath(node.Data, fixture.Info, 0));
    }

    [Fact]
    public void ExtractAndClone_PreserveOriginalPathAndIndependentOrdinaryIdentity()
    {
        var fixture = new NifOblivionBodySkinTestData("foot", "Foot");
        var exported = Assert.Single(NifExportExtractor.Extract(fixture.Data, fixture.Info).MeshParts).Submesh;
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            fixture.Data, fixture.Info, bindPoseOnly: true));
        var rendered = Assert.Single(model.Submeshes);

        Assert.True(exported.HasAuthoredOblivionOrdinaryInputs);
        Assert.True(rendered.HasAuthoredOblivionOrdinaryInputs);
        Assert.Equal(AuthoredPath, exported.AuthoredOblivionOrdinaryDiffusePath);
        Assert.Equal(AuthoredPath, rendered.AuthoredOblivionOrdinaryDiffusePath);
        Assert.False(exported.HasAuthoredOblivionBodySkinInputs);
        Assert.False(rendered.IsFaceGen);
        rendered.DiffuseTexturePath = @"injected\replacement.dds";
        var clone = RenderableSubmeshCloner.DeepClone(rendered);
        Assert.True(clone.HasAuthoredOblivionOrdinaryInputs);
        Assert.Equal(AuthoredPath, clone.AuthoredOblivionOrdinaryDiffusePath);
        Assert.NotSame(rendered.Positions, clone.Positions);
        Assert.Equal(rendered.Tangents, clone.Tangents);

        var ordinary = RenderableSubmeshCloner.CloneGeometryWithRenderState(exported, rendered);
        Assert.True(ordinary.HasAuthoredOblivionOrdinaryInputs);
        rendered.HasAuthoredOblivionOrdinaryInputs = false;
        rendered.AuthoredOblivionOrdinaryDiffusePath = null;
        var unknown = RenderableSubmeshCloner.CloneGeometryWithRenderState(exported, rendered);
        Assert.False(unknown.HasAuthoredOblivionOrdinaryInputs);
        Assert.Null(unknown.AuthoredOblivionOrdinaryDiffusePath);
    }

    private static string? Read(NifOblivionBodySkinTestData fixture) =>
        NifOblivionOrdinarySourceReader.ReadDiffusePath(fixture.Data, fixture.Info, 0);

    private static (byte[] Data, Dictionary<string, int> Offsets) AppendNode(
        NifOblivionBodySkinTestData fixture, ushort flags)
    {
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        using var stream = new MemoryStream();
        stream.Write(fixture.Data);
        var start = checked((int)stream.Position);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(4u); writer.Write(Encoding.ASCII.GetBytes("Root"));
        Mark("extra"); writer.Write(0u);
        Mark("controller"); writer.Write(-1);
        writer.Write(flags);
        WriteVector(Vector3.Zero);
        WriteVector(Vector3.UnitX); WriteVector(Vector3.UnitY); WriteVector(Vector3.UnitZ);
        writer.Write(1f);
        Mark("properties"); writer.Write(0u);
        Mark("collision"); writer.Write(-1);
        Mark("children"); writer.Write(1u);
        Mark("child"); writer.Write(0);
        Mark("effects"); writer.Write(0u);
        writer.Flush();
        fixture.Info.Blocks.Add(new BlockInfo
        {
            Index = fixture.Info.Blocks.Count,
            TypeName = "NiNode",
            DataOffset = start,
            Size = checked((int)stream.Position) - start
        });
        fixture.Info.BlockCount++;
        return (stream.ToArray(), offsets);

        void Mark(string name) => offsets.Add(name, checked((int)stream.Position));
        void WriteVector(Vector3 value)
        {
            writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z);
        }
    }
}
