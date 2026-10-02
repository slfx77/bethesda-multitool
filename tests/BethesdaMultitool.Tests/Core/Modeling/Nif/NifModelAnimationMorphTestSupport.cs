using System.Numerics;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Fixtures for the cut-1b slice 7 tests (<see cref="NifModelAnimationMorphs" />, <see cref="NifModelAnimationReader" />):
///     20.2.0.7 block bodies for NiGeomMorpherController, NiMorphData, NiFloatInterpolator, NiFloatData,
///     NiBlendFloatInterpolator, NiBSplineCompFloatInterpolator, NiBSplineData and NiBSplineBasisData, written field by
///     field from nif.xml at BS 34 so the reader is never compared with its own decoder; a triangle geometry the morpher
///     targets; and the document the brief assembles (the graph's nodes with the face carrying a morphed mesh, plus the
///     clips), evaluated through Shared's public <see cref="ScenePoseEvaluator" />, the only oracle for sampled weights.
/// </summary>
internal static class NifModelAnimationMorphTestSupport
{
    /// <summary>The Controller Type a morph controlled block names.</summary>
    public const string MorpherController = "NiGeomMorpherController";

    /// <summary>nif.xml's #INV_FLT#, the pose Value of an interpolator with none.</summary>
    public const uint InvalidFloatBits = 0xFF7FFFFF;

    /// <summary>LINEAR key type.</summary>
    public const uint Linear = 1;

    /// <summary>QUADRATIC key type.</summary>
    public const uint Quadratic = 2;

    /// <summary>TBC key type.</summary>
    public const uint Tbc = 3;

    /// <summary>CONST key type.</summary>
    public const uint Constant = 5;

    /// <summary>The face triangle (0,0,0), (1,0,0), (0,1,0).</summary>
    public static readonly float[] Triangle = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f];

    /// <summary>The Base morph: the triangle itself (morph 0 equals the stored positions).</summary>
    public static readonly float[] BaseVectors = Triangle;

    /// <summary>A relative morph: every vertex moved along x.</summary>
    public static readonly float[] SmileVectors = [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f];

    /// <summary>A relative morph: every vertex moved along y.</summary>
    public static readonly float[] FrownVectors = [0f, 2f, 0f, 0f, 2f, 0f, 0f, 2f, 0f];

    /// <summary>
    ///     An NiGeomMorpherController at 20.2.0.7 (nif.xml NiTimeController then NiGeomMorpherController): Next
    ///     Controller, Flags, Frequency, Phase, Start Time, Stop Time, Target, Morpher Flags (0), Data, Always Update (0),
    ///     Num Interpolators, then each MorphWeight (Interpolator ref, Weight).
    /// </summary>
    public static Action<NifTestBlockWriter> Morpher(int target, int data, ushort flags,
        (int Interpolator, float Weight)[] items, float frequency = 1f, float phase = 0f, float start = 0f,
        float stop = 1f, int next = -1)
    {
        return w =>
        {
            NifModelAnimationReaderTestSupport.TimeController(w, next, flags, frequency, phase, start, stop, target);
            w.U16(0).Ref(data).U8(0).U32((uint)items.Length);
            foreach (var (interpolator, weight) in items)
            {
                w.Ref(interpolator).F32(weight);
            }
        };
    }

    /// <summary>An NiMorphData over the three-vertex triangle (nif.xml NiMorphData and Morph, through the 1a layout).</summary>
    public static Action<NifTestBlockWriter> MorphData(byte relativeTargets, params (int Name, float[] Vectors)[] morphs)
    {
        return w => NifTestBlockLayouts.MorphData(w, 3, relativeTargets, morphs);
    }

    /// <summary>An NiFloatInterpolator (nif.xml: Value, Data): the pose Value bits, then the Data ref.</summary>
    public static Action<NifTestBlockWriter> FloatInterpolator(int data, uint valueBits = InvalidFloatBits)
    {
        return w => w.U32(valueBits).Ref(data);
    }

    /// <summary>
    ///     An NiFloatData (nif.xml: one KeyGroup&lt;float&gt;): Num Keys, Interpolation when there are keys, then each key's
    ///     floats exactly as given (time, value; QUADRATIC adds Forward and Backward; TBC adds its three floats).
    /// </summary>
    public static Action<NifTestBlockWriter> FloatData(uint keyType, params float[][] keys)
    {
        return w =>
        {
            w.U32((uint)keys.Length);
            if (keys.Length != 0)
            {
                w.U32(keyType);
            }

            foreach (var key in keys)
            {
                w.F32s(key);
            }
        };
    }

    /// <summary>
    ///     An NiBlendFloatInterpolator in the manager-controlled form (nif.xml NiBlendInterpolator since 10.1.0.112 with
    ///     Flags bit 0 set, so the conditional fields are absent): Flags (1), Array Size (0), Weight Threshold, then the
    ///     NiBlendFloatInterpolator Value (#INV_FLT#).
    /// </summary>
    public static Action<NifTestBlockWriter> BlendFloatInterpolator()
    {
        return static w => w.U8(1).U8(0).F32(0f).U32(InvalidFloatBits);
    }

    /// <summary>
    ///     An NiBSplineCompFloatInterpolator (nif.xml NiBSplineInterpolator, NiBSplineFloatInterpolator,
    ///     NiBSplineCompFloatInterpolator): Start Time, Stop Time, Spline Data, Basis Data, Value bits, Handle, Float
    ///     Offset, Float Half Range.
    /// </summary>
    public static Action<NifTestBlockWriter> CompFloatBspline(float start, float stop, int splineData, int basisData,
        uint handle, float offset, float halfRange, uint valueBits = InvalidFloatBits)
    {
        return w => w.F32(start).F32(stop).Ref(splineData).Ref(basisData).U32(valueBits).U32(handle).F32(offset)
            .F32(halfRange);
    }

    /// <summary>An NiBSplineData with no float controls and the given compact (signed short) controls.</summary>
    public static Action<NifTestBlockWriter> BsplineData(params short[] compact)
    {
        return w =>
        {
            w.U32(0).U32((uint)compact.Length);
            foreach (var control in compact)
            {
                w.U16(unchecked((ushort)control));
            }
        };
    }

    /// <summary>An NiBSplineBasisData: Num Control Points.</summary>
    public static Action<NifTestBlockWriter> BsplineBasis(uint controlPoints)
    {
        return w => w.U32(controlPoints);
    }

    /// <summary>
    ///     Adds the face: an NiTriShape at <paramref name="expectedShape" /> (data at the next index, the given controller)
    ///     and its NiTriShapeData holding <see cref="Triangle" />.
    /// </summary>
    public static void AddFace(NifTestFileBuilder builder, int expectedShape, int nameIndex, int controller)
    {
        Assert.Equal(expectedShape, NifModelTestSupport.AddTriShape(builder, nameIndex, expectedShape + 1, controller));
        Assert.Equal(expectedShape + 1, NifModelTestSupport.AddTriShapeData(builder,
            new NifTestGeometryStreams { Vertices = Triangle }, [0, 1, 2]));
    }

    /// <summary>
    ///     The document the brief assembles: the graph's nodes, with the face node re-created over its authored TRS
    ///     carrying mesh 0, a triangle with <paramref name="targetCount" /> relative morph targets (target t moves the
    ///     vertices by t + 1 along x, y and z), plus the clips.
    /// </summary>
    public static ModelDocument AssembleMorphDocument(NifModelNodeGraph graph, IEnumerable<SceneAnimation> clips,
        int faceNode, int targetCount)
    {
        var nodes = graph.Nodes.ToArray();
        var face = nodes[faceNode];
        Assert.NotNull(face.LocalTrs);
        nodes[faceNode] = new SceneNode(face.Name, face.LocalTrs!.Value, face.Children, meshIndex: 0);
        SceneVertex[] vertices =
        [
            new(Vector3.Zero, Vector3.UnitZ, Vector4.One, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector4.One, Vector2.UnitX),
            new(Vector3.UnitY, Vector3.UnitZ, Vector4.One, Vector2.UnitY)
        ];
        var targets = new SceneMorphTarget[targetCount];
        for (var target = 0; target < targets.Length; target++)
        {
            var amount = target + 1f;
            targets[target] = new SceneMorphTarget($"target{target}",
                [new Vector3(amount, 0f, 0f), new Vector3(0f, amount, 0f), new Vector3(0f, 0f, amount)]);
        }

        var mesh = new SceneMesh("face", [new ScenePrimitive("face", vertices, [0, 1, 2], morphTargets: targets)]);
        return new ModelDocument("nif", "fixture", [new SceneDefinition("scene", graph.RootNodeIndices)], nodes,
            [mesh], animations: clips);
    }

    /// <summary>Evaluates one clip through Shared's public evaluator and returns one node's complete morph weights.</summary>
    public static float[] SampleWeights(ModelDocument document, int clip, float seconds, int node)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaluator = new ScenePoseEvaluator(document, cancellationToken);
        var pose = evaluator.CreateWorkspace(0, cancellationToken);
        evaluator.EvaluateClip(pose, clip, seconds, cancellationToken);
        return pose.GetMorphWeights(node).ToArray();
    }

    /// <summary>The smoothstep a zero-tangent Hermite segment from 0 to 1 traces at the normalized time u.</summary>
    public static double Smoothstep(double u)
    {
        return 3d * u * u - 2d * u * u * u;
    }
}
