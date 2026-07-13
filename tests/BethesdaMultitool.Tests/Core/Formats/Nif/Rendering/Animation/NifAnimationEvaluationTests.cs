using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Deterministic time-domain evaluation: <see cref="NifTrackSampler" /> bracketing/interpolation
///     /looping, <see cref="NifAnimationClipSelector" /> policy on the banner's exact key set, and
///     <see cref="NifAnimationPoseEvaluator" /> parent-chain + skin-matrix composition.
/// </summary>
public class NifAnimationEvaluationTests
{
    // ---- sampler ------------------------------------------------------------------------------

    [Fact]
    public void Sampler_ExactKeyHit_ReturnsKeyValue()
    {
        NifVec3Key[] keys = [new(0f, Vector3.Zero), new(1f, new Vector3(10f, 0f, 0f))];
        Assert.Equal(new Vector3(10f, 0f, 0f), NifTrackSampler.SampleTranslation(keys, 1f));
    }

    [Fact]
    public void Sampler_Midpoint_Lerps()
    {
        NifVec3Key[] keys = [new(0f, Vector3.Zero), new(2f, new Vector3(10f, -4f, 6f))];
        Assert.Equal(new Vector3(5f, -2f, 3f), NifTrackSampler.SampleTranslation(keys, 1f));
    }

    [Fact]
    public void Sampler_OutsideRange_Clamps()
    {
        NifFloatKey[] keys = [new(1f, 5f), new(2f, 9f)];
        Assert.Equal(5f, NifTrackSampler.SampleScale(keys, 0f));
        Assert.Equal(9f, NifTrackSampler.SampleScale(keys, 99f));
    }

    [Fact]
    public void Sampler_Rotation_SlerpsHalfway()
    {
        var quarterTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        NifQuatKey[] keys = [new(0f, Quaternion.Identity), new(1f, quarterTurn)];

        var half = NifTrackSampler.SampleRotation(keys, 0.5f);

        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f);
        Assert.Equal(expected.Z, half.Z, 4);
        Assert.Equal(expected.W, half.W, 4);
    }

    [Fact]
    public void MapTime_LoopWrapsIntoClipWindow()
    {
        // NiTimeController cycle semantics: controller-local time u = t×freq + phase wraps into
        // [start, stop]. Any wall clock lands inside the window; window-length steps alias.
        var wrapped = NifTrackSampler.MapTime(1.4f, 1f, 0f, 2.333f, 3.667f, loop: true);
        Assert.InRange(wrapped, 2.333f, 3.667f);

        var length = 3.667f - 2.333f;
        var aliased = NifTrackSampler.MapTime(1.4f + length, 1f, 0f, 2.333f, 3.667f, loop: true);
        Assert.Equal(wrapped, aliased, 3);

        // A time already inside the window maps to itself.
        Assert.Equal(3f, NifTrackSampler.MapTime(3f, 1f, 0f, 2.333f, 3.667f, loop: true), 3);
    }

    [Fact]
    public void MapTime_ClampHoldsAtStop()
    {
        Assert.Equal(3f, NifTrackSampler.MapTime(10f, 1f, 0f, 1f, 3f, loop: false), 4);
    }

    // ---- clip selector --------------------------------------------------------------------------

    [Fact]
    public void ClipSelector_BannerTracks_LoopsFullAuthoredRange()
    {
        // Passive Morrowind decor loops its whole controller range (returns to the hang each cycle),
        // NOT one text-key idle sub-window: furn_banner_tavern_01.nif's Root Bone track spans 0→4 s,
        // so the play window is the full 0→4 even though "Idle3: Loop Start/Stop" mark 2.333–3.667.
        var bannerRotKeys = new NifQuatKey[]
        {
            new(0f, Quaternion.Identity), new(0.667f, Quaternion.Identity),
            new(1.333f, Quaternion.Identity), new(2f, Quaternion.Identity),
            new(2.333f, Quaternion.Identity), new(2.667f, Quaternion.Identity),
            new(3f, Quaternion.Identity), new(3.333f, Quaternion.Identity),
            new(3.667f, Quaternion.Identity), new(4f, Quaternion.Identity),
        };
        var track = new NifNodeTrack(
            "Root Bone", 1f, 0f,
            NifKeyInterpolation.Quadratic, bannerRotKeys,
            NifKeyInterpolation.Linear, [], NifKeyInterpolation.Linear, []);

        NifAnimTextKey[] textKeys =
        [
            new(0f, "Idle: Start"), new(0f, "Idle: Stop"),
            new(0f, "Idle2: Start"), new(2f, "Idle2: Stop"),
            new(2f, "Idle3: Start"), new(2.333333f, "Idle3: Loop Start"),
            new(3.666667f, "Idle3: Loop Stop"), new(4f, "Idle3: Stop"),
        ];

        var clip = NifAnimationClipSelector.SelectClip(textKeys, [track]);

        Assert.NotNull(clip);
        Assert.Equal(0f, clip.Value.Start, 4);
        Assert.Equal(4f, clip.Value.Stop, 4);
        Assert.True(clip.Value.Loops);
    }

    [Fact]
    public void ClipSelector_TextKeysIgnored_RangeComesFromTracks()
    {
        // Text-key group markers do not drive selection (they are actor-PlayGroup vocabulary, not a
        // passive-prop control): the window is the track key span regardless of what the markers say.
        NifAnimTextKey[] misleadingTextKeys = [new(2.333f, "Idle3: Loop Start"), new(3.667f, "Idle3: Loop Stop")];
        var track = new NifNodeTrack(
            "Bone", 1f, 0f,
            NifKeyInterpolation.Linear,
            [new(0f, Quaternion.Identity), new(3f, Quaternion.Identity)],
            NifKeyInterpolation.Linear, [], NifKeyInterpolation.Linear, []);

        var clip = NifAnimationClipSelector.SelectClip(misleadingTextKeys, [track]);

        Assert.NotNull(clip);
        Assert.Equal(0f, clip.Value.Start);
        Assert.Equal(3f, clip.Value.Stop);
    }

    [Fact]
    public void ClipSelector_NoMotionTracks_ReturnsNull()
    {
        // No animated tracks (only degenerate text markers, no key motion) → not animated.
        NifAnimTextKey[] keys = [new(0f, "Idle: Start"), new(0f, "Idle: Stop")];
        Assert.Null(NifAnimationClipSelector.SelectClip(keys, []));
    }

    [Fact]
    public void ClipSelector_NoTextKeys_FallsBackToKeyRangeUnion()
    {
        var track = new NifNodeTrack(
            "Bone", 1f, 0f,
            NifKeyInterpolation.Linear,
            [new(0.5f, Quaternion.Identity), new(2.5f, Quaternion.Identity)],
            NifKeyInterpolation.Linear, [],
            NifKeyInterpolation.Linear, []);

        var clip = NifAnimationClipSelector.SelectClip([], [track]);

        Assert.NotNull(clip);
        Assert.Equal(0.5f, clip.Value.Start);
        Assert.Equal(2.5f, clip.Value.Stop);
        Assert.True(clip.Value.Loops);
    }

    // ---- pose evaluator --------------------------------------------------------------------------

    [Fact]
    public void PoseEvaluator_ParentRotation_MovesChildWorld()
    {
        // Parent at origin rotating 90° about Z at t=1; child hangs 10 units down parent-local -Z…
        // use -Y so the rotation visibly relocates it: child local (0,-10,0). After a 90° Z spin,
        // parent-local -Y maps to world +X.
        var quarterTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var animation = new NifMeshAnimation(
            Bones:
            [
                new NifAnimBone("Parent", -1, Vector3.Zero, Quaternion.Identity, 1f),
                new NifAnimBone("Child", 0, new Vector3(0f, -10f, 0f), Quaternion.Identity, 1f),
            ],
            Tracks:
            [
                new NifNodeTrack(
                    "Parent", 1f, 0f,
                    NifKeyInterpolation.Linear,
                    [new(0f, Quaternion.Identity), new(1f, quarterTurn)],
                    NifKeyInterpolation.Linear, [],
                    NifKeyInterpolation.Linear, []),
                null,
            ],
            TextKeys: [],
            ClipStart: 0f, ClipStop: 1f, ClipLoops: false);

        Span<Matrix4x4> worlds = stackalloc Matrix4x4[2];

        NifAnimationPoseEvaluator.EvaluateBoneWorlds(animation, 0f, worlds);
        Assert.Equal(-10f, worlds[1].Translation.Y, 3);

        NifAnimationPoseEvaluator.EvaluateBoneWorlds(animation, 1f, worlds);
        // Row-vector convention: childWorld = childLocal × parentWorld. A 90° Z spin moves the
        // child's offset entirely out of Y into ±X (sign is the rotation handedness — the
        // invariant under test is the parent DRIVING the child, not the sign).
        Assert.Equal(0f, worlds[1].Translation.Y, 2);
        Assert.Equal(10f, MathF.Abs(worlds[1].Translation.X), 2);
    }

    [Fact]
    public void PoseEvaluator_SkinMatrices_ReproduceRestAtBind()
    {
        // A bone at rest translation T with inverse bind = translate(-T): skin = IBP × world =
        // identity ⇒ skinning at rest reproduces the authored vertices.
        var restT = new Vector3(0f, 0f, -30.7f);
        var animation = new NifMeshAnimation(
            [new NifAnimBone("Bone", -1, restT, Quaternion.Identity, 1f)],
            [null],
            [],
            0f, 1f, true);

        Span<Matrix4x4> worlds = stackalloc Matrix4x4[1];
        NifAnimationPoseEvaluator.EvaluateBoneWorlds(animation, 0.5f, worlds);

        var inverseBind = Matrix4x4.CreateTranslation(-restT);
        Span<Matrix4x4> skins = stackalloc Matrix4x4[1];
        NifAnimationPoseEvaluator.ComputeSkinMatrices([inverseBind], [0], worlds, skins);

        Assert.True(skins[0].IsIdentity ||
                    (skins[0].Translation.Length() < 1e-4f && MathF.Abs(skins[0].M11 - 1f) < 1e-4f));
    }
}
