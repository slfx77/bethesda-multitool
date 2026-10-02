using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Nif.Decoding.NifDecodingTestSupport;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Decoding;

/// <summary>
///     NiTriShapeData, NiTriStripsData and NiMorphData through the schema-driven decoder. The layouts are hand-laid
///     from nif.xml's NiGeometryData (nif.xml:9933-10042), NiTriBasedGeomData, NiTriShapeData (12564-12592),
///     NiTriStripsData (12598-12620) and NiMorphData (10908-10925) for 20.2.0.7 with BS &gt; 0, where the
///     BS202 vercond selects "BS Data Flags" and drops the NiGeometryData "Data Flags".
/// </summary>
public class NifBlockDecoderGeometryTests
{
    private const ushort BsFlagUv = 0x0001;
    private const ushort BsFlagTangents = 0x1000;

    private static readonly float[] Vertices = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, -0.5f];
    private static readonly float[] Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0.6f, 0.8f];
    private static readonly float[] Tangents = [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f];
    private static readonly float[] Bitangents = [0f, 1f, 0f, 0f, 1f, 0f, 0f, 0.8f, -0.6f];
    private static readonly float[] Colors = [1f, 0.5f, 0.25f, 1f, 1.5f, 0f, 0f, 0.5f, 0.1f, 0.2f, 0.3f, 0.4f];
    private static readonly float[] Uvs = [0f, 0f, 1f, 0f, 0.25f, -1.5f];

    /// <summary>The NiGeometryData run shared by the shape and strips data (BS &lt;= 34).</summary>
    private static void GeometryData(
        NifTestBlockWriter w,
        ushort bsDataFlags,
        bool normals,
        bool colors,
        int additionalData = -1)
    {
        w.I32(0); // Group ID (since 10.1.0.114)
        w.U16(3); // Num Vertices
        w.U8(0).U8(0); // Keep Flags, Compress Flags
        w.Bool(true).F32s(Vertices); // Has Vertices, Vertices
        w.U16(bsDataFlags); // BS Data Flags (vercond #BS202#); "Data Flags" is excluded by !#BS202#
        w.Bool(normals);
        if (normals)
        {
            w.F32s(Normals);
            if ((bsDataFlags & BsFlagTangents) != 0)
            {
                w.F32s(Tangents).F32s(Bitangents);
            }
        }

        w.F32s(0.25f, 0.5f, -0.25f, 1.75f); // Bounding Sphere: Center, Radius
        w.Bool(colors);
        if (colors)
        {
            w.F32s(Colors);
        }

        if ((bsDataFlags & BsFlagUv) != 0)
        {
            w.F32s(Uvs); // UV Sets: ((Data Flags & 63) | (BS Data Flags & 1)) rows of Num Vertices
        }

        w.U16(0x4000); // Consistency Flags: CT_STATIC
        w.Ref(additionalData);
    }

    private static NifTestFileBuilder TriShape(bool bigEndian, uint bs, ushort bsDataFlags, bool normals, bool colors)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        builder.AddBlock("NiTriShapeData", w =>
        {
            GeometryData(w, bsDataFlags, normals, colors);
            w.U16(1); // Num Triangles (NiTriBasedGeomData)
            w.U32(3); // Num Triangle Points
            w.Bool(true).U16(0).U16(1).U16(2); // Has Triangles, Triangles
            w.U16(1); // Num Match Groups
            w.U16(2).U16(0).U16(2); // MatchGroup: its own Num Vertices (2), Vertex Indices
        });
        return builder;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiTriShapeData_WithNormalsTangentsColorsAndUvs_DecodesEveryStreamExactly(bool bigEndian)
    {
        var block = DecodeStrict(TriShape(bigEndian, 34, BsFlagUv | BsFlagTangents, true, true), 0);
        var root = block.Root;

        Assert.True(block.IsComplete);
        AssertInteger(root, "Num Vertices", 3);
        AssertFloats(root.Get<NifFloatArrayValue>("Vertices"), 3, Vertices);
        AssertInteger(root, "BS Data Flags", BsFlagUv | BsFlagTangents);
        Assert.False(root.Contains("Data Flags"));
        AssertFloats(root.Get<NifFloatArrayValue>("Normals"), 3, Normals);
        AssertFloats(root.Get<NifFloatArrayValue>("Tangents"), 3, Tangents);
        AssertFloats(root.Get<NifFloatArrayValue>("Bitangents"), 3, Bitangents);

        var sphere = root.Get<NifStructValue>("Bounding Sphere");
        AssertTriple(sphere, "Center", ["x", "y", "z"], 0.25f, 0.5f, -0.25f);
        AssertFloat(sphere, "Radius", 1.75f);

        // Colors are kept raw: 1.5 is not clamped.
        AssertFloats(root.Get<NifFloatArrayValue>("Vertex Colors"), 4, Colors);

        var uvSets = root.Get<NifArrayValue>("UV Sets");
        Assert.True(uvSets.IsRows);
        var uvSet = Assert.IsType<NifFloatArrayValue>(Assert.Single(uvSets.Items));
        AssertFloats(uvSet, 2, Uvs);

        AssertInteger(root, "Consistency Flags", 0x4000);
        Assert.Equal(-1, root.Get<NifRefValue>("Additional Data").Index);
        AssertInteger(root, "Num Triangles", 1);
        AssertInteger(root, "Num Triangle Points", 3);
        var triangles = root.Get<NifUInt16ArrayValue>("Triangles");
        Assert.Equal(3, triangles.ComponentsPerElement);
        Assert.Equal(new ushort[] { 0, 1, 2 }, triangles.Values.ToArray());

        // MatchGroup declares its own "Num Vertices" (2); a flattened scope would read the block's 3 and desync.
        var group = Assert.IsType<NifStructValue>(Assert.Single(root.Get<NifArrayValue>("Match Groups").Items));
        AssertInteger(group, "Num Vertices", 2);
        Assert.Equal(new ushort[] { 0, 2 }, group.Get<NifUInt16ArrayValue>("Vertex Indices").Values.ToArray());
    }

    [Theory]
    [InlineData(false, 14u)]
    [InlineData(false, 34u)]
    [InlineData(true, 14u)]
    [InlineData(true, 34u)]
    public void NiTriShapeData_WithoutOptionalStreams_ReadsNoUvSetsOrTangents(bool bigEndian, uint bs)
    {
        var block = DecodeStrict(TriShape(bigEndian, bs, 0, false, false), 0);
        var root = block.Root;

        Assert.True(block.IsComplete);
        Assert.False(root.Contains("Normals"));
        Assert.False(root.Contains("Tangents"));
        Assert.False(root.Contains("Vertex Colors"));

        // "(Data Flags #BITAND# 63) #BITOR# (BS Data Flags #BITAND# 1)": Data Flags is declared but excluded, so 0.
        var uvSets = root.Get<NifArrayValue>("UV Sets");
        Assert.True(uvSets.IsRows);
        Assert.Equal(0, uvSets.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiTriShapeData_TangentFlagWithoutNormals_ReadsNoTangents(bool bigEndian)
    {
        var block = DecodeStrict(TriShape(bigEndian, 34, BsFlagTangents, false, false), 0);

        Assert.True(block.IsComplete);
        Assert.False(block.Root.Contains("Tangents"));
        Assert.False(block.Root.Contains("Bitangents"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiTriStripsData_ReadsJaggedStripPoints(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        builder.AddBlock("NiTriStripsData", w =>
        {
            GeometryData(w, 0, false, false);
            w.U16(3); // Num Triangles
            w.U16(2); // Num Strips
            w.U16(4).U16(3); // Strip Lengths
            w.Bool(true); // Has Points
            w.U16(0x0100).U16(0x0101).U16(0x0102).U16(0x0203); // strip 0 (values that change under a byte swap)
            w.U16(0x0304).U16(0x0405).U16(0x0506); // strip 1
        });

        var block = DecodeStrict(builder, 0);
        var root = block.Root;

        Assert.True(block.IsComplete);
        Assert.Equal(new ushort[] { 4, 3 }, root.Get<NifUInt16ArrayValue>("Strip Lengths").Values.ToArray());
        var points = root.Get<NifArrayValue>("Points");
        Assert.True(points.IsRows);
        Assert.Equal(2, points.Count);
        Assert.Equal(new ushort[] { 0x0100, 0x0101, 0x0102, 0x0203 },
            Assert.IsType<NifUInt16ArrayValue>(points.Items[0]).Values.ToArray());
        Assert.Equal(new ushort[] { 0x0304, 0x0405, 0x0506 },
            Assert.IsType<NifUInt16ArrayValue>(points.Items[1]).Values.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiMorphData_PassesNumVerticesAsTheMorphArgument(bool bigEndian)
    {
        float[] baseVectors = [0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];
        float[] smileVectors = [0.125f, 0f, 0f, 0f, -0.25f, 0f, 0f, 0f, 3.5f];
        var builder = new NifTestFileBuilder(bigEndian, 34);
        var baseName = builder.AddString("Base");
        var smileName = builder.AddString("Smile");
        builder.AddBlock("NiMorphData", w =>
        {
            w.U32(2); // Num Morphs
            w.U32(3); // Num Vertices (the Morphs arg)
            w.U8(1); // Relative Targets
            w.StringIndex(baseName).F32s(baseVectors); // Morph: Frame Name, Vectors[#ARG#]
            w.StringIndex(smileName).F32s(smileVectors);
        });

        var block = DecodeStrict(builder, 0);

        Assert.True(block.IsComplete);
        var morphs = block.Root.Get<NifArrayValue>("Morphs").Items.Cast<NifStructValue>().ToList();
        Assert.Equal(2, morphs.Count);
        Assert.Equal("Base", morphs[0].Get<NifStringValue>("Frame Name").Text);
        Assert.Equal("Smile", morphs[1].Get<NifStringValue>("Frame Name").Text);
        AssertFloats(morphs[0].Get<NifFloatArrayValue>("Vectors"), 3, baseVectors);
        AssertFloats(morphs[1].Get<NifFloatArrayValue>("Vectors"), 3, smileVectors);
    }
}
