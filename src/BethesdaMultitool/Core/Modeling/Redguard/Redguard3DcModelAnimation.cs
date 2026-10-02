using System.Globalization;
using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Redguard;

/// <summary>
///     The pose clip of a Redguard <c>.3DC</c> document (cut-1c plan section 4, "Clip"): one <see cref="SceneAnimation" />
///     named <see cref="ClipName" /> holding one <see cref="SceneMorphTrack" /> on node 0 over the N-1 pose targets, with
///     key times <c>i / 15</c> s for frames <c>i = 0 .. N-1</c>, one-hot weights (all zero at frame 0, which is the
///     keyframe itself), Step interpolation, and ONE DERIVED FINAL KEY at <c>N / 15</c> s repeating frame N-1's weights,
///     so the clip spans N frame periods and the last pose is held for one period.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="SceneAnimation.DurationSeconds" /> stays null on purpose: the GLB writer refuses a keyed clip whose
///         authored duration differs from its last key (Shared <c>ModelGltfAdmission</c>, "An authored clip duration
///         differing from its last standard key requires explicit timeline lowering"), so the draft's duration N/15 with
///         the last key at (N-1)/15 would have made every <c>.3DC</c> unwritable; the derived key carries the N/15 end
///         instead. The rate and the interpolation are Assumed (design section 4.2; RE-3), declared through
///         <see cref="SceneAnimationTiming" />, and the derived key is recorded in the <c>bmt.redguard.3dc.clip</c> native
///         row (<see cref="NativePayload" />).
///     </para>
///     <para>
///         Key times are <c>key / 15f</c>, one correctly rounded float32 division each. Blender writes the Step
///         morph-weight clip natively (Shared SA5: <c>animation-native-curves</c>) on its 32 frames per second timeline,
///         where a key lands at <c>32 * key / 15</c>, a fractional native frame; hop D confirms the readback at those
///         times. The one-hot vectors make the clip's weight payload (N + 1) x (N - 1) values (<see cref="WeightCount" />):
///         229 x 227 = 51,983 on the longest retail stack (TIRBA001, 228 frames). The reader refuses a stack past
///         <see cref="MaximumWeights" /> before it parses (slice-6 review finding 5).
///     </para>
/// </remarks>
internal static class Redguard3DcModelAnimation
{
    /// <summary>The clip's name.</summary>
    public const string ClipName = "poses";

    /// <summary>The assumed playback rate, frames per second.</summary>
    public const int FramesPerSecond = 15;

    /// <summary>
    ///     The most one-hot weights a clip may hold, 2^20 (4 MiB of float32 per copy): (N + 1) x (N - 1) = N^2 - 1 stays
    ///     within it up to <see cref="MaximumFrames" /> frames, about 20 times the longest retail clip's 51,983.
    /// </summary>
    public const long MaximumWeights = 1L << 20;

    /// <summary>The most frames whose clip fits <see cref="MaximumWeights" /> (1,024^2 - 1 = 1,048,575).</summary>
    public const int MaximumFrames = 1024;

    /// <summary>
    ///     The one-hot weights the clip of an <paramref name="frameCount" />-frame stack holds: (N + 1) x (N - 1), and 0
    ///     for a single frame, which has no clip.
    /// </summary>
    public static long WeightCount(int frameCount)
    {
        return frameCount < 2 ? 0 : ((long)frameCount + 1) * ((long)frameCount - 1);
    }

    /// <summary>The evidence the clip's timing and native row carry.</summary>
    public const string RateEvidence =
        "15 frames per second and Step interpolation are Assumed (design section 4.2; RE-3, RG.EXE, pending): frame i " +
        "is keyed at i/15 s and held until the next frame, and a derived final key at N/15 s holds the last pose for one " +
        "frame period at the assumed rate";

    /// <summary>The time of key <paramref name="key" /> in seconds: <c>key / 15f</c>.</summary>
    public static float KeyTime(int key)
    {
        return key / (float)FramesPerSecond;
    }

    /// <summary>
    ///     The clip of an <paramref name="frameCount" />-frame stack whose mesh node is <paramref name="nodeIndex" />, or
    ///     null for a single-frame stack, which has no later pose to show.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The frame count is not positive.</exception>
    public static SceneAnimation? Create(int frameCount, int nodeIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        if (frameCount < 2)
        {
            return null;
        }

        var targets = frameCount - 1;
        var times = new float[frameCount + 1];
        var weights = new float[(frameCount + 1) * targets];
        for (var key = 0; key <= frameCount; key++)
        {
            times[key] = KeyTime(key);

            // Key N, the derived final key, repeats frame N-1.
            var frame = Math.Min(key, frameCount - 1);
            if (frame > 0)
            {
                weights[key * targets + frame - 1] = 1f;
            }
        }

        var track = new SceneMorphTrack(nodeIndex, targets, times, weights, SceneInterpolation.Step);
        return new SceneAnimation(ClipName, [track], durationSeconds: null,
            timing: new SceneAnimationTiming(FramesPerSecond, provenance: SceneValueProvenance.Assumed,
                evidence: RateEvidence));
    }

    /// <summary>The <c>bmt.redguard.3dc.clip</c> payload: the rate, the interpolation and the derived final key.</summary>
    public static JsonObject NativePayload(int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        if (frameCount < 2)
        {
            return new JsonObject
            {
                ["frames"] = frameCount,
                ["clip"] = null,
                ["note"] = "a single-frame stack has no later pose, so no clip is written"
            };
        }

        return new JsonObject
        {
            ["frames"] = frameCount,
            ["clip"] = ClipName,
            ["targets"] = frameCount - 1,
            ["framesPerSecond"] = FramesPerSecond,
            ["rateProvenance"] = SceneValueProvenance.Assumed.ToString(),
            ["interpolation"] = SceneInterpolation.Step.ToString(),
            ["interpolationProvenance"] = SceneValueProvenance.Assumed.ToString(),
            ["keys"] = frameCount + 1,
            ["keyTimes"] = "key / 15 seconds for key = 0 .. N, one float32 division each",
            ["weights"] = "one-hot: key i selects target i-1 for i = 1 .. N-1, key 0 selects none, key N repeats key N-1",
            ["derivedFinalKey"] = new JsonObject
            {
                ["key"] = frameCount,
                ["timeSeconds"] = KeyTime(frameCount),
                ["repeatsFrame"] = frameCount - 1,
                ["reason"] = "holds the last pose for one frame period at the assumed rate, so the clip spans " +
                             string.Create(CultureInfo.InvariantCulture, $"{frameCount}") + " frame periods"
            },
            ["durationSeconds"] = null,
            ["durationReason"] =
                "not authored: the GLB writer refuses a keyed clip whose authored duration differs from its last key; " +
                "the derived final key carries the end instead",
            ["evidence"] = RateEvidence
        };
    }
}
