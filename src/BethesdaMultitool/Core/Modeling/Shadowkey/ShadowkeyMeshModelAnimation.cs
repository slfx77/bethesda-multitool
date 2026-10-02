using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The clips of an animated Shadowkey mesh document (cut-2 plan section 3.5, decision D5): one
///     <see cref="SceneAnimation" /> per sequence, named <c>seqNN</c>, holding one <see cref="SceneMorphTrack" /> over the
///     N-1 frame targets on each clip node: the default skin's node only
///     (<see cref="ShadowkeyMeshModelLayerResult.ClipNodes" />, cut-2 review finding 1, pending the owner's D4 ruling).
/// </summary>
/// <remarks>
///     <para>
///         Keys: for a sequence <c>[start, end)</c> of length L at rate r, key i at <c>fl32(i) / fl32(r)</c> seconds for
///         i = 0 .. L-1 with one-hot weights on target <c>start + i - 1</c> (all zero where the frame is 0, the base
///         frame), plus ONE DERIVED FINAL KEY at <c>L / r</c> repeating frame <c>end - 1</c>, so the clip spans L frame
///         periods and its last pose holds for one period. Step interpolation.
///         <see cref="SceneAnimation.DurationSeconds" /> stays null: the GLB writer refuses a clip whose authored duration
///         differs from its last key (Shared <c>ModelGltfAdmission</c>), and the derived key carries the end, as cut 1c's
///         <c>.3DC</c> clip does.
///     </para>
///     <para>
///         Timing: <see cref="SceneAnimationTiming" /> with the rate as frames per second and as the raw rate in unit
///         <see cref="RawRateUnit" />, Assumed (design section 4.2, RE-6), with the evidence that argues for it. Gaps that
///         stay (plan section 3.5): the rate unit (G1), the interpolation (G2, diagnostic), loop or once (G3, not
///         declared: no clock is invented), actor assembly (G4, diagnostic on the humanoid table), skin choice (G5) and
///         the two suspect rate-1 sequences (G6, diagnostic). A one-frame record gets no clip; its sequence stays in the
///         <c>bmt.shadowkey.mesh.sequences</c> native row.
///     </para>
/// </remarks>
internal static class ShadowkeyMeshModelAnimation
{
    /// <summary>The raw rate unit every clip states.</summary>
    public const string RawRateUnit = "shadowkey.sequence.rate";

    /// <summary>The timing evidence every clip states.</summary>
    public const string RateEvidence =
        "rate read as frames per second (design section 4.2, RE-6): 8 of 195 multi-frame sequences have rate equal to " +
        "length (1.0 s); durations median 2.1 s, p90/p10 4.6, against 9.7 read as ticks per frame; Step interpolation " +
        "and the derived final key (the last pose held one period) are Assumed";

    /// <summary>
    ///     The humanoid sequence table the four tunic bodies, delfran, orc_ice_warrior and the six weapon records share
    ///     (the first 9 entries or all 11).
    /// </summary>
    public static IReadOnlyList<ShadowkeySequence> HumanoidTable { get; } =
    [
        new(0, 1, 10), new(1, 22, 10), new(22, 31, 10), new(31, 42, 10), new(42, 66, 10), new(66, 98, 10),
        new(98, 106, 10), new(106, 116, 3), new(116, 127, 3), new(127, 138, 10), new(138, 144, 10)
    ];

    /// <summary>The clip name of sequence <paramref name="index" />.</summary>
    public static string ClipName(int index)
    {
        return string.Create(CultureInfo.InvariantCulture, $"seq{index:00}");
    }

    /// <summary>The time of key <paramref name="key" /> at rate <paramref name="rate" />: <c>fl32(key) / fl32(rate)</c>.</summary>
    public static float KeyTime(int key, int rate)
    {
        return key / (float)rate;
    }

    /// <summary>True when the record's sequences are the humanoid table or its first 9 or more entries.</summary>
    public static bool IsHumanoid(IReadOnlyList<ShadowkeySequence> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        if (sequences.Count < 9 || sequences.Count > HumanoidTable.Count)
        {
            return false;
        }

        for (var i = 0; i < sequences.Count; i++)
        {
            if (sequences[i] != HumanoidTable[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when a multi-frame sequence runs at rate 1 (gap G6).</summary>
    public static bool HasSuspectRate(IReadOnlyList<ShadowkeySequence> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        foreach (var sequence in sequences)
        {
            if (sequence.Length > 1 && sequence.Rate == 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The clip of one sequence over <paramref name="frameCount" /> frames, driving every node in <paramref name="nodes" />.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The record has fewer than 2 frames or the rate is zero (a caller contract: the document builder refuses a zero
    ///     rate as invalid data first, <see cref="ShadowkeyMeshDocumentBuilder.RequireDocumentable" />).
    /// </exception>
    public static SceneAnimation Create(int index, ShadowkeySequence sequence, int frameCount, IReadOnlyList<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (frameCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount, "A clip needs at least two frames.");
        }

        if (sequence.Rate == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "A sequence rate of zero cannot be timed.");
        }

        var targets = frameCount - 1;
        var length = sequence.Length;
        var times = new float[length + 1];
        var weights = new float[(length + 1) * targets];
        for (var key = 0; key <= length; key++)
        {
            times[key] = KeyTime(key, sequence.Rate);
            var frame = key < length ? sequence.Start + key : sequence.EndExclusive - 1;
            if (frame > 0)
            {
                weights[key * targets + frame - 1] = 1f;
            }
        }

        var tracks = new List<SceneMorphTrack>(nodes.Count);
        foreach (var node in nodes)
        {
            tracks.Add(new SceneMorphTrack(node, targets, times, weights, SceneInterpolation.Step));
        }

        var extras = new JsonObject
        {
            ["start"] = sequence.Start,
            ["end"] = sequence.EndExclusive,
            ["rate"] = sequence.Rate
        };
        return new SceneAnimation(ClipName(index), tracks, extras.ToJsonString(), durationSeconds: null,
            timing: new SceneAnimationTiming(sequence.Rate, sequence.Rate, RawRateUnit, SceneValueProvenance.Assumed,
                RateEvidence));
    }
}
