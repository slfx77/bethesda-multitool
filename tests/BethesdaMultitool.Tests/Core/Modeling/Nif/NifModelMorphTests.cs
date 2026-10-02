using System.Numerics;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     NiGeomMorpherController -> NiMorphData through the reader: morphs 1..n become targets named by their frame names,
///     Relative Targets 1 gives position deltas and 0 absolute positions, morph 0 is compared bit for bit with the stored
///     positions, and data that cannot be typed exactly stays native with a diagnostic. The controller is the animation
///     stage's (cut-1b slice 10): its stored weights become the (controllers) clip's constant weight tracks where the
///     targets are typed, and it stays native with the slice-10 reason where they are not.
/// </summary>
public class NifModelMorphTests
{
    private static readonly float[] BaseVertices = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f];
    private static readonly float[] Smile = [0.125f, 0f, 0f, 0f, -0.25f, 0f, 0f, 0f, 3.5f];
    private static readonly float[] Frown = [0f, 0f, -1f, 0f, 0f, 0f, 2f, 2f, 2f];

    /// <summary>
    ///     0 Root [1]; 1 NiTriShape "Face" (data 2, controller 3); 2 NiTriShapeData; 3 NiGeomMorpherController (target 1,
    ///     data 4); 4 NiMorphData with morphs Base, Smile, Frown.
    /// </summary>
    private static byte[] Fixture(byte relativeTargets, float[]? morph0 = null, uint? morphVertices = null,
        bool bigEndian = false)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        AddTriShape(builder, builder.AddString("Face"), 2, controller: 3);
        AddTriShapeData(builder, new NifTestGeometryStreams { Vertices = BaseVertices }, [0, 1, 2]);
        builder.AddBlock("NiGeomMorpherController", w => NifTestBlockLayouts.GeomMorpherController(w, 1, 4,
            interpolators: 3));
        var baseName = builder.AddString("Base");
        var smileName = builder.AddString("Smile");
        var frownName = builder.AddString("Frown");
        var count = morphVertices ?? 3;
        builder.AddBlock("NiMorphData", w => NifTestBlockLayouts.MorphData(w, count, relativeTargets,
            (baseName, (morph0 ?? BaseVertices)[..(int)(count * 3)]),
            (smileName, Smile[..(int)(count * 3)]),
            (frownName, Frown[..(int)(count * 3)])));
        return builder.Build();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelativeTargets_BecomePositionDeltas_NamedByFrame(bool bigEndian)
    {
        var result = Read(Fixture(1, bigEndian: bigEndian));
        var document = result.Document;
        var primitive = PrimitiveOf(document, "Face");

        Assert.Equal(["Smile", "Frown"], primitive.MorphTargets.Select(t => t.Name));
        Assert.Equal(Vectors(Smile), primitive.MorphTargets[0].PositionDeltas);
        Assert.Equal(Vectors(Frown), primitive.MorphTargets[1].PositionDeltas);
        Assert.All(primitive.MorphTargets, target => Assert.Null(target.AbsolutePositions));
        Assert.All(primitive.MorphTargets, target => Assert.Null(target.NormalDeltas));
        Assert.Null(document.Meshes[0].MorphWeights);

        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:4").Kind);
        var controller = result.Coverage.GetClassification("block:3");
        Assert.Equal(ModelSourceCoverageKind.Typed, controller.Kind);
        var clip = Assert.Single(document.Animations);
        Assert.Equal(NifModelAnimationReader.ControllersClipName, clip.Name);
        Assert.Equal(new[] { 0, 1 }, clip.MorphTargetTracks.Select(static track => track.TargetIndex));
        var morph = PrimitivePayload(document, 0)["morph"]!;
        Assert.True((bool)morph["typed"]!);
        Assert.Equal(3, (int)morph["controllerBlock"]!);
        Assert.Equal(4, (int)morph["dataBlock"]!);
        Assert.True((bool)morph["morph0MatchesBase"]!);
        Assert.Equal("positionDeltas", (string)morph["targetForm"]!);
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == NifModelMorphReader.BaseMismatchDiagnostic);
        var dataRow = Rows(document, NifModelNativeState.BlockKind)
            .Single(r => r.SourceLocation?.ElementIdentity == "block:4");
        Assert.Equal(new SceneElementRef(SceneElementKind.Mesh, 0), dataRow.Target);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     Relative Targets 0 gives absolute positions with no position deltas and no base subtracted. Control: the same
    ///     vectors read as relative land in PositionDeltas instead.
    /// </summary>
    [Fact]
    public void AbsoluteTargets_BecomeAbsolutePositions()
    {
        var document = Read(Fixture(0)).Document;
        var primitive = PrimitiveOf(document, "Face");

        Assert.Equal(Vectors(Smile), primitive.MorphTargets[0].AbsolutePositions);
        Assert.Equal(Vectors(Frown), primitive.MorphTargets[1].AbsolutePositions);
        Assert.All(primitive.MorphTargets, target => Assert.Empty(target.PositionDeltas));
        Assert.Equal("absolutePositions", (string)PrimitivePayload(document, 0)["morph"]!["targetForm"]!);
        SceneValidation.ValidateStructure(document);

        var relative = PrimitiveOf(Read(Fixture(1)).Document, "Face");
        Assert.Null(relative.MorphTargets[0].AbsolutePositions);
        Assert.Equal(Vectors(Smile), relative.MorphTargets[0].PositionDeltas);
    }

    /// <summary>
    ///     Morph 0 is compared bit for bit with the stored positions: one ulp of difference raises a diagnostic naming
    ///     the vertex, and the targets are still typed. Control: the identical base raises none.
    /// </summary>
    [Fact]
    public void Morph0_DifferingFromTheBase_RaisesADiagnostic()
    {
        var shifted = (float[])BaseVertices.Clone();
        shifted[4] = MathF.BitIncrement(shifted[4]);

        var document = Read(Fixture(1, shifted)).Document;

        var diagnostic = Assert.Single(document.Diagnostics, d => d.Code == NifModelMorphReader.BaseMismatchDiagnostic);
        Assert.Contains("first at vertex 1", DiagnosticText(diagnostic));
        var morph = PrimitivePayload(document, 0)["morph"]!;
        Assert.False((bool)morph["morph0MatchesBase"]!);
        Assert.Equal(1, (int)morph["morph0FirstMismatch"]!);
        Assert.Equal(2, PrimitiveOf(document, "Face").MorphTargets.Count);
        SceneValidation.ValidateStructure(document);

        Assert.DoesNotContain(Read(Fixture(1)).Document.Diagnostics,
            d => d.Code == NifModelMorphReader.BaseMismatchDiagnostic);
    }

    /// <summary>
    ///     Morph data whose vertex count differs from its geometry is not typed: no targets, a diagnostic, and the
    ///     NiMorphData block NativeOnly with that reason. Control: the matching count types (see the relative test).
    /// </summary>
    [Fact]
    public void MorphVertexCount_DifferingFromTheGeometry_IsNotTyped()
    {
        var result = Read(Fixture(1, morphVertices: 2));

        Assert.Empty(PrimitiveOf(result.Document, "Face").MorphTargets);
        var row = result.Coverage.GetClassification("block:4");
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, row.Kind);
        Assert.Equal(NifModelCoverage.MorphVertexCountReason, row.Reason);
        Assert.Contains(result.Document.Diagnostics, d => d.Code == NifModelMorphReader.NotTypedDiagnostic);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:2").Kind);

        // Cut-1b slice 10: with no typed targets on the mesh, the morpher emits no weight channel (Shared would refuse
        // one) and stays native with the slice-10 reason; the relative test above is the control that types it.
        Assert.Empty(result.Document.Animations);
        var controller = result.Coverage.GetClassification("block:3");
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, controller.Kind);
        Assert.Equal(NifModelAnimationReasons.MorphTargetsNotTyped, controller.Reason);
        SceneValidation.ValidateStructure(result.Document);
    }

    /// <summary>A Relative Targets value other than 0 or 1 has no established meaning and is not typed.</summary>
    [Fact]
    public void RelativeTargets_OtherThanZeroOrOne_AreNotTyped()
    {
        var result = Read(Fixture(2));

        Assert.Empty(PrimitiveOf(result.Document, "Face").MorphTargets);
        Assert.Equal(NifModelCoverage.MorphRelativeTargetsReason, result.Coverage.GetClassification("block:4").Reason);
        SceneValidation.ValidateStructure(result.Document);
    }

    private static Vector3[] Vectors(float[] values)
    {
        var vectors = new Vector3[values.Length / 3];
        for (var i = 0; i < vectors.Length; i++)
        {
            vectors[i] = new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
        }

        return vectors;
    }
}
