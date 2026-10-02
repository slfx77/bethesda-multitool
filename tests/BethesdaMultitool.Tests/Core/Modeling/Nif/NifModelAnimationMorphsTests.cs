using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationMorphTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationReaderTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTargetTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 7, morph weights (<see cref="NifModelAnimationMorphs" />, <see cref="NifModelMorphTracks" /> through
///     <see cref="NifModelAnimationReader" />; plan section 1.8, RE-23, RE-21 step 7, the SA5 contract at Shared 68335d3):
///     synthetic NIFs written field by field, read into the read state and node graph the way NifModelReader builds
///     them, assembled into clips, and sampled through Shared's public <see cref="ScenePoseEvaluator" />, the only
///     oracle for weights. Every test carries a control that fails.
/// </summary>
public sealed class NifModelAnimationMorphsTests
{
    private const int FaceNode = 1;
    private const int MorpherBlock = 3;
    private static readonly float[] SampleTimes = [0.25f, 0.5f, 0.75f, 1.5f];

    /// <summary>
    ///     Two targets keyed on DIFFERENT time vectors and interpolations (Smile LINEAR over [0, 1], Frown QUADRATIC with
    ///     zero tangents over [0, 2]) give one <see cref="SceneMorphTargetTrack" /> each and no whole-vector track; the
    ///     assembled document validates and the evaluator samples each target's own curve at every time (the linear ramp
    ///     held at 1 past its last key; the Hermite smoothstep). The Base interpolator and its data stay native with the
    ///     RE-23 reason. Control: the two targets' curves swapped give different weights.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmbeddedMorpher_DifferentTimesAndInterpolations_OnePerTargetTrackEach(bool bigEndian)
    {
        var (state, graph) = ReadGraph(EmbeddedFixture(bigEndian,
            FloatData(Linear, [0f, 0f], [1f, 1f]),
            FloatData(Quadratic, [0f, 0f, 0f, 0f], [2f, 1f, 0f, 0f])));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        Assert.Equal(NifModelAnimationReader.ControllersClipName, clip.Name);
        Assert.Empty(clip.MorphTracks);
        Assert.Empty(clip.TransformTracks);
        Assert.Equal(2, clip.MorphTargetTracks.Count);
        Assert.Equal(new[] { 0, 1 }, clip.MorphTargetTracks.Select(static track => track.TargetIndex));
        Assert.All(clip.MorphTargetTracks, static track =>
        {
            Assert.Equal(FaceNode, track.NodeIndex);
            Assert.NotNull(track.Clock);
            Assert.Equal(SceneAnimationChannelState.Keyed, track.Weight.State);
        });
        Assert.Equal(SceneInterpolation.Linear, clip.MorphTargetTracks[0].Weight.Interpolation);
        Assert.Equal(new[] { 0f, 1f }, clip.MorphTargetTracks[0].Weight.Times);
        Assert.Equal(SceneInterpolation.Hermite, clip.MorphTargetTracks[1].Weight.Interpolation);
        Assert.Equal(new[] { 0f, 2f }, clip.MorphTargetTracks[1].Weight.Times);
        AssertBaseNative(result);
        Assert.True(result.Dispositions[MorpherBlock].IsTyped);
        foreach (var block in new[] { 7, 8, 9, 10 })
        {
            Assert.True(result.Dispositions[block].IsTyped);
        }

        var morphTracks = Extras(clip)["morphTracks"]!.AsArray();
        Assert.Equal(2, morphTracks.Count);
        Assert.Equal("target", morphTracks[0]!["form"]!.GetValue<string>());
        Assert.Equal(MorpherBlock, morphTracks[0]!["controller"]!.GetValue<int>());
        Assert.Equal(7, morphTracks[0]!["interpolator"]!.GetValue<int>());
        Assert.Equal(1, morphTracks[0]!["morph"]!.GetValue<int>());
        Assert.Equal(0, morphTracks[0]!["target"]!.GetValue<int>());
        Assert.Equal(9, morphTracks[1]!["interpolator"]!.GetValue<int>());
        Assert.Equal(1, morphTracks[1]!["target"]!.GetValue<int>());

        var document = AssembleMorphDocument(graph, result.Clips, FaceNode, 2);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        foreach (var time in SampleTimes)
        {
            var weights = SampleWeights(document, 0, time, FaceNode);
            Assert.Equal(2, weights.Length);
            Assert.Equal(Math.Min(time, 1d), weights[0], 1e-6);
            Assert.Equal(Smoothstep(time / 2d), weights[1], 1e-6);
        }

        var swapped = new SceneAnimation(clip.Name, [], morphTargetTracks:
        [
            new SceneMorphTargetTrack(FaceNode, 1, clip.MorphTargetTracks[0].Weight, clip.MorphTargetTracks[0].Clock),
            new SceneMorphTargetTrack(FaceNode, 0, clip.MorphTargetTracks[1].Weight, clip.MorphTargetTracks[1].Clock)
        ]);
        var swappedDocument = AssembleMorphDocument(graph, [swapped], FaceNode, 2);
        var control = SampleWeights(swappedDocument, 0, 0.25f, FaceNode);
        Assert.True(Math.Abs(control[0] - 0.25d) > 0.1d, $"The swapped control still samples the ramp: {control[0]}.");
        Assert.True(Math.Abs(control[1] - Smoothstep(0.125d)) > 0.1d);
    }

    /// <summary>
    ///     Two targets keyed with IDENTICAL time bits and one key type (both LINEAR over [0, 1]) merge into one
    ///     whole-vector <see cref="SceneMorphTrack" /> with the weights interleaved key-major, and no per-target track;
    ///     the evaluator samples both. The Base stays native with the RE-23 reason. Control: one changed time bit in the
    ///     second target (1.0 to the next float) makes the clip per-target.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmbeddedMorpher_IdenticalTimeBitsAndKeyType_OneWholeVectorTrack(bool bigEndian)
    {
        var (state, graph) = ReadGraph(EmbeddedFixture(bigEndian,
            FloatData(Linear, [0f, 0f], [1f, 1f]),
            FloatData(Linear, [0f, 0.5f], [1f, 0.25f])));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        Assert.Empty(clip.MorphTargetTracks);
        var vector = Assert.Single(clip.MorphTracks);
        Assert.Equal(FaceNode, vector.NodeIndex);
        Assert.Equal(2, vector.TargetCount);
        Assert.Equal(new[] { 0f, 1f }, vector.Times);
        Assert.Equal(new[] { 0f, 0.5f, 1f, 0.25f }, vector.Weights);
        Assert.Equal(SceneInterpolation.Linear, vector.Interpolation);
        Assert.NotNull(vector.Clock);
        AssertBaseNative(result);
        var entry = Assert.Single(Extras(clip)["morphTracks"]!.AsArray())!;
        Assert.Equal("vector", entry["form"]!.GetValue<string>());
        Assert.Equal(new[] { 1, 2 }, entry["slots"]!.AsArray().Select(static slot => slot!["morph"]!.GetValue<int>()));
        Assert.Equal(new[] { 7, 9 },
            entry["slots"]!.AsArray().Select(static slot => slot!["interpolator"]!.GetValue<int>()));

        var document = AssembleMorphDocument(graph, result.Clips, FaceNode, 2);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        var weights = SampleWeights(document, 0, 0.5f, FaceNode);
        Assert.Equal(0.5d, weights[0], 1e-6);
        Assert.Equal(0.375d, weights[1], 1e-6);

        var (controlState, controlGraph) = ReadGraph(EmbeddedFixture(bigEndian,
            FloatData(Linear, [0f, 0f], [1f, 1f]),
            FloatData(Linear, [0f, 0.5f], [MathF.BitIncrement(1f), 0.25f])));
        var control = NifModelAnimationReader.ReadNif(controlState, controlGraph,
            TestContext.Current.CancellationToken);
        var controlClip = Assert.Single(control.Clips);
        Assert.Empty(controlClip.MorphTracks);
        Assert.Equal(2, controlClip.MorphTargetTracks.Count);
        AssertBaseNative(control);
    }

    /// <summary>
    ///     A sequence's morph controlled blocks bind by Frame Name: 'Smile' and 'Frown' become per-target tracks (their
    ///     times differ) on the face node with the block's priority, 'Base' stays native with the RE-23 reason, and the
    ///     manager-controlled morpher itself stays native; the document validates and samples. Control: the misspelled
    ///     'Smlie' does not bind (native 'interpolator ID matches no Frame Name') and only target 1 is driven.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SequenceMorph_BindsByFrameName(bool bigEndian)
    {
        var (state, graph) = ReadGraph(SequenceFixture(bigEndian, "Smile", "Frown", "Base"));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        Assert.Equal("Talk", clip.Name);
        Assert.Empty(clip.MorphTracks);
        Assert.Equal(2, clip.MorphTargetTracks.Count);
        Assert.Equal(new[] { 0, 1 }, clip.MorphTargetTracks.Select(static track => track.TargetIndex));
        Assert.All(clip.MorphTargetTracks, static track =>
        {
            Assert.Equal(FaceNode, track.NodeIndex);
            Assert.Null(track.Clock);
            Assert.Equal(26, track.SourcePolicy!.Priority);
        });
        Assert.Equal(new[] { 0f, 1f }, clip.MorphTargetTracks[0].Weight.Times);
        Assert.Equal(new[] { 0f, 0.5f }, clip.MorphTargetTracks[1].Weight.Times);
        foreach (var block in new[] { 7, 8, 9, 10 })
        {
            Assert.True(result.Dispositions[block].IsTyped);
        }

        var baseDecision = Assert.Single(DecisionsFor(result, 11));
        Assert.Equal(NifModelAnimationReasons.BaseWeight, baseDecision.Reason);
        Assert.Equal(NifModelAnimationReasons.BaseWeightCode, baseDecision.Code);
        Assert.Equal(2, baseDecision.ControlledBlock);
        Assert.Equal(5, baseDecision.SourceBlock);
        Assert.StartsWith("Base weight: no effect under relative targets (RE-23", baseDecision.Reason,
            StringComparison.Ordinal);
        Assert.Equal(NifModelAnimationReasons.ManagerControlled, result.Dispositions[4].Reason);
        var morphTracks = Extras(clip)["morphTracks"]!.AsArray();
        Assert.Equal(new[] { 0, 1 }, morphTracks.Select(static entry => entry!["controlledBlock"]!.GetValue<int>()));
        Assert.Equal(new[] { 1, 2 }, morphTracks.Select(static entry => entry!["morph"]!.GetValue<int>()));

        var document = AssembleMorphDocument(graph, result.Clips, FaceNode, 2);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        var weights = SampleWeights(document, 0, 0.25f, FaceNode);
        Assert.Equal(0.25d, weights[0], 1e-6);
        Assert.Equal(0.5d, weights[1], 1e-6);

        var (controlState, controlGraph) = ReadGraph(SequenceFixture(bigEndian, "Smlie", "Frown", "Base"));
        var control = NifModelAnimationReader.ReadNif(controlState, controlGraph,
            TestContext.Current.CancellationToken);
        var controlClip = Assert.Single(control.Clips);
        var only = Assert.Single(controlClip.MorphTargetTracks);
        Assert.Equal(1, only.TargetIndex);
        Assert.Equal(NifModelAnimationReasons.MorphFrameNotFound, control.Dispositions[7].Reason);
        Assert.Contains(DecisionsFor(control, 7),
            static decision => decision.Code == NifModelAnimationReasons.MorphFrameNotFoundCode);
    }

    /// <summary>
    ///     A clip driving target 1 of a mesh that has only one target is refused by Shared's structural validation, so a
    ///     count mismatch cannot reach a writer silently. Control: the same clip over a two-target mesh validates.
    /// </summary>
    [Fact]
    public void TargetCountMismatch_IsRefusedByShared()
    {
        var (state, graph) = ReadGraph(SequenceFixture(false, "Smile", "Frown", "Base"));
        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(result.Clips).MorphTargetTracks.Count);

        var mismatch = AssembleMorphDocument(graph, result.Clips, FaceNode, 1);
        Assert.Throws<InvalidDataException>(() =>
            SceneValidation.ValidateStructure(mismatch, TestContext.Current.CancellationToken));

        SceneValidation.ValidateStructure(AssembleMorphDocument(graph, result.Clips, FaceNode, 2),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     RE-21 step 7: two controlled blocks binding one morph ('Smile' twice) both stay native with the repeated-morph
    ///     reason while the third ('Frown', the static 0 interpolator) still types as a Constant target. Control: distinct
    ///     frame names type both keyed targets.
    /// </summary>
    [Fact]
    public void RepeatedMorphTarget_InOneSequence_StaysNative()
    {
        var (state, graph) = ReadGraph(SequenceFixture(false, "Smile", "Smile", "Frown"));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        var only = Assert.Single(clip.MorphTargetTracks);
        Assert.Equal(1, only.TargetIndex);
        Assert.Equal(SceneAnimationChannelState.Constant, only.Weight.State);
        Assert.Equal(new[] { 0f }, only.Weight.StaticValue);
        foreach (var block in new[] { 7, 8, 9, 10 })
        {
            Assert.Equal(NifModelAnimationReasons.RepeatedMorphTarget, result.Dispositions[block].Reason);
            Assert.Contains(DecisionsFor(result, block),
                static decision => decision.Code == NifModelAnimationReasons.RepeatedMorphTargetCode);
        }

        SceneValidation.ValidateStructure(AssembleMorphDocument(graph, result.Clips, FaceNode, 2),
            TestContext.Current.CancellationToken);

        var (controlState, controlGraph) = ReadGraph(SequenceFixture(false, "Smile", "Frown", "Base"));
        var control = NifModelAnimationReader.ReadNif(controlState, controlGraph,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(control.Clips).MorphTargetTracks.Count);
    }

    /// <summary>
    ///     A manager-controlled morpher whose slots hold NiBlendFloatInterpolators stays native as manager blend state
    ///     and gives no clip; a free-running morpher with one blend slot types the other target and keeps the blend slot
    ///     native. Control: the free-running form with a keyed slot instead types both (the first test).
    /// </summary>
    [Fact]
    public void ManagerBlendMorpher_StaysNativeAsBlendState()
    {
        var (managedState, managedGraph) = ReadGraph(BlendFixture(true));
        var managed = NifModelAnimationReader.ReadNif(managedState, managedGraph, TestContext.Current.CancellationToken);
        Assert.Empty(managed.Clips);
        Assert.Equal(NifModelAnimationReasons.ManagerControlled, managed.Dispositions[MorpherBlock].Reason);
        foreach (var block in new[] { 5, 6, 7 })
        {
            Assert.Equal(NifModelAnimationReasons.BlendState, managed.Dispositions[block].Reason);
        }

        var (freeState, freeGraph) = ReadGraph(BlendFixture(false));
        var free = NifModelAnimationReader.ReadNif(freeState, freeGraph, TestContext.Current.CancellationToken);
        var clip = Assert.Single(free.Clips);
        var only = Assert.Single(clip.MorphTargetTracks);
        Assert.Equal(0, only.TargetIndex);
        Assert.True(free.Dispositions[MorpherBlock].IsTyped);
        Assert.Equal(NifModelAnimationReasons.BlendState, free.Dispositions[7].Reason);
        Assert.Contains(DecisionsFor(free, 7),
            static decision => decision.Code == NifModelAnimationReasons.BlendStateCode &&
                               decision.SourceBlock == MorpherBlock);
        Assert.True(free.Dispositions[8].IsTyped);
    }

    /// <summary>
    ///     A compact float B-spline weight (four controls 0, 0, 1, 1 over [0, 1], offset 0, half range 1) types as a
    ///     width-1 B-spline curve on its own per-target track, and the evaluator samples the clamped cubic: 0 at the
    ///     start, 0.5 at the midpoint, 1 at the stop. Control: half range 0.5 halves the sampled weight.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactFloatBspline_TypesAsABSplineWeightCurve(bool bigEndian)
    {
        var (state, graph) = ReadGraph(BsplineFixture(bigEndian, 1f));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        Assert.Empty(clip.MorphTracks);
        Assert.Equal(2, clip.MorphTargetTracks.Count);
        var spline = clip.MorphTargetTracks[1];
        Assert.Equal(1, spline.TargetIndex);
        Assert.Equal(SceneInterpolation.BSpline, spline.Weight.Interpolation);
        Assert.NotNull(spline.Weight.Spline);
        Assert.True(spline.Weight.Spline!.IsQuantized);
        Assert.Equal(1, spline.Weight.Spline.ComponentCount);
        Assert.Equal(4, spline.Weight.Spline.ControlPointCount);
        foreach (var block in new[] { 9, 10, 11 })
        {
            Assert.True(result.Dispositions[block].IsTyped);
        }

        var document = AssembleMorphDocument(graph, result.Clips, FaceNode, 2);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        Assert.Equal(0d, SampleWeights(document, 0, 0f, FaceNode)[1], 1e-5);
        Assert.Equal(0.5d, SampleWeights(document, 0, 0.5f, FaceNode)[1], 1e-5);
        Assert.Equal(1d, SampleWeights(document, 0, 1f, FaceNode)[1], 1e-5);

        var (controlState, controlGraph) = ReadGraph(BsplineFixture(bigEndian, 0.5f));
        var control = NifModelAnimationReader.ReadNif(controlState, controlGraph,
            TestContext.Current.CancellationToken);
        var controlDocument = AssembleMorphDocument(controlGraph, control.Clips, FaceNode, 2);
        Assert.Equal(0.5d, SampleWeights(controlDocument, 0, 1f, FaceNode)[1], 1e-5);
    }

    /// <summary>The Base slot (interpolator 5, data 6) is native with the RE-23 reason and code.</summary>
    private static void AssertBaseNative(NifModelAnimationResult result)
    {
        foreach (var block in new[] { 5, 6 })
        {
            Assert.Equal(ModelSourceCoverageKind.NativeOnly, result.Dispositions[block].Kind);
            Assert.Equal(NifModelAnimationReasons.BaseWeight, result.Dispositions[block].Reason);
            Assert.Contains(DecisionsFor(result, block),
                static decision => decision.Code == NifModelAnimationReasons.BaseWeightCode);
        }
    }

    /// <summary>
    ///     0 Root [1]; 1 Face (data 2, controller 3); 2 the triangle; 3 a free-running, active, CLAMP morpher over [0, 2]
    ///     (target 1, data 4, slots 5, 7, 9); 4 NiMorphData Base, Smile, Frown; 5 and 6 the Base interpolator keyed 0.0;
    ///     7 and 8 Smile; 9 and 10 Frown, with the given NiFloatData bodies.
    /// </summary>
    private static byte[] EmbeddedFixture(bool bigEndian, Action<NifTestBlockWriter> smile,
        Action<NifTestBlockWriter> frown)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var face = builder.AddString("Face");
        var baseName = builder.AddString("Base");
        var smileName = builder.AddString("Smile");
        var frownName = builder.AddString("Frown");
        Add(builder, 0, "NiNode", Node(root, [1]));
        AddFace(builder, 1, face, 3);
        Add(builder, 3, MorpherController,
            Morpher(1, 4, Active | ClampCycle, [(5, 0f), (7, 0f), (9, 0f)], stop: 2f));
        Add(builder, 4, "NiMorphData",
            MorphData(1, (baseName, BaseVectors), (smileName, SmileVectors), (frownName, FrownVectors)));
        Add(builder, 5, "NiFloatInterpolator", FloatInterpolator(6));
        Add(builder, 6, "NiFloatData", FloatData(Linear, [0f, 0f], [2f, 0f]));
        Add(builder, 7, "NiFloatInterpolator", FloatInterpolator(8));
        Add(builder, 8, "NiFloatData", smile);
        Add(builder, 9, "NiFloatInterpolator", FloatInterpolator(10));
        Add(builder, 10, "NiFloatData", frown);
        return builder.Build();
    }

    /// <summary>
    ///     0 Root [1] with the manager (3) on its chain; 1 Face (data 2, controller 4); 2 the triangle; 3 the manager
    ///     listing sequence 5; 4 a manager-controlled morpher (target 1, data 6, three empty slots); 5 sequence 'Talk'
    ///     with three morph controlled blocks on Face naming controller 4, interpolators 7, 9 and 11 and the given
    ///     Interpolator IDs; 6 NiMorphData Base, Smile, Frown; 7 and 8 LINEAR (0, 0), (1, 1); 9 and 10 LINEAR (0, 0),
    ///     (0.5, 1); 11 a static 0.0 NiFloatInterpolator with no data.
    /// </summary>
    private static byte[] SequenceFixture(bool bigEndian, string firstId, string secondId, string thirdId)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var face = builder.AddString("Face");
        var talk = builder.AddString("Talk");
        var type = builder.AddString(MorpherController);
        var baseName = builder.AddString("Base");
        var smileName = builder.AddString("Smile");
        var frownName = builder.AddString("Frown");
        var ids = new[] { firstId, secondId, thirdId }.Select(id => id switch
        {
            "Base" => baseName,
            "Smile" => smileName,
            "Frown" => frownName,
            _ => builder.AddString(id)
        }).ToArray();
        Add(builder, 0, "NiNode", Node(root, [1], 3));
        AddFace(builder, 1, face, 4);
        Add(builder, 3, "NiControllerManager", Manager(0, [5], -1));
        Add(builder, 4, MorpherController,
            Morpher(1, 6, Active | ManagerControlled | ClampCycle, [(-1, 0f), (-1, 0f), (-1, 0f)]));
        Add(builder, 5, "NiControllerSequence", Sequence(talk,
        [
            Controlled(7, face, type, controller: 4, interpolatorId: ids[0]),
            Controlled(9, face, type, controller: 4, interpolatorId: ids[1]),
            Controlled(11, face, type, controller: 4, interpolatorId: ids[2])
        ], manager: 3));
        Add(builder, 6, "NiMorphData",
            MorphData(1, (baseName, BaseVectors), (smileName, SmileVectors), (frownName, FrownVectors)));
        Add(builder, 7, "NiFloatInterpolator", FloatInterpolator(8));
        Add(builder, 8, "NiFloatData", FloatData(Linear, [0f, 0f], [1f, 1f]));
        Add(builder, 9, "NiFloatInterpolator", FloatInterpolator(10));
        Add(builder, 10, "NiFloatData", FloatData(Linear, [0f, 0f], [0.5f, 1f]));
        Add(builder, 11, "NiFloatInterpolator", FloatInterpolator(-1, Bits(0f)));
        return builder.Build();
    }

    /// <summary>
    ///     The embedded layout with blend slots: when <paramref name="managed" /> the morpher is manager-controlled and
    ///     every slot (5, 6, 7) is an NiBlendFloatInterpolator; otherwise it is free-running with slot 0 the keyed Base
    ///     (5 and its data 6), slot 1 the keyed Smile (8 and its data 9) and slot 2 an NiBlendFloatInterpolator (7).
    /// </summary>
    private static byte[] BlendFixture(bool managed)
    {
        var builder = new NifTestFileBuilder(false, Bs);
        var root = builder.AddString("Root");
        var face = builder.AddString("Face");
        var baseName = builder.AddString("Base");
        var smileName = builder.AddString("Smile");
        var frownName = builder.AddString("Frown");
        Add(builder, 0, "NiNode", Node(root, [1]));
        AddFace(builder, 1, face, 3);
        if (managed)
        {
            Add(builder, 3, MorpherController,
                Morpher(1, 4, Active | ManagerControlled | ClampCycle, [(5, 0f), (6, 0f), (7, 0f)]));
        }
        else
        {
            Add(builder, 3, MorpherController, Morpher(1, 4, Active | ClampCycle, [(5, 0f), (8, 0f), (7, 0f)]));
        }

        Add(builder, 4, "NiMorphData",
            MorphData(1, (baseName, BaseVectors), (smileName, SmileVectors), (frownName, FrownVectors)));
        if (managed)
        {
            Add(builder, 5, "NiBlendFloatInterpolator", BlendFloatInterpolator());
            Add(builder, 6, "NiBlendFloatInterpolator", BlendFloatInterpolator());
            Add(builder, 7, "NiBlendFloatInterpolator", BlendFloatInterpolator());
        }
        else
        {
            Add(builder, 5, "NiFloatInterpolator", FloatInterpolator(6));
            Add(builder, 6, "NiFloatData", FloatData(Linear, [0f, 0f], [1f, 0f]));
            Add(builder, 7, "NiBlendFloatInterpolator", BlendFloatInterpolator());
            Add(builder, 8, "NiFloatInterpolator", FloatInterpolator(9));
            Add(builder, 9, "NiFloatData", FloatData(Linear, [0f, 0f], [1f, 1f]));
        }

        return builder.Build();
    }

    /// <summary>
    ///     The embedded layout over [0, 1] with the Frown slot a compact float B-spline: 9 NiBSplineCompFloatInterpolator
    ///     (spline 10, basis 11, handle 0, offset 0, the given half range); 10 NiBSplineData with the compact controls 0,
    ///     0, 32767, 32767; 11 NiBSplineBasisData with 4 control points.
    /// </summary>
    private static byte[] BsplineFixture(bool bigEndian, float halfRange)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var face = builder.AddString("Face");
        var baseName = builder.AddString("Base");
        var smileName = builder.AddString("Smile");
        var frownName = builder.AddString("Frown");
        Add(builder, 0, "NiNode", Node(root, [1]));
        AddFace(builder, 1, face, 3);
        Add(builder, 3, MorpherController, Morpher(1, 4, Active | ClampCycle, [(5, 0f), (7, 0f), (9, 0f)]));
        Add(builder, 4, "NiMorphData",
            MorphData(1, (baseName, BaseVectors), (smileName, SmileVectors), (frownName, FrownVectors)));
        Add(builder, 5, "NiFloatInterpolator", FloatInterpolator(6));
        Add(builder, 6, "NiFloatData", FloatData(Linear, [0f, 0f], [1f, 0f]));
        Add(builder, 7, "NiFloatInterpolator", FloatInterpolator(8));
        Add(builder, 8, "NiFloatData", FloatData(Linear, [0f, 0f], [1f, 1f]));
        Add(builder, 9, "NiBSplineCompFloatInterpolator", CompFloatBspline(0f, 1f, 10, 11, 0, 0f, halfRange));
        Add(builder, 10, "NiBSplineData", BsplineData(0, 0, 32767, 32767));
        Add(builder, 11, "NiBSplineBasisData", BsplineBasis(4));
        return builder.Build();
    }
}
