using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Slice 3 geometry through the real reader: stored streams kept bit for bit (positions, authored normals, UV set 0
///     without a V flip, raw colors including overbright and negative values), the primary color attribute, flat
///     normals when none are stored, tangent handedness, and the list and strip triangle rules. Fixtures are hand-laid
///     from nif.xml by <see cref="NifTestBlockLayouts" />; every produced document passes Shared's structure validation.
/// </summary>
public class NifModelGeometryTests
{
    private static readonly float[] QuadVertices = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0.5f];

    /// <summary>Normals kept raw: the last one is deliberately not unit length.</summary>
    private static readonly float[] QuadNormals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0.6f, 0.8f, 0f, 0.6f, 0.9f];

    /// <summary>Overbright (1.5, 16) and negative (-0.125) components, as measured in about 370 FNV files.</summary>
    private static readonly float[] QuadColors =
    [
        1.5f, 0.25f, -0.125f, 1f,
        16f, 1f, 1f, 0.5f,
        0.2f, 0.3f, 0.4f, 1f,
        1f, 1f, 1f, 1f
    ];

    /// <summary>Asymmetric in V (no v equals 1 - v), so a flipped reading is visible.</summary>
    private static readonly float[] QuadUvs = [0.25f, 0.1f, 0.75f, 0.1f, 0.25f, 0.8f, 1.5f, -0.5f];

    private static readonly ushort[] QuadTriangles = [0, 1, 2, 1, 3, 2];

    private static NifTestGeometryStreams Quad(float[]? normals = null, float[]? colors = null, float[]? uvs = null)
    {
        return new NifTestGeometryStreams
        {
            Vertices = QuadVertices,
            Normals = normals,
            Colors = colors,
            Uvs = uvs
        };
    }

    [Theory]
    [InlineData(false, 14u)]
    [InlineData(false, 34u)]
    [InlineData(true, 21u)]
    [InlineData(true, 34u)]
    public void TriShape_KeepsEveryStoredStreamBitForBit(bool bigEndian, uint bs)
    {
        var bytes = SingleTriShape(Quad(QuadNormals, QuadColors, QuadUvs), QuadTriangles, bigEndian, bs);

        var result = Read(bytes);
        var document = result.Document;

        var mesh = Assert.Single(document.Meshes);
        Assert.Equal("Shape", mesh.Name);
        Assert.Null(mesh.MorphWeights);
        Assert.Equal(0, document.Nodes[1].MeshIndex);
        Assert.Null(document.Nodes[0].MeshIndex);
        Assert.Null(document.Nodes[1].SkinIndex);
        var primitive = Assert.Single(mesh.Primitives);
        Assert.Equal("block:2", primitive.Name);
        Assert.Null(primitive.MaterialIndex);
        Assert.Equal(SceneColorEncoding.FloatingPoint, primitive.ColorEncoding);
        Assert.Equal(SceneNormalMode.Vertex, primitive.NormalMode);
        Assert.Equal(SceneNormalProvenanceKind.Authored, primitive.NormalProvenance!.Kind);
        Assert.Equal([0, 1, 2, 1, 3, 2], primitive.Indices);

        Assert.Equal(Bits(QuadVertices), primitive.Vertices.SelectMany(v => Bits(v.Position)).ToArray());
        Assert.Equal(Bits(QuadNormals), primitive.Vertices.SelectMany(v => Bits(v.Normal)).ToArray());
        Assert.Equal(Bits(QuadColors), primitive.Vertices.SelectMany(v => Bits(v.Color)).ToArray());
        Assert.Equal(Bits(QuadUvs), primitive.Vertices.SelectMany(v => Bits(v.TexCoord)).ToArray());
        Assert.Empty(primitive.AdditionalTextureCoordinates);
        Assert.Null(primitive.Tangents);

        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:1").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:2").Kind);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     Colors outside [0, 1] are kept raw in the vertices and in the primary attribute, which declares sRGB (Assumed,
    ///     RE-11). Control: a clamping implementation would give different bits for 1.5, 16 and -0.125.
    /// </summary>
    [Fact]
    public void Colors_OutsideTheUnitRange_AreKeptRaw_InTheVerticesAndThePrimaryAttribute()
    {
        var document = Read(SingleTriShape(Quad(QuadNormals, QuadColors), QuadTriangles)).Document;
        var primitive = PrimitiveOf(document, "Shape");

        Assert.Equal(0, primitive.PrimaryColorAttributeIndex);
        var stream = Assert.Single(primitive.Attributes);
        Assert.Equal(NifModelGeometryData.VertexColorAttribute, stream.Name);
        Assert.Equal(SceneAttributeDomain.Vertex, stream.Domain);
        Assert.Equal(SceneAttributeComponentType.Float32, stream.ComponentType);
        Assert.Equal(4, stream.Components);
        Assert.Equal(4, stream.Count);
        Assert.False(stream.Normalized);
        Assert.Equal(SceneColorSpace.Srgb, stream.ColorSpace);
        Assert.Equal(SceneValueProvenance.Assumed, stream.ColorSpaceProvenance);
        Assert.Contains("RE-11", stream.ColorSpaceEvidence);
        Assert.Equal(LittleEndian(Bits(QuadColors)), stream.CopyContent());
        Assert.Equal(new Vector4(1.5f, 0.25f, -0.125f, 1f), primitive.Vertices[0].Color);
        Assert.Equal(new Vector4(16f, 1f, 1f, 0.5f), primitive.Vertices[1].Color);

        var clamped = QuadColors.Select(c => Math.Clamp(c, 0f, 1f)).ToArray();
        Assert.NotEqual(Bits(clamped), primitive.Vertices.SelectMany(v => Bits(v.Color)).ToArray());
        Assert.Equal(3, (int)PrimitivePayload(document, 0)["vertexColors"]!["componentsOutsideUnitRange"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>With no stored colors the portable colors are neutral (1, 1, 1, 1) and no attribute is declared.</summary>
    [Fact]
    public void Colors_Absent_AreNeutral_WithoutAnAttribute()
    {
        var document = Read(SingleTriShape(Quad(QuadNormals), QuadTriangles)).Document;
        var primitive = PrimitiveOf(document, "Shape");

        Assert.All(primitive.Vertices, v => Assert.Equal(Vector4.One, v.Color));
        Assert.Empty(primitive.Attributes);
        Assert.Null(primitive.PrimaryColorAttributeIndex);
        Assert.Equal("absent", (string)PrimitivePayload(document, 0)["vertexColors"]!["state"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A color array holding NaN or infinity (measured in 15 FNV files, e.g. nvbungalow01.nif) keeps its exact bits in
    ///     a non-primary UInt32 attribute, gets neutral portable colors and a diagnostic naming the block, and the document
    ///     still validates. Control: the same bits declared as a Float32 attribute fail Shared's structure validation.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Colors_NonFinite_KeepTheirBits_InARawAttribute_WithNeutralColorsAndADiagnostic(bool bigEndian)
    {
        uint[] colorBits =
        [
            Bit(0.5f), Bit(0.5f), Bit(0.5f), Bit(1f),
            0x7FC00001u, Bit(1f), Bit(1f), Bit(1f),
            Bit(1e30f), 0x7F800000u, Bit(0f), Bit(1f),
            Bit(1f), Bit(1f), Bit(1f), Bit(1f)
        ];
        var streams = new NifTestGeometryStreams
        {
            Vertices = QuadVertices,
            Normals = QuadNormals,
            ColorBits = colorBits
        };

        var document = Read(SingleTriShape(streams, QuadTriangles, bigEndian)).Document;
        var primitive = PrimitiveOf(document, "Shape");

        Assert.All(primitive.Vertices, v => Assert.Equal(Vector4.One, v.Color));
        Assert.Null(primitive.PrimaryColorAttributeIndex);
        var stream = Assert.Single(primitive.Attributes);
        Assert.Equal(NifModelGeometryData.RawVertexColorAttribute, stream.Name);
        Assert.Equal(SceneAttributeComponentType.UInt32, stream.ComponentType);
        Assert.Equal(SceneColorSpace.Unknown, stream.ColorSpace);
        Assert.Equal(LittleEndian(colorBits), stream.CopyContent());
        var diagnostic = Assert.Single(document.Diagnostics,
            d => d.Code == NifModelGeometryData.NonFiniteColorDiagnostic);
        Assert.Contains("Block 2 (NiTriShapeData)", DiagnosticText(diagnostic));
        var colors = PrimitivePayload(document, 0)["vertexColors"]!;
        Assert.Equal("non-finite", (string)colors["state"]!);
        Assert.Equal(2, (int)colors["nonFiniteComponents"]!);
        SceneValidation.ValidateStructure(document);

        // Baseline: the same minimal document around the reader's UInt32 stream validates, so the control below fails
        // only because of the Float32 declaration.
        SceneValidation.ValidateStructure(WithAttribute(primitive, stream));
        var asFloat = new SceneAttributeStream("control", "control", SceneAttributeDomain.Vertex,
            SceneAttributeComponentType.Float32, 4, 4, stream.CopyContent());
        Assert.Throws<InvalidDataException>(() => SceneValidation.ValidateStructure(WithAttribute(primitive, asFloat)));
    }

    /// <summary>
    ///     A UV set holding NaN or infinity (measured in 4 FNV and 5 FO3 retail files, e.g. nv_thetops_pool.nif with 2 of
    ///     766 components) keeps its exact bits in a UInt32 x2 attribute and gets a diagnostic; only the non-finite
    ///     components read as 0, the other vertices keep their coordinates, and the document validates. With finite
    ///     colors beside them (the retail shape, scolbld06georgetown01.nif) the primary color index still names the
    ///     color attribute, not the raw UV one added before it. Control: the finite fixture yields no raw attribute, no
    ///     diagnostic and a zero count.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Uvs_NonFinite_KeepTheirBits_InARawAttribute_WithZeroedComponentsAndADiagnostic(bool bigEndian)
    {
        uint[] uvBits =
        [
            Bit(0.25f), Bit(0.1f),
            0xFFC00000u, Bit(0.1f),
            Bit(0.25f), 0x7F800000u,
            Bit(1.5f), Bit(-0.5f)
        ];
        var streams = new NifTestGeometryStreams
        {
            Vertices = QuadVertices, Normals = QuadNormals, Colors = QuadColors, UvBits = uvBits
        };

        var document = Read(SingleTriShape(streams, QuadTriangles, bigEndian)).Document;
        var primitive = PrimitiveOf(document, "Shape");

        Assert.Equal(new Vector2(0.25f, 0.1f), primitive.Vertices[0].TexCoord);
        Assert.Equal(new Vector2(0f, 0.1f), primitive.Vertices[1].TexCoord);
        Assert.Equal(new Vector2(0.25f, 0f), primitive.Vertices[2].TexCoord);
        Assert.Equal(new Vector2(1.5f, -0.5f), primitive.Vertices[3].TexCoord);
        Assert.Equal(2, primitive.Attributes.Count);
        var stream = Assert.Single(primitive.Attributes,
            a => a.Name == NifModelGeometryData.RawTexCoordAttribute + "0.raw");
        Assert.Equal(NifModelGeometryData.VertexColorAttribute,
            primitive.Attributes[Assert.IsType<int>(primitive.PrimaryColorAttributeIndex)].Name);
        Assert.Equal(SceneAttributeComponentType.UInt32, stream.ComponentType);
        Assert.Equal(2, stream.Components);
        Assert.Equal(LittleEndian(uvBits), stream.CopyContent());
        var diagnostic = Assert.Single(document.Diagnostics,
            d => d.Code == NifModelGeometryData.NonFiniteTexCoordDiagnostic);
        Assert.Contains("Block 2 (NiTriShapeData): 2 texture-coordinate component(s) of UV set 0", DiagnosticText(diagnostic));
        Assert.Equal(2, (int)PrimitivePayload(document, 0)["texCoordNonFiniteComponents"]!);
        SceneValidation.ValidateStructure(document);

        var control = Read(SingleTriShape(Quad(QuadNormals, uvs: QuadUvs), QuadTriangles, bigEndian)).Document;
        var controlPrimitive = PrimitiveOf(control, "Shape");
        Assert.DoesNotContain(controlPrimitive.Attributes, a => a.Name.StartsWith(NifModelGeometryData.RawTexCoordAttribute, StringComparison.Ordinal));
        Assert.DoesNotContain(control.Diagnostics, d => d.Code == NifModelGeometryData.NonFiniteTexCoordDiagnostic);
        Assert.Equal(0, (int)PrimitivePayload(control, 0)["texCoordNonFiniteComponents"]!);
    }

    /// <summary>
    ///     UV set 0 is copied with no V flip (NIF, the document and DDS are all top-left origin). Control: the fixture is
    ///     asymmetric, so the flipped reading 1 - v differs on every vertex.
    /// </summary>
    [Fact]
    public void Uvs_AreNotFlipped()
    {
        var document = Read(SingleTriShape(Quad(QuadNormals, uvs: QuadUvs), QuadTriangles)).Document;
        var primitive = PrimitiveOf(document, "Shape");

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(QuadUvs[i * 2 + 1], primitive.Vertices[i].TexCoord.Y);
            Assert.NotEqual(1f - QuadUvs[i * 2 + 1], primitive.Vertices[i].TexCoord.Y);
        }

        var payload = PrimitivePayload(document, 0);
        Assert.Equal(1, (int)payload["uvSets"]!);
        Assert.False((bool)payload["uvVFlip"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     With no stored normals the vertices carry (0, 0, 0), NormalMode Flat and Flat provenance, and native state says
    ///     so. Control: Shared rejects a Flat primitive that declares Authored normals, so the two must agree.
    /// </summary>
    [Fact]
    public void Normals_Absent_AreFlat_AndAgreeWithTheNormalMode()
    {
        var document = Read(SingleTriShape(Quad(), QuadTriangles)).Document;
        var primitive = PrimitiveOf(document, "Shape");

        Assert.All(primitive.Vertices, v => Assert.Equal(Vector3.Zero, v.Normal));
        Assert.Equal(SceneNormalMode.Flat, primitive.NormalMode);
        Assert.Equal(SceneNormalProvenanceKind.Flat, primitive.NormalProvenance!.Kind);
        Assert.StartsWith("absent", (string)PrimitivePayload(document, 0)["normals"]!);
        SceneValidation.ValidateStructure(document);

        SceneValidation.ValidateStructure(WithPrimitive(new ScenePrimitive("baseline", primitive.Vertices,
            primitive.Indices, normalMode: SceneNormalMode.Flat)
        {
            NormalProvenance = new SceneNormalProvenance(SceneNormalProvenanceKind.Flat)
        }));
        var disagreeing = new ScenePrimitive("control", primitive.Vertices, primitive.Indices,
            normalMode: SceneNormalMode.Flat)
        {
            NormalProvenance = new SceneNormalProvenance(SceneNormalProvenanceKind.Authored)
        };
        Assert.Throws<InvalidDataException>(() => SceneValidation.ValidateStructure(WithPrimitive(disagreeing)));
    }

    /// <summary>
    ///     Tangent xyz is the stored Bitangents array, raw (a length-2 direction stays length 2): it is the array that
    ///     runs along +dP/du, which glTF's TANGENT requires (see <see cref="NifModelTangentFrameTests" /> for the frame
    ///     pinned against UVs). w is sign(dot(cross(N, Bitangents), -Tangents)): +1, -1, and +1 for a zero Tangents
    ///     element, counted as undetermined. Both stored arrays stay in native state under their nif.xml names. Controls:
    ///     the unnegated product sign(dot(cross(N, Bitangents), Tangents)) gives the opposite sign on both determined
    ///     vertices, and the xyz is not the stored Tangents array.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tangents_TakeTheStoredBitangentsDirection_AndTheirSignFromBothArrays(bool bigEndian)
    {
        float[] normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f];
        float[] tangents = [0f, -1f, 0f, 0f, 1f, 0f, 0f, 0f, 0f];
        float[] bitangents = [1f, 0f, 0f, 2f, 0f, 0f, 1f, 0f, 0f];
        var streams = new NifTestGeometryStreams
        {
            Vertices = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Normals = normals,
            Tangents = tangents,
            Bitangents = bitangents
        };

        var document = Read(SingleTriShape(streams, [0, 1, 2], bigEndian)).Document;
        var primitive = PrimitiveOf(document, "Shape");

        var values = Assert.IsType<SceneTangents>(primitive.Tangents).Values;
        Assert.Equal(
            [new Vector4(1f, 0f, 0f, 1f), new Vector4(2f, 0f, 0f, -1f), new Vector4(1f, 0f, 0f, 1f)], values);
        var payload = PrimitivePayload(document, 0)["tangents"]!;
        Assert.True((bool)payload["typed"]!);
        Assert.Equal("Bitangents", (string)payload["xyz"]!);
        Assert.Equal("Tangents", (string)payload["handednessFrom"]!);
        Assert.Equal(1, (int)payload["handedness"]!["positive"]!);
        Assert.Equal(1, (int)payload["handedness"]!["negative"]!);
        Assert.Equal(1, (int)payload["handedness"]!["undeterminedAsPositive"]!);
        Assert.Equal(NifModelGeometryData.HandednessRule, (string)payload["rule"]!);
        Assert.Equal(nameof(SceneValueProvenance.Assumed), (string)payload["provenance"]!);
        var frame = PrimitivePayload(document, 0)["storedTangentFrame"]!;
        Assert.Equal(Bits(tangents), Bits(Components(frame["Tangents"]!)));
        Assert.Equal(Bits(bitangents), Bits(Components(frame["Bitangents"]!)));
        SceneValidation.ValidateStructure(document);

        for (var i = 0; i < 2; i++)
        {
            var n = new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
            var t = new Vector3(tangents[i * 3], tangents[i * 3 + 1], tangents[i * 3 + 2]);
            var b = new Vector3(bitangents[i * 3], bitangents[i * 3 + 1], bitangents[i * 3 + 2]);
            Assert.NotEqual(values[i].W, (float)MathF.Sign(Vector3.Dot(Vector3.Cross(n, b), t)));
            Assert.NotEqual(t, new Vector3(values[i].X, values[i].Y, values[i].Z));
        }
    }

    /// <summary>
    ///     A list triangle repeating an index is dropped and counted in native state, and no emitted triangle repeats
    ///     an index. Control: the stored list does repeat one (so keeping it would emit a face the Blender writer drops).
    /// </summary>
    [Fact]
    public void ListTriangles_RepeatingAnIndex_AreDroppedAndCounted()
    {
        ushort[] stored = [0, 1, 2, 1, 1, 3, 0, 2, 3];

        var document = Read(SingleTriShape(Quad(QuadNormals), stored)).Document;
        var primitive = PrimitiveOf(document, "Shape");

        Assert.Equal([0, 1, 2, 0, 2, 3], primitive.Indices);
        var triangles = PrimitivePayload(document, 0)["triangles"]!;
        Assert.Equal("list", (string)triangles["form"]!);
        Assert.Equal(3, (int)triangles["stored"]!);
        Assert.Equal(2, (int)triangles["kept"]!);
        Assert.Equal(1, (int)triangles["droppedRepeatedIndex"]!);
        Assert.Equal([1], triangles["droppedOrdinals"]!.AsArray().Select(v => (int)v!));
        Assert.False(RepeatsAnIndex(primitive.Indices));
        Assert.True(RepeatsAnIndex(stored.Select(i => (int)i).ToList()));
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A triangle with three distinct indices is kept even when its positions have zero area. Control: the fixture's
    ///     first triangle really has zero area (its three positions are collinear), so an area filter would remove it.
    /// </summary>
    [Fact]
    public void ListTriangles_ZeroAreaWithDistinctIndices_AreKept()
    {
        var streams = new NifTestGeometryStreams { Vertices = [0f, 0f, 0f, 1f, 0f, 0f, 2f, 0f, 0f, 0f, 1f, 0f] };

        var document = Read(SingleTriShape(streams, [0, 1, 2, 0, 1, 3])).Document;
        var primitive = PrimitiveOf(document, "Shape");

        Assert.Equal([0, 1, 2, 0, 1, 3], primitive.Indices);
        Assert.Equal(0, (int)PrimitivePayload(document, 0)["triangles"]!["droppedRepeatedIndex"]!);
        var p = primitive.Vertices;
        Assert.Equal(Vector3.Zero, Vector3.Cross(p[1].Position - p[0].Position, p[2].Position - p[0].Position));
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     NiTriStrips through the reader: the parity rule, the repeated-index strip triangle dropped and counted per
    ///     strip, and the stored strip lengths in native state.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Strips_AreTriangulatedWithParity_AndTheirDropsCounted(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        AddTriStrips(builder, builder.AddString("Strip"), 2);
        AddTriStripsData(builder, new NifTestGeometryStreams
        {
            Vertices = [0f, 1f, 0f, 0f, 0f, 0f, 1f, 1f, 0f, 1f, 0f, 0f, 2f, 1f, 0f]
        }, [[0, 1, 2, 3, 4], [4, 4, 3]]);

        var result = Read(builder.Build());
        var document = result.Document;
        var primitive = PrimitiveOf(document, "Strip");

        Assert.Equal([0, 1, 2, 1, 3, 2, 2, 3, 4], primitive.Indices);
        var triangles = PrimitivePayload(document, 0)["triangles"]!;
        Assert.Equal("strips", (string)triangles["form"]!);
        Assert.Equal(4, (int)triangles["stored"]!);
        Assert.Equal(1, (int)triangles["droppedRepeatedIndex"]!);
        Assert.Equal([0, 1], triangles["droppedPerStrip"]!.AsArray().Select(v => (int)v!));
        Assert.Equal([5, 3], triangles["stripLengths"]!.AsArray().Select(v => (int)v!));
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:2").Kind);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>BSSegmentedTriShape is a NiTriShape with segments: its geometry is typed the same way.</summary>
    [Fact]
    public void SegmentedTriShape_IsTypedLikeATriShape()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        var name = builder.AddString("Segmented");
        builder.AddBlock("BSSegmentedTriShape", w =>
        {
            NifTestBlockLayouts.GeometryShape(w, 34, name, 2);
            NifTestBlockLayouts.SegmentedTail(w, [(0, 0, 2)]);
        });
        AddTriShapeData(builder, Quad(QuadNormals), QuadTriangles);

        var result = Read(builder.Build());

        Assert.Equal([0, 1, 2, 1, 3, 2], PrimitiveOf(result.Document, "Segmented").Indices);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:1").Kind);
        SceneValidation.ValidateStructure(result.Document);
    }

    /// <summary>
    ///     A shape placed under two parents is one mesh placed by two nodes, and the primitive row lists both
    ///     occurrences; two shapes sharing one data block are two meshes and the data block stays Typed.
    /// </summary>
    [Fact]
    public void InstancedShape_IsOneMesh_AndSharedDataFeedsTwoMeshes()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1, 2, 4]);
        AddNode(builder, builder.AddString("A"), [3]);
        AddNode(builder, builder.AddString("B"), [3]);
        AddTriShape(builder, builder.AddString("Instanced"), 5);
        AddTriShape(builder, builder.AddString("Sharing"), 5);
        AddTriShapeData(builder, Quad(QuadNormals), QuadTriangles);

        var result = Read(builder.Build());
        var document = result.Document;

        Assert.Equal(2, document.Meshes.Count);
        var placements = document.Nodes.Where(n => n.Name == "Instanced").ToList();
        Assert.Equal(2, placements.Count);
        Assert.All(placements, node => Assert.Equal(0, node.MeshIndex));
        Assert.Equal(1, document.Nodes.Single(n => n.Name == "Sharing").MeshIndex);
        Assert.Equal(2, PrimitivePayload(document, 0)["nodes"]!.AsArray().Count);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:5").Kind);
        var dataRow = Rows(document, NifModelNativeState.BlockKind)
            .Single(r => r.SourceLocation?.ElementIdentity == "block:5");
        Assert.Equal(new SceneElementRef(SceneElementKind.Mesh, 0), dataRow.Target);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     One <c>bmt.nif.primitive</c> row per primitive, targeting primitive 0 of its mesh and located at the data block,
    ///     with the stored NiGeometryData facts.
    /// </summary>
    [Fact]
    public void PrimitiveRow_TargetsThePrimitive_AndCarriesTheStoredFacts()
    {
        var streams = new NifTestGeometryStreams
        {
            Vertices = QuadVertices,
            Normals = QuadNormals,
            BoundingSphere = [0.5f, 0.5f, 0.25f, 0.75f],
            ConsistencyFlags = 0x0000
        };
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        AddTriShape(builder, builder.AddString("Shape"), 2);
        AddTriShapeData(builder, streams, QuadTriangles, matchGroups: [[0, 3]]);

        var document = Read(builder.Build()).Document;

        var row = Assert.Single(Rows(document, NifModelGeometryReader.PrimitiveKind));
        Assert.Equal(new SceneElementRef(SceneElementKind.Primitive, 0, 0), row.Target);
        Assert.Equal(NifModelGeometryReader.PrimitivePayloadVersion, row.Version);
        Assert.Equal("block:2", row.SourceLocation?.ElementIdentity);
        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        Assert.Equal(1, (int)payload["geometryBlock"]!);
        Assert.Equal(2, (int)payload["dataBlock"]!);
        Assert.Equal(0, (int)payload["bsDataFlags"]!);
        Assert.Null(payload["dataFlags"]);
        Assert.Equal(0, (int)payload["consistencyFlags"]!);
        Assert.Equal(-1, (int)payload["additionalData"]!);
        Assert.Equal(0.75f, (float)payload["boundingSphere"]!["Radius"]!);
        Assert.Equal(1, payload["matchGroups"]!.AsArray().Count);
        Assert.Equal(-1, (int)payload["skinInstance"]!);
        SceneValidation.ValidateStructure(document);
    }

    private static uint Bit(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

    /// <summary>The components of a native array value written element-major (an array of arrays of numbers).</summary>
    private static float[] Components(JsonNode node)
    {
        return node.AsArray().SelectMany(element => element!.AsArray().Select(component => (float)component!)).ToArray();
    }

    private static uint[] Bits(IEnumerable<float> values)
    {
        return values.Select(Bit).ToArray();
    }

    private static uint[] Bits(Vector2 value)
    {
        return [Bit(value.X), Bit(value.Y)];
    }

    private static uint[] Bits(Vector3 value)
    {
        return [Bit(value.X), Bit(value.Y), Bit(value.Z)];
    }

    private static uint[] Bits(Vector4 value)
    {
        return [Bit(value.X), Bit(value.Y), Bit(value.Z), Bit(value.W)];
    }

    private static byte[] LittleEndian(uint[] bits)
    {
        var bytes = new byte[bits.Length * 4];
        for (var i = 0; i < bits.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), bits[i]);
        }

        return bytes;
    }

    private static bool RepeatsAnIndex(IReadOnlyList<int> indices)
    {
        for (var t = 0; t + 2 < indices.Count; t += 3)
        {
            if (indices[t] == indices[t + 1] || indices[t + 1] == indices[t + 2] || indices[t] == indices[t + 2])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A minimal document around one primitive (for the Shared-validation controls).</summary>
    private static ModelDocument WithPrimitive(ScenePrimitive primitive)
    {
        return new ModelDocument("test", "control", [new SceneDefinition("s", [0])],
            [new SceneNode("n", Matrix4x4.Identity, meshIndex: 0)], [new SceneMesh("m", [primitive])]);
    }

    /// <summary>The primitive's geometry with one replacement attribute.</summary>
    private static ModelDocument WithAttribute(ScenePrimitive primitive, SceneAttributeStream attribute)
    {
        return WithPrimitive(new ScenePrimitive("control", primitive.Vertices, primitive.Indices,
            normalMode: primitive.NormalMode)
        {
            Attributes = [attribute],
            NormalProvenance = primitive.NormalProvenance
        });
    }
}
