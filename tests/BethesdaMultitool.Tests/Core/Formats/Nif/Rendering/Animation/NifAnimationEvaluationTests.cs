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

    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 0)]
    [InlineData(0.999f, 0)]
    [InlineData(1f, 1)]
    [InlineData(1.5f, 1)]
    [InlineData(1.999f, 1)]
    [InlineData(2f, 2)]
    [InlineData(3f, 2)]
    public void ConstantChannels_HoldEarlierKeyUntilExactNextKey(float time, int expectedIndex)
    {
        // NiStepFloat/Pos/RotKey::Interpolate compares the segment fraction against 1.0f,
        // not 0.5f: samples past the midpoint still use the earlier key.
        var rotations = new[]
        {
            Quaternion.Identity,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 3f)
        };
        var translations = new[] { Vector3.Zero, new Vector3(8f, -4f, 2f), new Vector3(-3f, 7f, 6f) };
        var scales = new[] { 1f, 3f, 2f };
        NifQuatKey[] rotationKeys = [new(0f, rotations[0]), new(1f, rotations[1]), new(2f, rotations[2])];
        NifVec3Key[] translationKeys = [new(0f, translations[0]), new(1f, translations[1]), new(2f, translations[2])];
        NifFloatKey[] scaleKeys = [new(0f, scales[0]), new(1f, scales[1]), new(2f, scales[2])];

        Assert.Equal(Quaternion.Normalize(rotations[expectedIndex]),
            NifTrackSampler.SampleRotation(rotationKeys, time, NifKeyInterpolation.Constant));
        Assert.Equal(translations[expectedIndex],
            NifTrackSampler.SampleTranslation(translationKeys, time, NifKeyInterpolation.Constant));
        Assert.Equal(scales[expectedIndex],
            NifTrackSampler.SampleScale(scaleKeys, time, NifKeyInterpolation.Constant));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PoseEvaluator_UsesEachChannelsAuthoredConstantBasis(int constantChannel)
    {
        var endRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var endTranslation = new Vector3(8f, -4f, 2f);
        var animation = new NifMeshAnimation(
            [new NifAnimBone("Bone", -1, Vector3.Zero, Quaternion.Identity, 1f)],
            [
                new NifNodeTrack(
                    "Bone", 1f, 0f,
                    constantChannel == 0 ? NifKeyInterpolation.Constant : NifKeyInterpolation.Linear,
                    [new NifQuatKey(0f, Quaternion.Identity), new NifQuatKey(1f, endRotation)],
                    constantChannel == 1 ? NifKeyInterpolation.Constant : NifKeyInterpolation.Linear,
                    [new NifVec3Key(0f, Vector3.Zero), new NifVec3Key(1f, endTranslation)],
                    constantChannel == 2 ? NifKeyInterpolation.Constant : NifKeyInterpolation.Linear,
                    [new NifFloatKey(0f, 1f), new NifFloatKey(1f, 3f)])
            ],
            [], 0f, 1f, false);
        Span<Matrix4x4> worlds = stackalloc Matrix4x4[1];

        NifAnimationPoseEvaluator.EvaluateBoneWorlds(animation, 0.75f, worlds);

        var rotation = constantChannel == 0
            ? Quaternion.Identity
            : Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, endRotation, 0.75f));
        var expected = Matrix4x4.CreateFromQuaternion(rotation) *
                       Matrix4x4.CreateScale(constantChannel == 2 ? 1f : 2.5f);
        expected.Translation = constantChannel == 1 ? Vector3.Zero : endTranslation * 0.75f;
        Assert.Equal(expected, worlds[0]);

        NifAnimationPoseEvaluator.EvaluateBoneWorlds(animation, 1f, worlds);
        expected = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(endRotation)) * Matrix4x4.CreateScale(3f);
        expected.Translation = endTranslation;
        Assert.Equal(expected, worlds[0]);
    }

    [Fact]
    public void MapTime_LoopWrapsIntoClipWindow()
    {
        // NiTimeController cycle semantics: controller-local time u = t×freq + phase wraps into
        // [start, stop]. Any wall clock lands inside the window; window-length steps alias.
        var wrapped = NifTrackSampler.MapTime(1.4f, 1f, 0f, 2.333f, 3.667f, true);
        Assert.InRange(wrapped, 2.333f, 3.667f);

        var length = 3.667f - 2.333f;
        var aliased = NifTrackSampler.MapTime(1.4f + length, 1f, 0f, 2.333f, 3.667f, true);
        Assert.Equal(wrapped, aliased, 3);

        // A time already inside the window maps to itself.
        Assert.Equal(3f, NifTrackSampler.MapTime(3f, 1f, 0f, 2.333f, 3.667f, true), 3);
    }

    [Fact]
    public void MapTime_ClampHoldsAtStop()
    {
        Assert.Equal(3f, NifTrackSampler.MapTime(10f, 1f, 0f, 1f, 3f, false), 4);
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
            new(3.667f, Quaternion.Identity), new(4f, Quaternion.Identity)
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
            new(3.666667f, "Idle3: Loop Stop"), new(4f, "Idle3: Stop")
        ];

        var clip = NifAnimationClipSelector.SelectClip(textKeys, [track]);

        Assert.NotNull(clip);
        Assert.Equal(0f, clip.Value.Start, 4);
        Assert.Equal(4f, clip.Value.Stop, 4);
        Assert.True(clip.Value.Loops);
    }

    [Fact]
    public void ClipSelector_NumberedIdleVariants_DoNotDriveSelection()
    {
        // Only the PLAIN "Idle" group selects a window. Numbered variants are the AI's occasional
        // fidgets (the banner's "Idle3" is the violent gust) — with no plain Idle the window is the
        // track key span.
        NifAnimTextKey[] variantOnlyKeys = [new(2.333f, "Idle3: Loop Start"), new(3.667f, "Idle3: Loop Stop")];
        var track = new NifNodeTrack(
            "Bone", 1f, 0f,
            NifKeyInterpolation.Linear,
            [new NifQuatKey(0f, Quaternion.Identity), new NifQuatKey(3f, Quaternion.Identity)],
            NifKeyInterpolation.Linear, [], NifKeyInterpolation.Linear, []);

        var clip = NifAnimationClipSelector.SelectClip(variantOnlyKeys, [track]);

        Assert.NotNull(clip);
        Assert.Equal(0f, clip.Value.Start);
        Assert.Equal(3f, clip.Value.Stop);
    }

    [Fact]
    public void ClipSelector_CreatureKeySet_PicksPlainIdleLoop()
    {
        // Creature-shaped marker set (r\Guar.NIF): every animation group concatenated on one
        // timeline. Ambient playback must loop ONLY the plain Idle group — not tour the repertoire.
        NifAnimTextKey[] keys =
        [
            new(0f, "Idle: Start"),
            new(0f, "Idle: Loop Start"),
            new(3.267f, "Idle: Loop Stop"),
            new(3.267f, "Idle: Stop"),
            new(3.267f, "Idle3: Start"),
            new(5.6f, "Idle3: Stop"),
            new(14.333f, "WalkForward: Start"),
            new(16.467f, "WalkForward: Stop"),
            new(19f, "Attack1: Start"),
            new(19.8f, "Attack1: Stop"),
            new(22.867f, "Death1: Start"),
            new(23.6f, "Death1: Stop")
        ];
        var track = new NifNodeTrack(
            "Bip01 Spine", 1f, 0f,
            NifKeyInterpolation.Linear,
            [new NifQuatKey(0f, Quaternion.Identity), new NifQuatKey(27.333f, Quaternion.Identity)],
            NifKeyInterpolation.Linear, [], NifKeyInterpolation.Linear, []);

        var clip = NifAnimationClipSelector.SelectClip(keys, [track]);

        Assert.NotNull(clip);
        Assert.Equal(0f, clip.Value.Start, 3);
        Assert.Equal(3.267f, clip.Value.Stop, 3);
        Assert.True(clip.Value.Loops);
    }

    [Fact]
    public void ClipSelector_DegeneratePlainIdle_FallsBackToFullRange()
    {
        // Animated decor authors a zero-length plain Idle (the tavern banner: Start/Stop both at 0)
        // — that must NOT freeze the clip; the window falls back to the full key span.
        NifAnimTextKey[] keys = [new(0f, "Idle: Start"), new(0f, "Idle: Stop")];
        var track = new NifNodeTrack(
            "Bone", 1f, 0f,
            NifKeyInterpolation.Linear,
            [new NifQuatKey(0f, Quaternion.Identity), new NifQuatKey(4f, Quaternion.Identity)],
            NifKeyInterpolation.Linear, [], NifKeyInterpolation.Linear, []);

        var clip = NifAnimationClipSelector.SelectClip(keys, [track]);

        Assert.NotNull(clip);
        Assert.Equal(0f, clip.Value.Start);
        Assert.Equal(4f, clip.Value.Stop);
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
            [new NifQuatKey(0.5f, Quaternion.Identity), new NifQuatKey(2.5f, Quaternion.Identity)],
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
            [
                new NifAnimBone("Parent", -1, Vector3.Zero, Quaternion.Identity, 1f),
                new NifAnimBone("Child", 0, new Vector3(0f, -10f, 0f), Quaternion.Identity, 1f)
            ],
            [
                new NifNodeTrack(
                    "Parent", 1f, 0f,
                    NifKeyInterpolation.Linear,
                    [new NifQuatKey(0f, Quaternion.Identity), new NifQuatKey(1f, quarterTurn)],
                    NifKeyInterpolation.Linear, [],
                    NifKeyInterpolation.Linear, []),
                null
            ],
            [],
            0f, 1f, false);

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