using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Fixtures for the cut-1b slice 4 and 6 tests (<see cref="NifModelAnimationReader" />): 20.2.0.7 block bodies for the
///     animation blocks, written field by field from nif.xml (NiTimeController, NiControllerManager, NiControllerSequence
///     and its ControlledBlock, NiTransformInterpolator, NiTransformData, NiTransformController,
///     NiMultiTargetTransformController, NiTextKeyExtraData) at BS 34, so the reader is never compared with its own decoder;
///     a <c>.kf</c> read into a <see cref="NifModelReadState" /> without a node walk; and the document the brief assembles
///     (the graph's nodes plus the clips).
/// </summary>
internal static class NifModelAnimationReaderTestSupport
{
    /// <summary>The BS version of every fixture (FNV).</summary>
    public const uint Bs = 34;

    /// <summary>NiTimeController flags bit 3, active.</summary>
    public const ushort Active = 0x08;

    /// <summary>NiTimeController flags bit 5, manager controlled.</summary>
    public const ushort ManagerControlled = 0x20;

    /// <summary>The CLAMP cycle in flags bits 1-2.</summary>
    public const ushort ClampCycle = 2 << 1;

    /// <summary>The REVERSE cycle in flags bits 1-2.</summary>
    public const ushort ReverseCycle = 1 << 1;

    /// <summary>The Controller Type every transform controlled block names.</summary>
    public const string TransformController = "NiTransformController";

    /// <summary>
    ///     One controlled block as a fixture writes it (nif.xml ControlledBlock at 20.2.0.7 on a Bethesda stream: Interpolator,
    ///     Controller, Priority, then five string indices).
    /// </summary>
    public static (int Interpolator, int Controller, byte Priority, int Node, int PropertyType, int ControllerType,
        int ControllerId, int InterpolatorId) Controlled(int interpolator, int node, int controllerType,
            byte priority = 26, int controller = -1, int propertyType = -1, int controllerId = -1,
            int interpolatorId = -1)
    {
        return (interpolator, controller, priority, node, propertyType, controllerType, controllerId, interpolatorId);
    }

    /// <summary>Adds a block and asserts it landed at the index the fixture's forward references assume.</summary>
    public static void Add(NifTestFileBuilder builder, int expectedIndex, string type, Action<NifTestBlockWriter> write)
    {
        Assert.Equal(expectedIndex, builder.AddBlock(type, write));
    }

    /// <summary>A NiNode body: identity transform, the given children and controller.</summary>
    public static Action<NifTestBlockWriter> Node(int name, int[] children, int controller = -1)
    {
        return w => NifTestBlockLayouts.NodeWithTail(w, Bs, name, children, static _ => { }, controller: controller);
    }

    /// <summary>
    ///     The NiTimeController fields (nif.xml:9494-9527): Next Controller, Flags (ushort), Frequency, Phase, Start Time,
    ///     Stop Time, Target.
    /// </summary>
    public static void TimeController(NifTestBlockWriter w, int next, ushort flags, float frequency, float phase,
        float start, float stop, int target)
    {
        w.Ref(next).U16(flags).F32s(frequency, phase, start, stop).Ref(target);
    }

    /// <summary>
    ///     An NiControllerManager (nif.xml:10566-10581): NiTimeController, Cumulative (bool), Num Controller Sequences, the
    ///     sequence refs, Object Palette.
    /// </summary>
    public static Action<NifTestBlockWriter> Manager(int target, int[] sequences, int palette,
        ushort flags = Active | ClampCycle, int next = -1)
    {
        return w =>
        {
            TimeController(w, next, flags, 1f, 0f, 0f, 0f, target);
            w.Bool(false).U32((uint)sequences.Length);
            foreach (var sequence in sequences)
            {
                w.Ref(sequence);
            }

            w.Ref(palette);
        };
    }

    /// <summary>
    ///     An NiMultiTargetTransformController (nif.xml:9537-9546): NiTimeController (NiInterpController adds nothing at
    ///     20.2.0.7), Num Extra Targets (ushort) = 0.
    /// </summary>
    public static Action<NifTestBlockWriter> MultiTarget(int target, ushort flags, int next = -1)
    {
        return w =>
        {
            TimeController(w, next, flags, 1f, 0f, 0f, 1f, target);
            w.U16(0);
        };
    }

    /// <summary>
    ///     An NiControllerSequence at 20.2.0.7, BS 34 (nif.xml:10582-10651): Name, Num Controlled Blocks, Array Grow By, the
    ///     controlled blocks, Weight, Text Keys, Cycle Type (uint), Frequency, Start Time, Stop Time, Manager, Accum Root
    ///     Name, then Num Anim Note Arrays (ushort) = 0 because BS &gt; 28.
    /// </summary>
    public static Action<NifTestBlockWriter> Sequence(
        int name,
        (int Interpolator, int Controller, byte Priority, int Node, int PropertyType, int ControllerType,
            int ControllerId, int InterpolatorId)[] blocks,
        int textKeys = -1,
        int manager = -1,
        int accumRoot = -1,
        float weight = 1f,
        uint cycle = 2,
        float start = 0f,
        float stop = 1f)
    {
        return w =>
        {
            w.StringIndex(name).U32((uint)blocks.Length).U32(1);
            foreach (var block in blocks)
            {
                w.Ref(block.Interpolator).Ref(block.Controller).U8(block.Priority)
                    .StringIndex(block.Node).StringIndex(block.PropertyType).StringIndex(block.ControllerType)
                    .StringIndex(block.ControllerId).StringIndex(block.InterpolatorId);
            }

            w.F32(weight).Ref(textKeys).U32(cycle).F32(1f).F32(start).F32(stop).Ref(manager).StringIndex(accumRoot);
            w.U16(0);
        };
    }

    /// <summary>
    ///     An NiTransformInterpolator: the static NiQuatTransform (translation 0, 0, 0; rotation w, x, y, z = 1, 0, 0, 0;
    ///     scale 1) and the Data ref.
    /// </summary>
    public static Action<NifTestBlockWriter> TransformInterpolator(int data)
    {
        return w => w.F32s(0f, 0f, 0f).F32s(1f, 0f, 0f, 0f).F32(1f).Ref(data);
    }

    /// <summary>
    ///     An NiTransformData: the rotation part the given writer lays out, a LINEAR translation KeyGroup (Num Keys,
    ///     Interpolation when there are keys, then time, x, y, z per key), and an empty scale KeyGroup.
    /// </summary>
    public static Action<NifTestBlockWriter> TransformData(Action<NifTestBlockWriter> rotation,
        params (float Time, float X, float Y, float Z)[] translations)
    {
        return w =>
        {
            rotation(w);
            w.U32((uint)translations.Length);
            if (translations.Length != 0)
            {
                w.U32(1);
                foreach (var key in translations)
                {
                    w.F32s(key.Time, key.X, key.Y, key.Z);
                }
            }

            w.U32(0);
        };
    }

    /// <summary>A rotation part with no keys (Num Rotation Keys = 0).</summary>
    public static void NoRotation(NifTestBlockWriter w)
    {
        w.U32(0);
    }

    /// <summary>A LINEAR quaternion rotation part: Num Rotation Keys, Rotation Type 1, then time, w, x, y, z per key.</summary>
    public static Action<NifTestBlockWriter> LinearRotation(params (float Time, float W, float X, float Y, float Z)[] keys)
    {
        return w =>
        {
            w.U32((uint)keys.Length).U32(1);
            foreach (var key in keys)
            {
                w.F32s(key.Time, key.W, key.X, key.Y, key.Z);
            }
        };
    }

    /// <summary>
    ///     An XYZ_ROTATION rotation part at 20.2.0.7 (no legacy word): one NiEulerRotKey record, Rotation Type 4, then the
    ///     X axis as a LINEAR float KeyGroup and empty Y and Z KeyGroups.
    /// </summary>
    public static Action<NifTestBlockWriter> EulerRotation(params (float Time, float Angle)[] xKeys)
    {
        return w =>
        {
            w.U32(1).U32(4);
            w.U32((uint)xKeys.Length).U32(1);
            foreach (var key in xKeys)
            {
                w.F32s(key.Time, key.Angle);
            }

            w.U32(0).U32(0);
        };
    }

    /// <summary>An NiTransformController: the NiTimeController fields, then Interpolator (nif.xml NiSingleInterpController).</summary>
    public static Action<NifTestBlockWriter> TransformControllerBlock(int target, int interpolator, ushort flags,
        float frequency = 1f, float phase = 0f, float start = 0f, float stop = 1f, int next = -1)
    {
        return w =>
        {
            TimeController(w, next, flags, frequency, phase, start, stop, target);
            w.Ref(interpolator);
        };
    }

    /// <summary>An NiTextKeyExtraData at 20.2.0.7: Name (-1), Num Text Keys, then time and label index per key.</summary>
    public static Action<NifTestBlockWriter> TextKeys(params (float Time, int Label)[] keys)
    {
        return w =>
        {
            w.StringIndex(-1).U32((uint)keys.Length);
            foreach (var key in keys)
            {
                w.F32(key.Time).StringIndex(key.Label);
            }
        };
    }

    /// <summary>
    ///     Reads a built <c>.kf</c> into a read state as NifModelReader does before its sub-readers run (NiNode and geometry
    ///     blocks strict, every other block tolerant); a <c>.kf</c>'s roots are sequences, so no node graph is walked.
    /// </summary>
    public static NifModelReadState ReadState(byte[] bytes)
    {
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        var schema = NifSchema.LoadEmbedded();
        var decoder = new NifBlockDecoder(schema, info, bytes);
        var footer = decoder.ValidateLayout();
        var blocks = new NifDecodedBlock[info.Blocks.Count];
        for (var i = 0; i < blocks.Length; i++)
        {
            var type = info.Blocks[i].TypeName;
            var mode = schema.Inherits(type, "NiNode") || NifModelGeometryReader.IsStrictType(type)
                ? NifDecodeMode.Strict
                : NifDecodeMode.Tolerant;
            blocks[i] = decoder.Decode(i, mode);
        }

        var (item, input) = NifModelTestSupport.Open(bytes, "meshes/test/animation.kf");
        input.Dispose();
        return new NifModelReadState(item, ModelNativeDetail.Metadata, bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), info, schema, decoder, footer, blocks);
    }

    /// <summary>The document the brief assembles: the graph's nodes (one scene over its roots) plus the clips.</summary>
    public static ModelDocument Assemble(NifModelNodeGraph graph, IEnumerable<SceneAnimation> clips)
    {
        return new ModelDocument("nif", "fixture", [new SceneDefinition("scene", graph.RootNodeIndices)], graph.Nodes,
            [], animations: clips);
    }

    /// <summary>The clip object inside a clip's ExtrasJson.</summary>
    public static JsonNode Extras(SceneAnimation clip)
    {
        Assert.NotNull(clip.ExtrasJson);
        return JsonNode.Parse(clip.ExtrasJson)![NifModelAnimationExtras.Key]!;
    }

    /// <summary>Every decision the reader made about one block, in order.</summary>
    public static NifModelAnimationDisposition[] DecisionsFor(NifModelAnimationResult result, int block)
    {
        return result.Decisions.Where(decision => decision.Block == block).ToArray();
    }

    /// <summary>Evaluates one clip through Shared's public evaluator and returns one node's local matrix.</summary>
    public static Matrix4x4 SampleLocal(ModelDocument document, int clip, float seconds, int node)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaluator = new ScenePoseEvaluator(document, cancellationToken);
        var pose = evaluator.CreateWorkspace(0, cancellationToken);
        evaluator.EvaluateClip(pose, clip, seconds, cancellationToken);
        return pose.LocalMatrices.Span[node];
    }
}
