using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationReaderTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTargetTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 6, the <c>(controllers)</c> clip (<see cref="NifModelAnimationReader" />; plan section 1.6, owner
///     ruling D6, RE-22 rules 1 and 3), plus the assembled document of sequences and controllers through Shared's
///     validation. The sampled oracle is the engine's controller clock restated from RE-22's answer
///     (NiTimeController::ComputeScaledTime: s = f t + p; LOOP fmod(s - lo, L) + lo; REVERSE fmod(s, 2L) anchored at 0;
///     clamp to [lo, hi]), evaluated through Shared's public <see cref="ScenePoseEvaluator" />.
/// </summary>
public sealed class NifModelAnimationReaderControllersTests
{
    private static readonly float[] SampleTimes = [0.1f, 0.35f, 0.8f, 1.3f, 2.05f, 3.75f];

    /// <summary>
    ///     Three free-running controllers, LOOP (f 2, phase 0.25, [0, 1]), REVERSE (f 1, phase 0.3, [0.5, 1.5]) and CLAMP
    ///     (f 0.5, phase -0.25, [0, 2]), each keying translation x from 0 to 10 over its interval: the clip has no clock,
    ///     every track carries its controller's clock, and the evaluated pose matches the engine at every sample. Control:
    ///     retiming the keys at read time (times divided by the frequency, no clock) misses the engine on every node.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ControllerClocks_RoundTripThroughTheEvaluator(bool bigEndian)
    {
        var (state, graph) = ReadGraph(ClockFixture(bigEndian));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        Assert.Equal(NifModelAnimationReader.ControllersClipName, clip.Name);
        Assert.Null(clip.Clock);
        Assert.Empty(clip.Events);
        Assert.Equal(9, clip.TransformTracks.Count);
        Assert.All(clip.TransformTracks, static track => Assert.NotNull(track.Clock));
        Assert.Equal(new[] { 1, 2, 3 }, clip.TransformTracks
            .Where(static track => track.Property == SceneTransformProperty.Translation)
            .Select(static track => track.NodeIndex));
        var clocks = clip.TransformTracks.Where(static track => track.Property == SceneTransformProperty.Translation)
            .Select(static track => track.Clock!).ToArray();
        Assert.Equal((2f, 0.25f, 0f, 1f, SceneAnimationCycle.Loop), Describe(clocks[0]));
        Assert.Equal((1f, 0.8f, 0.5f, 1.5f, SceneAnimationCycle.Reverse), Describe(clocks[1]));
        Assert.Equal((0.5f, -0.25f, 0f, 2f, SceneAnimationCycle.Clamp), Describe(clocks[2]));
        Assert.Equal("(controllers)", Extras(clip)["source"]!.GetValue<string>());

        var document = Assemble(graph, result.Clips);
        foreach (var time in SampleTimes)
        {
            Assert.Equal(Engine(0, 2f, 0.25f, 0f, 1f, time), SampleLocal(document, 0, time, 1).Translation.X, 1e-4);
            Assert.Equal(Engine(1, 1f, 0.3f, 0.5f, 1.5f, time), SampleLocal(document, 0, time, 2).Translation.X, 1e-4);
            Assert.Equal(Engine(2, 0.5f, -0.25f, 0f, 2f, time), SampleLocal(document, 0, time, 3).Translation.X, 1e-4);
        }

        var retimed = clip.TransformTracks.Where(static track => track.Property == SceneTransformProperty.Translation)
            .Select(static track => new SceneTransformTrack(track.NodeIndex, track.Property,
                track.Times.Select(time => time / track.Clock!.Frequency), track.Values, track.Interpolation))
            .ToArray();
        var retimedDocument = Assemble(graph, [new SceneAnimation("retimed", [], transformTracks: retimed)]);
        (int Cycle, float Frequency, float Phase, float Start, float Stop, int Node)[] controllers =
            [(0, 2f, 0.25f, 0f, 1f, 1), (1, 1f, 0.3f, 0.5f, 1.5f, 2), (2, 0.5f, -0.25f, 0f, 2f, 3)];
        foreach (var (cycle, frequency, phase, start, stop, node) in controllers)
        {
            Assert.Contains(SampleTimes, time => Math.Abs(
                SampleLocal(retimedDocument, 0, time, node).Translation.X -
                Engine(cycle, frequency, phase, start, stop, time)) > 1d);
        }
    }

    /// <summary>
    ///     An inactive controller stays native 'inactive controller' (D6) and a manager-controlled one (flags 0x20) stays
    ///     native with the manager reason; only the active free-running controller's node (1) is in the clip. With the free
    ///     controller inactive too, no track qualifies and there is no clip at all. Control: setting the inactive
    ///     controller's active bit puts its node (2) in the clip.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InactiveAndManagerControlledControllers_AreExcluded(bool bigEndian)
    {
        const ushort activeClamp = Active | ClampCycle;
        var (state, graph) = ReadGraph(ExclusionFixture(bigEndian, activeClamp, ClampCycle));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        Assert.All(clip.TransformTracks, static track => Assert.Equal(1, track.NodeIndex));
        Assert.Equal(3, clip.TransformTracks.Count);
        Assert.True(result.Dispositions[4].IsTyped);
        Assert.Equal("inactive controller", result.Dispositions[5].Reason);
        Assert.Contains(DecisionsFor(result, 5), static decision => decision.Code == "inactiveController");
        Assert.Equal(NifModelAnimationReasons.ManagerControlled, result.Dispositions[6].Reason);
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, result.Dispositions[6].Kind);

        var (noneState, noneGraph) = ReadGraph(ExclusionFixture(bigEndian, ClampCycle, ClampCycle));
        var none = NifModelAnimationReader.ReadNif(noneState, noneGraph, TestContext.Current.CancellationToken);
        Assert.Empty(none.Clips);
        Assert.Equal("inactive controller", none.Dispositions[4].Reason);
        Assert.Equal("inactive controller", none.Dispositions[5].Reason);

        var (bothState, bothGraph) = ReadGraph(ExclusionFixture(bigEndian, activeClamp, activeClamp));
        var both = NifModelAnimationReader.ReadNif(bothState, bothGraph, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 2 },
            Assert.Single(both.Clips).TransformTracks.Select(static track => track.NodeIndex).Distinct());
        Assert.True(both.Dispositions[5].IsTyped);
    }

    /// <summary>
    ///     The assembled document (the graph's nodes plus the clips [Idle, Walk, (controllers)]) passes Shared's structural
    ///     validation; the manager-side NiMultiTargetTransformController stays native. Control: the same document with one
    ///     track repeated is refused, so the validation examined the clips.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssembledDocument_PassesSharedValidation(bool bigEndian)
    {
        var (state, graph) = ReadGraph(DocumentFixture(bigEndian, Active | ClampCycle));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "Idle", "Walk", NifModelAnimationReader.ControllersClipName },
            result.Clips.Select(static clip => clip.Name));
        Assert.Equal(SceneAnimationCycle.Loop, result.Clips[1].Clock!.Cycle);
        Assert.Equal(NifModelAnimationReasons.MultiTargetBinding, result.Dispositions[3].Reason);
        SceneValidation.ValidateStructure(Assemble(graph, result.Clips), TestContext.Current.CancellationToken);

        var first = result.Clips[0];
        var broken = new SceneAnimation(first.Name, [],
            transformTracks: first.TransformTracks.Append(first.TransformTracks[0]), clock: first.Clock);
        Assert.Throws<InvalidDataException>(() => SceneValidation.ValidateStructure(
            Assemble(graph, [broken, .. result.Clips.Skip(1)]), TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     RE-22 rule 1's exception: an inactive NiMultiTargetTransformController on the manager target's chain plays none
    ///     of the manager's sequences, so both stay native and only the <c>(controllers)</c> clip remains. Control: the
    ///     active one types both (see <see cref="AssembledDocument_PassesSharedValidation" />).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InactiveMultiTargetController_KeepsItsSequencesNative(bool bigEndian)
    {
        var (state, graph) = ReadGraph(DocumentFixture(bigEndian, ClampCycle));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { NifModelAnimationReader.ControllersClipName },
            result.Clips.Select(static clip => clip.Name));
        var reason = NifModelAnimationReasons.Reason(NifModelCurveBlock.InactiveManager);
        Assert.Equal(reason, result.Dispositions[5].Reason);
        Assert.Equal(reason, result.Dispositions[7].Reason);
        Assert.Equal(NifModelAnimationReasons.ManagerNoClip, result.Dispositions[4].Reason);
    }

    /// <summary>The engine's controller output (RE-22) for keys 0 at lo and 10 at hi, restated in double.</summary>
    private static double Engine(int cycle, float frequency, float phase, float start, float stop, float time)
    {
        var s = (double)frequency * time + phase;
        var length = (double)stop - start;
        if (cycle == 0)
        {
            s = (s - start) % length + start;
            if (s < start)
            {
                s += length;
            }
        }
        else if (cycle == 1)
        {
            var r = s % (2 * length);
            if (r < 0)
            {
                r += 2 * length;
            }

            s = r <= length ? start + r : start + (2 * length - r);
        }

        s = Math.Clamp(s, start, stop);
        return 10d * (s - start) / length;
    }

    private static (float, float, float, float, SceneAnimationCycle) Describe(SceneAnimationClock clock)
    {
        return (clock.Frequency, clock.PhaseSeconds, clock.StartSeconds, clock.StopSeconds, clock.Cycle);
    }

    /// <summary>
    ///     Root (node 0) with children Loop, Reverse and Clamp (nodes 1 to 3), each with its own free-running
    ///     NiTransformController (blocks 4 to 6), interpolator (7 to 9) and translation keys 0 at lo, 10 at hi (10 to 12).
    /// </summary>
    private static byte[] ClockFixture(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var loop = builder.AddString("Loop");
        var reverse = builder.AddString("Reverse");
        var clamp = builder.AddString("Clamp");
        Add(builder, 0, "NiNode", Node(root, [1, 2, 3]));
        Add(builder, 1, "NiNode", Node(loop, [], 4));
        Add(builder, 2, "NiNode", Node(reverse, [], 5));
        Add(builder, 3, "NiNode", Node(clamp, [], 6));
        Add(builder, 4, "NiTransformController", TransformControllerBlock(1, 7, Active, 2f, 0.25f, 0f, 1f));
        Add(builder, 5, "NiTransformController",
            TransformControllerBlock(2, 8, Active | ReverseCycle, 1f, 0.3f, 0.5f, 1.5f));
        Add(builder, 6, "NiTransformController",
            TransformControllerBlock(3, 9, Active | ClampCycle, 0.5f, -0.25f, 0f, 2f));
        Add(builder, 7, "NiTransformInterpolator", TransformInterpolator(10));
        Add(builder, 8, "NiTransformInterpolator", TransformInterpolator(11));
        Add(builder, 9, "NiTransformInterpolator", TransformInterpolator(12));
        Add(builder, 10, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 10f, 0f, 0f)));
        Add(builder, 11, "NiTransformData", TransformData(NoRotation, (0.5f, 0f, 0f, 0f), (1.5f, 10f, 0f, 0f)));
        Add(builder, 12, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (2f, 10f, 0f, 0f)));
        return builder.Build();
    }

    /// <summary>
    ///     Root (node 0) with children Free, Sleeping and Managed (nodes 1 to 3), controlled by blocks 4 (the given flags), 5
    ///     (the given flags) and 6 (manager controlled, active, clamp), all through interpolator 7 over keys 8.
    /// </summary>
    private static byte[] ExclusionFixture(bool bigEndian, ushort freeFlags, ushort sleepingFlags)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var free = builder.AddString("Free");
        var sleeping = builder.AddString("Sleeping");
        var managed = builder.AddString("Managed");
        Add(builder, 0, "NiNode", Node(root, [1, 2, 3]));
        Add(builder, 1, "NiNode", Node(free, [], 4));
        Add(builder, 2, "NiNode", Node(sleeping, [], 5));
        Add(builder, 3, "NiNode", Node(managed, [], 6));
        Add(builder, 4, "NiTransformController", TransformControllerBlock(1, 7, freeFlags));
        Add(builder, 5, "NiTransformController", TransformControllerBlock(2, 7, sleepingFlags));
        Add(builder, 6, "NiTransformController",
            TransformControllerBlock(3, 7, ManagerControlled | Active | ClampCycle));
        Add(builder, 7, "NiTransformInterpolator", TransformInterpolator(8));
        Add(builder, 8, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 10f, 0f, 0f)));
        return builder.Build();
    }

    /// <summary>
    ///     Root (node 0) with children Bone and Spin (nodes 1 and 2). Root's controller chain: an
    ///     NiMultiTargetTransformController (block 3, the given flags) then the NiControllerManager (block 4) listing Idle
    ///     (block 5, CLAMP) and Walk (block 7, LOOP), both driving Bone through interpolator 8 and naming the multi-target
    ///     controller as their controller. Spin has a free-running NiTransformController (block 6) through interpolator 9.
    /// </summary>
    private static byte[] DocumentFixture(bool bigEndian, ushort multiTargetFlags)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var bone = builder.AddString("Bone");
        var spin = builder.AddString("Spin");
        var idle = builder.AddString("Idle");
        var walk = builder.AddString("Walk");
        var type = builder.AddString(TransformController);
        Add(builder, 0, "NiNode", Node(root, [1, 2], 3));
        Add(builder, 1, "NiNode", Node(bone, []));
        Add(builder, 2, "NiNode", Node(spin, [], 6));
        Add(builder, 3, "NiMultiTargetTransformController", MultiTarget(0, multiTargetFlags, 4));
        Add(builder, 4, "NiControllerManager", Manager(0, [5, 7], -1));
        Add(builder, 5, "NiControllerSequence",
            Sequence(idle, [Controlled(8, bone, type, controller: 3)], manager: 4));
        Add(builder, 6, "NiTransformController", TransformControllerBlock(2, 9, Active | ClampCycle));
        Add(builder, 7, "NiControllerSequence",
            Sequence(walk, [Controlled(8, bone, type, controller: 3)], manager: 4, cycle: 0));
        Add(builder, 8, "NiTransformInterpolator", TransformInterpolator(10));
        Add(builder, 9, "NiTransformInterpolator", TransformInterpolator(10));
        Add(builder, 10, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 1f, 0f, 0f)));
        return builder.Build();
    }
}
