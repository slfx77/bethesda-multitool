using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Cut-1b slice 10, the D5 follow-up: the renderer's <see cref="NifGeometryMorphReader" /> now reads through the
///     slice-7 morph views (one decode path with the model reader), and its output must not change. This pins the
///     complete output on one synthetic file, parsed by NifParser from bytes written field by field, with every expected
///     value stated independently of either implementation, so it passes against the pre-view reader and the projection
///     alike. Controls: one changed stored tangent changes the output (the pin can fail), and the big-endian twin stays
///     refused (the renderer's little-endian admission rule survives the re-pointing).
/// </summary>
public sealed class NifGeometryMorphReaderViewTests
{
    private static readonly float[] Base = [10f, 20f, 30f, 11f, 20f, 30f, 10f, 21f, 30f];
    private static readonly float[] Delta = [2f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, -1f];

    /// <summary>The pinned output: shape, clock, loop flag, both targets' names, vectors and curves, and one sample.</summary>
    [Fact]
    public void RendererMorphOutput_IsPinnedOnASyntheticFile()
    {
        var (bytes, nif) = Parse(Fixture(false, 2f));

        Assert.True(NifGeometryMorphReader.TryRead(bytes, nif, nif.Blocks[3], out var morph));

        Assert.Equal(1, morph.SourceBlockIndex);
        Assert.Equal(1f, morph.Frequency);
        Assert.Equal(0f, morph.Phase);
        Assert.Equal(0f, morph.StartTime);
        Assert.Equal(2f, morph.StopTime);
        Assert.True(morph.Loops);
        Assert.Equal(2, morph.Targets.Length);
        Assert.Equal(3, morph.VertexCount);

        var baseTarget = morph.Targets[0];
        Assert.Equal("Base", baseTarget.Name);
        Assert.Equal(Vectors(Base), baseTarget.Positions);
        Assert.Equal(NifKeyInterpolation.Linear, baseTarget.Curve.Interpolation);
        Assert.Equal(0f, baseTarget.Curve.FallbackWeight);
        Assert.Empty(baseTarget.Curve.Keys);

        var delta = morph.Targets[1];
        Assert.Equal("Delta", delta.Name);
        Assert.Equal(Vectors(Delta), delta.Positions);
        Assert.Equal(NifKeyInterpolation.Quadratic, delta.Curve.Interpolation);
        Assert.Equal(0.25f, delta.Curve.FallbackWeight);
        Assert.Equal(
            new[] { new NifMorphScalarKey(0f, 0f, 0f, 2f), new NifMorphScalarKey(2f, 1f, 1.5f, 0f) },
            delta.Curve.Keys);
        Assert.Equal(0.5625f, NifGeometryMorphEvaluator.Sample(delta.Curve, 1f));
    }

    /// <summary>Control: key 0's stored Backward 3 instead of 2 reaches the output, so the pin above can fail.</summary>
    [Fact]
    public void RendererMorphOutput_FollowsTheStoredTangent()
    {
        var (bytes, nif) = Parse(Fixture(false, 3f));

        Assert.True(NifGeometryMorphReader.TryRead(bytes, nif, nif.Blocks[3], out var morph));

        Assert.Equal(3f, morph.Targets[1].Curve.Keys[0].OutTangent);
        Assert.NotEqual(0.5625f, NifGeometryMorphEvaluator.Sample(morph.Targets[1].Curve, 1f));
    }

    /// <summary>Control: the big-endian twin is refused (the renderer's admission rule), while the views read it.</summary>
    [Fact]
    public void RendererMorphReader_KeepsItsLittleEndianRule()
    {
        var (bytes, nif) = Parse(Fixture(true, 2f));

        Assert.False(NifGeometryMorphReader.TryRead(bytes, nif, nif.Blocks[3], out _));
        Assert.True(NifGeomMorpherReader.TryReadControllerView(bytes, nif, nif.Blocks[3], out var view));
        Assert.Equal(2, view.Items.Length);
    }

    /// <summary>
    ///     0 NiNode Root [1]; 1 NiTriShape Flag (data 2, controller 3); 2 NiTriShapeData (<see cref="Base" />); 3
    ///     NiGeomMorpherController (flags 72, clock 1/0/0/2, target 1, data 4, items (-1, 0) and (5, 0.25)); 4 NiMorphData
    ///     (Base, Delta; relative); 5 NiFloatInterpolator (0.5, data 6); 6 NiFloatData QUADRATIC (0, 0, fwd 0, bwd
    ///     <paramref name="backward0" />), (2, 1, fwd 1.5, bwd 0).
    /// </summary>
    private static byte[] Fixture(bool bigEndian, float backward0)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        var root = builder.AddString("Root");
        var flag = builder.AddString("Flag");
        var baseName = builder.AddString("Base");
        var deltaName = builder.AddString("Delta");
        Assert.Equal(0, NifModelTestSupport.AddNode(builder, root, [1]));
        Assert.Equal(1, NifModelTestSupport.AddTriShape(builder, flag, 2, controller: 3));
        Assert.Equal(2, NifModelTestSupport.AddTriShapeData(builder, new NifTestGeometryStreams { Vertices = Base },
            [0, 1, 2]));
        Assert.Equal(3, builder.AddBlock("NiGeomMorpherController", w =>
        {
            NifModelAnimationReaderTestSupport.TimeController(w, -1, 72, 1f, 0f, 0f, 2f, 1);
            w.U16(0).Ref(4).U8(0).U32(2);
            w.Ref(-1).F32(0f);
            w.Ref(5).F32(0.25f);
        }));
        Assert.Equal(4, builder.AddBlock("NiMorphData",
            w => NifTestBlockLayouts.MorphData(w, 3, 1, (baseName, Base), (deltaName, Delta))));
        Assert.Equal(5, builder.AddBlock("NiFloatInterpolator", w => w.F32(0.5f).Ref(6)));
        Assert.Equal(6, builder.AddBlock("NiFloatData", NifModelAnimationMorphTestSupport.FloatData(
            NifModelAnimationMorphTestSupport.Quadratic, [0f, 0f, 0f, backward0], [2f, 1f, 1.5f, 0f])));
        return builder.Build();
    }

    private static (byte[] Bytes, NifInfo Nif) Parse(byte[] bytes)
    {
        var nif = NifParser.Parse(bytes);
        Assert.NotNull(nif);
        return (bytes, nif);
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
