using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationReaderTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTargetTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 4, sequence clips (<see cref="NifModelAnimationReader" />; plan section 1.6, owner rulings D7 and D11):
///     synthetic NIFs and KFs written field by field, read into the read state and node graph the way NifModelReader
///     builds them, then assembled into clips. Every test runs little- and big-endian and carries a control that fails.
/// </summary>
public sealed class NifModelAnimationReaderSequenceTests
{
    /// <summary>
    ///     A <c>.nif</c>'s clips follow its manager's sequence list [Idle, Walk, Caf&#xE9;], not block order, and each
    ///     name is the Latin-1 text of the stored bytes (0xE9 included). Control: the same clips sorted by their sequence
    ///     block give [Walk, Idle, Caf&#xE9;].
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NifClips_FollowTheManagerSequenceList(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var walk = builder.AddString("Walk");
        var idle = builder.AddString("Idle");
        var cafe = builder.AddRawString([0x43, 0x61, 0x66, 0xE9]);
        var type = builder.AddString(TransformController);
        Add(builder, 0, "NiNode", Node(root, [], 1));
        Add(builder, 1, "NiControllerManager", Manager(0, [3, 2, 4], -1));
        Add(builder, 2, "NiControllerSequence", Sequence(walk, [Controlled(5, root, type)], manager: 1));
        Add(builder, 3, "NiControllerSequence", Sequence(idle, [Controlled(5, root, type)], manager: 1));
        Add(builder, 4, "NiControllerSequence", Sequence(cafe, [Controlled(5, root, type)], manager: 1));
        Add(builder, 5, "NiTransformInterpolator", TransformInterpolator(-1));
        var (state, graph) = ReadGraph(builder.Build());

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "Idle", "Walk", "Caf\u00E9" }, result.Clips.Select(static clip => clip.Name));
        Assert.Equal(new[] { 3, 2, 4 }, result.Clips.Select(static clip => Extras(clip)["block"]!.GetValue<int>()));
        Assert.All(result.Clips, static clip =>
        {
            Assert.NotNull(clip.Clock);
            Assert.Null(clip.DurationSeconds);
            Assert.Equal(3, clip.TransformTracks.Count);
            Assert.All(clip.TransformTracks, static track => Assert.Null(track.Clock));
        });
        Assert.True(result.Dispositions[1].IsTyped);
        SceneValidation.ValidateStructure(Assemble(graph, result.Clips), TestContext.Current.CancellationToken);

        var blockOrder = result.Clips.OrderBy(static clip => Extras(clip)["block"]!.GetValue<int>())
            .Select(static clip => clip.Name).ToArray();
        Assert.Equal(new[] { "Walk", "Idle", "Caf\u00E9" }, blockOrder);
        Assert.NotEqual(blockOrder, result.Clips.Select(static clip => clip.Name).ToArray());
    }

    /// <summary>
    ///     A <c>.kf</c>'s clips follow its footer roots [Idle, Run, Walk], bound within the skeleton, so every track sits on
    ///     the skeleton's 'Bip01 Spine' occurrence (node 2). Control: block order gives [Walk, Idle, Run].
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KfClips_FollowTheFooterOrder_AndBindInTheSkeleton(bool bigEndian)
    {
        var (_, skeleton) = ReadGraph(Skeleton(bigEndian));
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var walk = builder.AddString("Walk");
        var idle = builder.AddString("Idle");
        var run = builder.AddString("Run");
        var spine = builder.AddString("Bip01 Spine");
        var type = builder.AddString(TransformController);
        Add(builder, 0, "NiControllerSequence", Sequence(walk, [Controlled(3, spine, type)]));
        Add(builder, 1, "NiControllerSequence", Sequence(idle, [Controlled(3, spine, type)]));
        Add(builder, 2, "NiControllerSequence", Sequence(run, [Controlled(3, spine, type)]));
        Add(builder, 3, "NiTransformInterpolator", TransformInterpolator(4));
        Add(builder, 4, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 1f, 2f, 3f)));
        builder.WithRoot(1).WithRoot(2).WithRoot(0);
        var state = ReadState(builder.Build());

        var result = NifModelAnimationReader.ReadKf(state, skeleton, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "Idle", "Run", "Walk" }, result.Clips.Select(static clip => clip.Name));
        Assert.Equal("Bip01 Spine", skeleton.Nodes[2].Name);
        Assert.All(result.Clips, static clip => Assert.All(clip.TransformTracks,
            static track => Assert.Equal(2, track.NodeIndex)));
        var translation = Assert.Single(result.Clips[0].TransformTracks,
            static track => track.Property == SceneTransformProperty.Translation);
        Assert.Equal(SceneAnimationChannelState.Keyed, translation.State);
        Assert.Equal(new[] { 0f, 1f }, translation.Times);
        SceneValidation.ValidateStructure(Assemble(skeleton, result.Clips), TestContext.Current.CancellationToken);

        var blockOrder = result.Clips.OrderBy(static clip => Extras(clip)["block"]!.GetValue<int>())
            .Select(static clip => clip.Name).ToArray();
        Assert.Equal(new[] { "Walk", "Idle", "Run" }, blockOrder);
        Assert.NotEqual(blockOrder, result.Clips.Select(static clip => clip.Name).ToArray());
    }

    /// <summary>
    ///     The palette binds 'Bone' to block 6, which is instanced under ParentA and ParentB (nodes 4 and 6); another block
    ///     named 'Bone' sits under ParentC and comes first in pre-order (node 2). The clip drives exactly nodes 4 and 6, one
    ///     track per occurrence and channel. Control: a lookup by node name picks node 2, the wrong occurrence, which gets no
    ///     track.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstancedTarget_OneTrackPerOccurrence_AtTheExactNodes(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var parentC = builder.AddString("ParentC");
        var parentA = builder.AddString("ParentA");
        var parentB = builder.AddString("ParentB");
        var bone = builder.AddString("Bone");
        var idle = builder.AddString("Idle");
        var type = builder.AddString(TransformController);
        Add(builder, 0, "NiNode", Node(root, [1, 2, 3], 4));
        Add(builder, 1, "NiNode", Node(parentC, [5]));
        Add(builder, 2, "NiNode", Node(parentA, [6]));
        Add(builder, 3, "NiNode", Node(parentB, [6]));
        Add(builder, 4, "NiControllerManager", Manager(0, [7], 8));
        Add(builder, 5, "NiNode", Node(bone, []));
        Add(builder, 6, "NiNode", Node(bone, []));
        Add(builder, 7, "NiControllerSequence", Sequence(idle, [Controlled(9, bone, type)], manager: 4));
        Add(builder, 8, "NiDefaultAVObjectPalette",
            w => NifTestBlockLayouts.DefaultAvObjectPalette(w, 0, [("Root", 0), ("Bone", 6)]));
        Add(builder, 9, "NiTransformInterpolator", TransformInterpolator(10));
        Add(builder, 10, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 4f, 5f, 6f)));
        var (state, graph) = ReadGraph(builder.Build());

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 4, 6 }, graph.OccurrencesByBlock[6]);
        Assert.Equal(new[] { 2 }, graph.OccurrencesByBlock[5]);
        var clip = Assert.Single(result.Clips);
        Assert.Equal(new[] { 4, 4, 4, 6, 6, 6 }, clip.TransformTracks.Select(static track => track.NodeIndex));
        Assert.Equal(
            new[]
            {
                SceneTransformProperty.Translation, SceneTransformProperty.Rotation, SceneTransformProperty.Scale,
                SceneTransformProperty.Translation, SceneTransformProperty.Rotation, SceneTransformProperty.Scale
            },
            clip.TransformTracks.Select(static track => track.Property));
        Assert.Equal(new[] { 4, 4, 4, 6, 6, 6 },
            Extras(clip)["tracks"]!.AsArray().Select(static entry => entry!["node"]!.GetValue<int>()));
        Assert.True(result.Dispositions[8].IsTyped);
        SceneValidation.ValidateStructure(Assemble(graph, result.Clips), TestContext.Current.CancellationToken);

        var byName = graph.Nodes.ToList().FindIndex(static node => node.Name == "Bone");
        Assert.Equal(2, byName);
        Assert.DoesNotContain(clip.TransformTracks, track => track.NodeIndex == byName);
    }

    /// <summary>
    ///     RE-21 step 4: 'Bone' named twice under NiTransformController with identical content collapses to the lowest
    ///     controlled block: one clip whose tracks equal the single-block file's, the dropped interpolator native with the
    ///     collapse reason (kept index 0, multiplicity 2), and the document validates. Controls: the same tracks repeated
    ///     are refused by Shared, so keeping both would fail; and a SHARED interpolator collapses the same way.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedTarget_IdenticalContent_CollapsesToTheLowestBlock(bool bigEndian)
    {
        var (singleState, singleGraph) = ReadGraph(RepeatedTargetFixture(bigEndian, Repeat.None));
        var single = NifModelAnimationReader.ReadNif(singleState, singleGraph, TestContext.Current.CancellationToken);
        var singleClip = Assert.Single(single.Clips);

        foreach (var repeat in new[] { Repeat.IdenticalCopy, Repeat.SharedInterpolator })
        {
            var (state, graph) = ReadGraph(RepeatedTargetFixture(bigEndian, repeat));
            var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

            var clip = Assert.Single(result.Clips);
            Assert.Equal("Death", clip.Name);
            Assert.Equal(singleClip.TransformTracks.Count, clip.TransformTracks.Count);
            for (var index = 0; index < clip.TransformTracks.Count; index++)
            {
                Assert.True(NifModelTrackContent.Equal(singleClip.TransformTracks[index], clip.TransformTracks[index]));
                Assert.Equal(1, clip.TransformTracks[index].NodeIndex);
            }

            SceneValidation.ValidateStructure(Assemble(graph, result.Clips), TestContext.Current.CancellationToken);
            var collapsedBlock = repeat == Repeat.IdenticalCopy ? 5 : 3;
            Assert.Contains(DecisionsFor(result, collapsedBlock), static decision =>
                decision.Code == NifModelAnimationReasons.RepeatedCollapsedCode &&
                decision.Disposition.Reason == NifModelAnimationReasons.Collapsed(0, 2) && decision.ControlledBlock == 1);
            Assert.Equal(ModelSourceCoverageKind.Typed, result.Dispositions[4].Kind);
            if (repeat == Repeat.IdenticalCopy)
            {
                Assert.Equal(ModelSourceCoverageKind.NativeOnly, result.Dispositions[5].Kind);
            }
        }

        var repeated = new SceneAnimation("Death", [],
            transformTracks: singleClip.TransformTracks.Concat(singleClip.TransformTracks), clock: singleClip.Clock);
        Assert.Throws<InvalidDataException>(() => SceneValidation.ValidateStructure(
            Assemble(singleGraph, [repeated]), TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     RE-21 steps 2 and 5: repeated targets whose content differs (the second pose translation is 1, 0, 0), or whose
    ///     priorities differ, keep the WHOLE sequence native with their reason: no clip, the text keys 'text keys outside a
    ///     clip', the manager 'manager: no clip'. Control: the identical copy collapses (previous test), so the content
    ///     comparison is what separates them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedTarget_DifferingContentOrPriority_KeepsTheWholeSequenceNative(bool bigEndian)
    {
        foreach (var (repeat, reason) in new[]
                 {
                     (Repeat.DifferentContent, NifModelAnimationReasons.RepeatedDifferingContent),
                     (Repeat.DifferentPriority, NifModelAnimationReasons.RepeatedDifferingPriority)
                 })
        {
            var (state, graph) = ReadGraph(RepeatedTargetFixture(bigEndian, repeat));
            var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

            Assert.Empty(result.Clips);
            foreach (var block in new[] { 3, 4, 5 })
            {
                var disposition = result.Dispositions[block];
                Assert.Equal(ModelSourceCoverageKind.NativeOnly, disposition.Kind);
                Assert.Equal(reason, disposition.Reason);
                Assert.DoesNotContain(DecisionsFor(result, block), static decision => decision.IsTyped);
            }

            Assert.Equal(NifModelAnimationReasons.TextKeysOutsideClip, result.Dispositions[6].Reason);
            Assert.Equal(NifModelAnimationReasons.ManagerNoClip, result.Dispositions[2].Reason);
        }
    }

    /// <summary>
    ///     An XYZ_ROTATION (Euler) group becomes the clip's Euler rotation track on the node (slice 13, RE-20): the
    ///     interpolator and its data are Typed on the rotation channel, the clip keeps the keyed translation and the
    ///     constant scale as transform tracks, no quaternion Rotation track exists for the node, the extras' eulerTracks
    ///     map names it, and the assembled document validates. Control: the same keys as a LINEAR quaternion type a
    ///     Rotation transform track and no Euler track.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EulerRotation_BecomesAnEulerTrack_TheClipKeepsTheOtherTracks(bool bigEndian)
    {
        var (state, graph) = ReadGraph(RotationFixture(bigEndian,
            EulerRotation((0f, 0f), (1f, 1.5f))));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        Assert.Equal(new[] { SceneTransformProperty.Translation, SceneTransformProperty.Scale },
            clip.TransformTracks.Select(static track => track.Property));
        Assert.Equal(SceneAnimationChannelState.Keyed, clip.TransformTracks[0].State);
        Assert.Equal(SceneAnimationChannelState.Constant, clip.TransformTracks[1].State);
        var euler = Assert.Single(clip.EulerRotationTracks);
        Assert.Equal(1, euler.NodeIndex);
        Assert.Equal(SceneEulerOrder.Xyz, euler.Order);
        Assert.Equal(new[] { 0f, 1f }, euler.X.Times);
        Assert.Equal(new[] { 0f, 1.5f }, euler.X.Values);
        Assert.Equal(SceneAnimationChannelState.Constant, euler.Y.State);
        Assert.Equal(SceneAnimationChannelState.Constant, euler.Z.State);
        Assert.Null(euler.Clock);
        foreach (var block in new[] { 4, 5 })
        {
            var rotation = Assert.Single(DecisionsFor(result, block),
                static decision => decision.Property == SceneTransformProperty.Rotation);
            Assert.True(rotation.IsTyped);
            Assert.Equal(3, rotation.SourceBlock);
            Assert.Equal(0, rotation.ControlledBlock);
        }

        var eulerSource = Assert.Single(Extras(clip)["eulerTracks"]!.AsArray())!;
        Assert.Equal("rotation", eulerSource["property"]!.GetValue<string>());
        Assert.Equal(1, eulerSource["node"]!.GetValue<int>());
        Assert.Equal(4, eulerSource["interpolator"]!.GetValue<int>());
        Assert.Equal(new[] { 1u, 0u, 0u },
            eulerSource["axisKeyTypes"]!.AsArray().Select(static keyType => keyType!.GetValue<uint>()));
        Assert.Equal(2, Extras(clip)["tracks"]!.AsArray().Count);
        SceneValidation.ValidateStructure(Assemble(graph, result.Clips), TestContext.Current.CancellationToken);

        var quarter = MathF.Sqrt(0.5f);
        var (linearState, linearGraph) = ReadGraph(RotationFixture(bigEndian,
            LinearRotation((0f, 1f, 0f, 0f, 0f), (1f, quarter, 0f, 0f, quarter))));
        var linear = NifModelAnimationReader.ReadNif(linearState, linearGraph, TestContext.Current.CancellationToken);
        var linearClip = Assert.Single(linear.Clips);
        var rotated = Assert.Single(linearClip.TransformTracks,
            static track => track.Property == SceneTransformProperty.Rotation);
        Assert.Equal(SceneAnimationChannelState.Keyed, rotated.State);
        Assert.Empty(linearClip.EulerRotationTracks);
        Assert.Empty(Extras(linearClip)["eulerTracks"]!.AsArray());
        Assert.DoesNotContain(linear.Decisions, static decision => !decision.IsTyped &&
            decision.Property == SceneTransformProperty.Rotation);
    }

    /// <summary>
    ///     D11: the clip's ExtrasJson carries the weight, accumulation root, manager ref, text-key ref and each controlled
    ///     block's priority and five strings exactly, beside the typed source policies (weight 0.75, root 'Root' bound to node
    ///     0, priority on the track) and a NativeOnly decision on the sequence. Control: priority 27 instead of 26 changes the
    ///     extras, and only in the priority.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtrasJson_CarriesTheD11Fields(bool bigEndian)
    {
        var (state, graph) = ReadGraph(PolicyFixture(bigEndian, 26));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        var extras = Extras(clip);
        Assert.Equal(NifModelAnimationExtras.Version, extras["version"]!.GetValue<int>());
        Assert.Equal("sequence", extras["source"]!.GetValue<string>());
        Assert.Equal(3, extras["block"]!.GetValue<int>());
        Assert.Equal(Bits(0.75f), extras["weightBits"]!.GetValue<uint>());
        Assert.Equal("Root", extras["accumRootName"]!.GetValue<string>());
        Assert.Equal(2, extras["managerRef"]!.GetValue<int>());
        Assert.Equal(5, extras["textKeysRef"]!.GetValue<int>());
        var controlled = Assert.Single(extras["controlledBlocks"]!.AsArray())!;
        Assert.Equal(26, controlled["priority"]!.GetValue<int>());
        Assert.Equal("Bone", controlled["nodeName"]!.GetValue<string>());
        Assert.Null(controlled["propertyType"]);
        Assert.Equal(-1, controlled["propertyTypeIndex"]!.GetValue<int>());
        Assert.Equal(TransformController, controlled["controllerType"]!.GetValue<string>());
        Assert.Equal("CtlId", controlled["controllerId"]!.GetValue<string>());
        Assert.Equal("InterpId", controlled["interpolatorId"]!.GetValue<string>());
        Assert.Equal(4, controlled["interpolatorRef"]!.GetValue<int>());
        Assert.Equal(3, extras["tracks"]!.AsArray().Count);

        Assert.Equal(0.75f, clip.SourcePolicy!.Weight);
        Assert.Equal("Root", clip.SourcePolicy.AccumulationRoot!.SourceName);
        Assert.Equal(0, clip.SourcePolicy.AccumulationRoot.NodeIndex);
        Assert.Equal(SceneValueProvenance.Authored, clip.SourcePolicy.Provenance);
        Assert.All(clip.TransformTracks, static track => Assert.Equal(26, track.SourcePolicy!.Priority));
        Assert.Contains(DecisionsFor(result, 3), static decision =>
            decision.Reason == NifModelAnimationReasons.SequenceNativeFields);
        Assert.True(result.Dispositions[3].IsTyped);

        var (otherState, otherGraph) = ReadGraph(PolicyFixture(bigEndian, 27));
        var other = Assert.Single(NifModelAnimationReader
            .ReadNif(otherState, otherGraph, TestContext.Current.CancellationToken).Clips);
        Assert.NotEqual(clip.ExtrasJson, other.ExtrasJson);
        Assert.Equal(27, Extras(other)["controlledBlocks"]![0]!["priority"]!.GetValue<int>());
        Assert.Equal(other.ExtrasJson, clip.ExtrasJson!.Replace("\"priority\":26", "\"priority\":27",
            StringComparison.Ordinal));
    }

    /// <summary>
    ///     A sequence's text keys (2.0 'b', 0.5 'a', 0.5 'c') arrive as the clip's events in file order and the text-key
    ///     block is typed. Control: sorting by time gives 'a', 'c', 'b'.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextKeys_ArriveAsEvents_InFileOrder(bool bigEndian)
    {
        var (state, graph) = ReadGraph(PolicyFixture(bigEndian, 26));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        Assert.Equal(new[] { "b", "a", "c" }, clip.Events.Select(static e => e.Text));
        Assert.Equal(new[] { Bits(2f), Bits(0.5f), Bits(0.5f) }, clip.Events.Select(static e => Bits(e.TimeSeconds)));
        Assert.True(result.Dispositions[5].IsTyped);
        Assert.NotEqual(clip.Events.Select(static e => e.Text).ToArray(),
            clip.Events.OrderBy(static e => e.TimeSeconds).Select(static e => e.Text).ToArray());
    }

    /// <summary>A skeleton: Bip01 with children Bip01 Pelvis and Bip01 Spine (pre-order nodes 0, 1, 2).</summary>
    private static byte[] Skeleton(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var bip = builder.AddString("Bip01");
        var pelvis = builder.AddString("Bip01 Pelvis");
        var spine = builder.AddString("Bip01 Spine");
        Add(builder, 0, "NiNode", Node(bip, [1, 2]));
        Add(builder, 1, "NiNode", Node(pelvis, []));
        Add(builder, 2, "NiNode", Node(spine, []));
        return builder.Build();
    }

    /// <summary>How <see cref="RepeatedTargetFixture" /> repeats the 'Bone' controlled block.</summary>
    private enum Repeat
    {
        None,
        IdenticalCopy,
        SharedInterpolator,
        DifferentContent,
        DifferentPriority
    }

    /// <summary>
    ///     Root (node 0) with child Bone (node 1); a manager lists sequence 'Death' (block 3), which names 'Bone' under
    ///     NiTransformController once, or twice per <paramref name="repeat" />: a second interpolator (block 5) that is an
    ///     identical copy, the SAME interpolator 4, a copy whose pose translation is 1, 0, 0, or an identical copy at
    ///     priority 20 instead of 15. Text keys at block 6.
    /// </summary>
    private static byte[] RepeatedTargetFixture(bool bigEndian, Repeat repeat)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var bone = builder.AddString("Bone");
        var death = builder.AddString("Death");
        var type = builder.AddString(TransformController);
        var start = builder.AddString("start");
        var blocks = repeat switch
        {
            Repeat.None => new[] { Controlled(4, bone, type, 15) },
            Repeat.SharedInterpolator => new[] { Controlled(4, bone, type, 15), Controlled(4, bone, type, 15) },
            Repeat.DifferentPriority => new[] { Controlled(4, bone, type, 15), Controlled(5, bone, type, 20) },
            _ => new[] { Controlled(4, bone, type, 15), Controlled(5, bone, type, 15) }
        };
        Add(builder, 0, "NiNode", Node(root, [1], 2));
        Add(builder, 1, "NiNode", Node(bone, []));
        Add(builder, 2, "NiControllerManager", Manager(0, [3], -1));
        Add(builder, 3, "NiControllerSequence", Sequence(death, blocks, 6, 2));
        Add(builder, 4, "NiTransformInterpolator", TransformInterpolator(-1));
        Add(builder, 5, "NiTransformInterpolator", repeat == Repeat.DifferentContent
            ? static w => w.F32s(1f, 0f, 0f).F32s(1f, 0f, 0f, 0f).F32(1f).Ref(-1)
            : TransformInterpolator(-1));
        Add(builder, 6, "NiTextKeyExtraData", TextKeys((0f, start)));
        return builder.Build();
    }

    /// <summary>
    ///     Root (node 0) with child Bone (node 1); sequence 'Turn' (block 3) drives Bone through interpolator 4, whose
    ///     NiTransformData (block 5) holds the given rotation part and two LINEAR translation keys.
    /// </summary>
    private static byte[] RotationFixture(bool bigEndian, Action<NifTestBlockWriter> rotation)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var bone = builder.AddString("Bone");
        var turn = builder.AddString("Turn");
        var type = builder.AddString(TransformController);
        Add(builder, 0, "NiNode", Node(root, [1], 2));
        Add(builder, 1, "NiNode", Node(bone, []));
        Add(builder, 2, "NiControllerManager", Manager(0, [3], -1));
        Add(builder, 3, "NiControllerSequence", Sequence(turn, [Controlled(4, bone, type)], manager: 2));
        Add(builder, 4, "NiTransformInterpolator", TransformInterpolator(5));
        Add(builder, 5, "NiTransformData", TransformData(rotation, (0f, 0f, 0f, 0f), (1f, 1f, 2f, 3f)));
        return builder.Build();
    }

    /// <summary>
    ///     Root (node 0) with child Bone (node 1); sequence 'Idle' (block 3): weight 0.75, accumulation root 'Root', manager
    ///     2, text keys 5 (2.0 'b', 0.5 'a', 0.5 'c'), one controlled block (interpolator 4 on Bone, the given priority,
    ///     property type NULL, controller ID 'CtlId', interpolator ID 'InterpId').
    /// </summary>
    private static byte[] PolicyFixture(bool bigEndian, byte priority)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var bone = builder.AddString("Bone");
        var idle = builder.AddString("Idle");
        var type = builder.AddString(TransformController);
        var controllerId = builder.AddString("CtlId");
        var interpolatorId = builder.AddString("InterpId");
        var b = builder.AddString("b");
        var a = builder.AddString("a");
        var c = builder.AddString("c");
        Add(builder, 0, "NiNode", Node(root, [1], 2));
        Add(builder, 1, "NiNode", Node(bone, []));
        Add(builder, 2, "NiControllerManager", Manager(0, [3], -1));
        Add(builder, 3, "NiControllerSequence", Sequence(idle,
            [Controlled(4, bone, type, priority, controllerId: controllerId, interpolatorId: interpolatorId)],
            5, 2, root, 0.75f));
        Add(builder, 4, "NiTransformInterpolator", TransformInterpolator(-1));
        Add(builder, 5, "NiTextKeyExtraData", TextKeys((2f, b), (0.5f, a), (0.5f, c)));
        return builder.Build();
    }
}
