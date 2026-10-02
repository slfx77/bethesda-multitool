using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     How slice 3 classifies geometry it does not type (empty geometry, console packed streams, PC additional streams)
///     and the corrupt geometry it refuses. Every "not typed" case has a control that differs in exactly the one
///     property under test and is typed.
/// </summary>
public class NifModelGeometryCoverageTests
{
    private static readonly float[] Triangle = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f];

    public static TheoryData<string> EmptyForms => new() { "no triangles", "all triangles repeat", "no vertices" };

    /// <summary>
    ///     A geometry that yields no triangle or no vertex emits no primitive (Shared rejects empty ones): the shape stays a
    ///     placed node without a mesh, and the shape and its data are NativeOnly "no drawable triangles". Control: the
    ///     same fixture with one drawable triangle is typed.
    /// </summary>
    [Theory]
    [MemberData(nameof(EmptyForms))]
    public void EmptyGeometry_EmitsNoPrimitive_AndIsNativeOnly(string form)
    {
        var result = Read(EmptyFixture(form, drawable: false));

        Assert.Empty(result.Document.Meshes);
        var shape = result.Document.Nodes.Single(n => n.Name == "Shape");
        Assert.Null(shape.MeshIndex);
        AssertNativeOnly(result.Coverage, "block:1", NifModelCoverage.EmptyGeometryReason);
        AssertNativeOnly(result.Coverage, "block:2", NifModelCoverage.EmptyGeometryReason);
        Assert.Empty(Rows(result.Document, NifModelGeometryReader.PrimitiveKind));
        SceneValidation.ValidateStructure(result.Document);

        var control = Read(EmptyFixture(form, drawable: true));
        Assert.Single(control.Document.Meshes);
        Assert.Equal(ModelSourceCoverageKind.Typed, control.Coverage.GetClassification("block:1").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, control.Coverage.GetClassification("block:2").Kind);
        SceneValidation.ValidateStructure(control.Document);
    }

    /// <summary>A placed geometry with no data link is NativeOnly "no drawable triangles" and keeps its node.</summary>
    [Fact]
    public void Geometry_WithoutData_IsNativeOnly_AndKeepsItsNode()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        AddTriShape(builder, builder.AddString("Shape"));

        var result = Read(builder.Build());

        Assert.Equal(["Root", "Shape"], result.Document.Nodes.Select(n => n.Name));
        AssertNativeOnly(result.Coverage, "block:1", NifModelCoverage.EmptyGeometryReason);
        SceneValidation.ValidateStructure(result.Document);
    }

    /// <summary>
    ///     Geometry whose data links a BSPackedAdditionalGeometryData with no channel is console packed geometry of an
    ///     unknown layout: the shape, its data and the packed block are NativeOnly "packed layout unknown" (the inline
    ///     streams beside it are not used, and no layout is guessed from a vector length). Control: the identical bytes
    ///     with the additional block typed as NiAdditionalGeometryData (the PC LOD form) type the inline streams, and
    ///     only the additional block stays NativeOnly. The known layouts are typed in NifModelPackedGeometryTests.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackedGeometry_OfUnknownLayout_IsNativeOnly(bool bigEndian)
    {
        var packed = Read(AdditionalFixture(bigEndian, "BSPackedAdditionalGeometryData"));

        Assert.Empty(packed.Document.Meshes);
        AssertNativeOnly(packed.Coverage, "block:1", NifModelCoverage.PackedLayoutUnknownReason);
        AssertNativeOnly(packed.Coverage, "block:2", NifModelCoverage.PackedLayoutUnknownReason);
        AssertNativeOnly(packed.Coverage, "block:3", NifModelCoverage.PackedLayoutUnknownReason);
        SceneValidation.ValidateStructure(packed.Document);

        var additional = Read(AdditionalFixture(bigEndian, "NiAdditionalGeometryData"));
        Assert.Single(additional.Document.Meshes);
        Assert.Equal(ModelSourceCoverageKind.Typed, additional.Coverage.GetClassification("block:1").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, additional.Coverage.GetClassification("block:2").Kind);
        AssertNativeOnly(additional.Coverage, "block:3", NifModelCoverage.AdditionalGeometryReason);
        var payload = PrimitivePayload(additional.Document, 0);
        Assert.Equal(3, (int)payload["additionalData"]!);
        SceneValidation.ValidateStructure(additional.Document);
    }

    /// <summary>
    ///     The console form proper: Has Vertices 0 with the streams in a packed block that declares no channel. Still
    ///     NativeOnly with the packed reason, never an empty "no vertices" geometry, because the packed link is checked
    ///     first.
    /// </summary>
    [Fact]
    public void PackedGeometry_WithoutInlineVertices_IsPackedNotEmpty()
    {
        var builder = new NifTestFileBuilder(true, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        AddTriShape(builder, builder.AddString("Shape"), 2);
        AddTriShapeData(builder, new NifTestGeometryStreams
        {
            Vertices = [],
            HasVertices = false,
            NumVertices = 3,
            AdditionalData = 3
        }, [0, 1, 2]);
        builder.AddBlock("BSPackedAdditionalGeometryData", w => NifTestBlockLayouts.EmptyAdditionalGeometryData(w, 3));

        var result = Read(builder.Build());

        AssertNativeOnly(result.Coverage, "block:1", NifModelCoverage.PackedLayoutUnknownReason);
        AssertNativeOnly(result.Coverage, "block:2", NifModelCoverage.PackedLayoutUnknownReason);
        SceneValidation.ValidateStructure(result.Document);
    }

    /// <summary>
    ///     Morph data that is reachable (here through a morpher controller on the root node) but that no placed geometry
    ///     uses is NativeOnly "unreferenced by placed geometry"; the controller is the animation stage's (cut-1b slice 10):
    ///     its target (the root node) draws no mesh carrying the morph targets, so no weight channel can address them and
    ///     it stays native with that reason, not the retired 'later-cut(1b)' one.
    /// </summary>
    [Fact]
    public void MorphData_NotUsedByPlacedGeometry_IsUnreferenced()
    {
        var builder = new NifTestFileBuilder(false, 34);
        var rootName = builder.AddString("Root");
        builder.AddBlock("NiNode", w =>
        {
            NifTestBlockLayouts.ObjectNet(w, rootName, controller: 1);
            NifTestBlockLayouts.AvObject(w, 34, 0x0E, (0f, 0f, 0f), NifTestBlockLayouts.Identity, 1f);
            NifTestBlockLayouts.NodeTail(w, []);
        });
        builder.AddBlock("NiGeomMorpherController", w => NifTestBlockLayouts.GeomMorpherController(w, 0, 2));
        builder.AddBlock("NiMorphData", w => NifTestBlockLayouts.MorphData(w, 3, 1, (-1, Triangle), (-1, Triangle)));

        var result = Read(builder.Build());

        AssertNativeOnly(result.Coverage, "block:1", NifModelAnimationReasons.MorphTargetsNotTyped);
        Assert.Empty(result.Document.Animations);
        AssertNativeOnly(result.Coverage, "block:2", NifModelCoverage.UnusedGeometryDataReason);
        Assert.Empty(result.Document.Meshes);
        SceneValidation.ValidateStructure(result.Document);
    }

    /// <summary>A triangle index at or beyond Num Vertices is corrupt input naming the block. Control: index 2 reads.</summary>
    [Fact]
    public void TriangleIndex_BeyondTheVertexCount_IsCorrupt()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            Read(SingleTriShape(new NifTestGeometryStreams { Vertices = Triangle }, [0, 1, 3])));

        Assert.Contains("NIF block 2 (NiTriShapeData)", error.Message);
        Assert.Contains("Num Vertices (3)", error.Message);
        Assert.Single(Read(SingleTriShape(new NifTestGeometryStreams { Vertices = Triangle }, [0, 1, 2]))
            .Document.Meshes);
    }

    /// <summary>
    ///     A non-finite position is corrupt input, reported with its block, field and offset before Shared's validation
    ///     would reject it. Control: the finite fixture reads.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonFinitePosition_IsCorrupt_WithItsOffset(bool bigEndian)
    {
        float[] vertices = [0f, 0f, 0f, 1f, float.NaN, 0f, 0f, 1f, 0f];

        var error = Assert.Throws<InvalidDataException>(() =>
            Read(SingleTriShape(new NifTestGeometryStreams { Vertices = vertices }, [0, 1, 2], bigEndian)));

        Assert.Contains("field 'Vertices' element 1 component 1", error.Message);
        Assert.Contains("offset 0x", error.Message);
        Assert.Single(Read(SingleTriShape(new NifTestGeometryStreams { Vertices = Triangle }, [0, 1, 2], bigEndian))
            .Document.Meshes);
    }

    /// <summary>A NiTriStrips that links NiTriShapeData is corrupt input. Control: a NiTriShape linking it reads.</summary>
    [Fact]
    public void Strips_LinkingShapeData_AreCorrupt()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        AddTriStrips(builder, builder.AddString("Strips"), 2);
        AddTriShapeData(builder, new NifTestGeometryStreams { Vertices = Triangle }, [0, 1, 2]);

        var error = Assert.Throws<InvalidDataException>(() => Read(builder.Build()));

        Assert.Contains("requires NiTriStripsData", error.Message);
        Assert.Single(Read(SingleTriShape(new NifTestGeometryStreams { Vertices = Triangle }, [0, 1, 2]))
            .Document.Meshes);
    }

    private static byte[] EmptyFixture(string form, bool drawable)
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        AddTriShape(builder, builder.AddString("Shape"), 2);
        switch (form)
        {
            case "no triangles":
                AddTriShapeData(builder, new NifTestGeometryStreams { Vertices = Triangle }, [0, 1, 2],
                    hasTriangles: drawable);
                break;
            case "all triangles repeat":
                ushort[] oneDrawable = [0, 1, 1, 0, 1, 2];
                ushort[] allRepeat = [0, 1, 1, 2, 2, 0];
                AddTriShapeData(builder, new NifTestGeometryStreams { Vertices = Triangle },
                    drawable ? oneDrawable : allRepeat);
                break;
            default:
                AddTriShapeData(builder, new NifTestGeometryStreams
                {
                    Vertices = drawable ? Triangle : Array.Empty<float>(),
                    HasVertices = drawable,
                    NumVertices = 3
                }, [0, 1, 2]);
                break;
        }

        return builder.Build();
    }

    /// <summary>0 Root [1]; 1 NiTriShape (data 2); 2 NiTriShapeData (Additional Data 3); 3 an empty additional block.</summary>
    private static byte[] AdditionalFixture(bool bigEndian, string additionalType)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        AddTriShape(builder, builder.AddString("Shape"), 2);
        AddTriShapeData(builder, new NifTestGeometryStreams { Vertices = Triangle, AdditionalData = 3 }, [0, 1, 2]);
        builder.AddBlock(additionalType, w => NifTestBlockLayouts.EmptyAdditionalGeometryData(w, 3));
        return builder.Build();
    }

    private static void AssertNativeOnly(ModelSourceCoverage coverage, string identity, string reason)
    {
        var row = coverage.GetClassification(identity);
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, row.Kind);
        Assert.Equal(reason, row.Reason);
    }
}
