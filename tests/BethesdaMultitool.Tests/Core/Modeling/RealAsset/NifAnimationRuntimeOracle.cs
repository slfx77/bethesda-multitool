using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A3-anim (plan section 3, slice 9): the renderer's runtime clip reader
///     (<see cref="NifControllerSequenceNameTrackReader.ReadAll" />, which decodes B-splines through
///     <see cref="NifBsplineTransformReader" />) against the document. The runtime path shares only
///     <see cref="NifParser" /> with the document path (its own offsets, <c>BinaryUtils</c> reads and decode).
/// </summary>
/// <remarks>
///     <para>
///         Per sequence clip of the document (matched to the runtime clip of the same name, in block order among equal
///         names) and per transform track (matched through the probe's controlled blocks to the runtime track list, which
///         keeps NiTransformInterpolator and B-spline blocks in controlled-block order):
///     </para>
///     <list type="bullet">
///         <item>
///             Each compact B-spline control of the document decoded as <c>bias + s / 32767f * multiplier</c> (Float32)
///             equals the runtime's decoded control bit for bit; a Float32 control is equal as stored. Rotation controls
///             compare component by component (the document's X, Y, Z, W against the runtime's X, Y, Z, W), a scale
///             control's three replicated components against the runtime's one.
///         </item>
///         <item>
///             A keyed channel's key times and key values equal the runtime's keys bit for bit: translation and scale as
///             stored (the value of a Hermite triple), a LINEAR or CONST rotation as RE-17 steps 1 to 3 of the runtime's
///             raw keys, a Squad rotation's key as RE-17 steps 1 and 2 of them, and each Euler axis as stored.
///         </item>
///     </list>
///     <para>The runtime reads no <c>.nif</c> embedded controller, so the <c>(controllers)</c> clip is not compared.</para>
/// </remarks>
internal static class NifAnimationRuntimeOracle
{
    /// <summary>The NotCompared reason of a sequence the runtime reader refuses for an empty text-key label.</summary>
    public const string RuntimeEmptyTextKeyReason =
        "sequence with an empty text-key label: the runtime reader refuses the whole sequence";

    /// <summary>The NotCompared reason of a sequence the runtime reader refuses for a NULL or blank name.</summary>
    public const string RuntimeNameReason = "sequence with a NULL or blank name: the runtime reader refuses it";

    /// <summary>The NotCompared reason of a sequence the runtime reader refuses for its clock.</summary>
    public const string RuntimeClockReason =
        "sequence with a negative frequency, a clock that does not advance or an undefined cycle: the runtime reader refuses it";

    /// <summary>The NotCompared reason of a controlled block the name-targeted runtime reader does not read.</summary>
    public const string RuntimeSkipsReason = "controlled block with a NULL or blank Node Name: the runtime reader skips it";

    /// <summary>Compares one read with the runtime reader.</summary>
    /// <param name="read">The read.</param>
    /// <returns>The report (the diffs and what was compared).</returns>
    public static NifAnimationDocumentOracleReport Compare(Cut1bAnimationRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var report = new NifAnimationDocumentOracleReport();
        var nif = NifParser.Parse(read.Bytes);
        if (nif is null)
        {
            report.Diffs.Add("NifParser could not parse the file");
            return report;
        }

        var runtime = NifControllerSequenceNameTrackReader.ReadAll(read.Bytes, nif);
        var byName = new Dictionary<string, Queue<NifNameTargetedAnimationClip>>(StringComparer.Ordinal);
        foreach (var clip in runtime)
        {
            if (!byName.TryGetValue(clip.Name, out var queue))
            {
                queue = new Queue<NifNameTargetedAnimationClip>();
                byName.Add(clip.Name, queue);
            }

            queue.Enqueue(clip);
        }

        var facts = NifAnimationExpectations.Facts(read.Expectation);
        var sequences = read.Result.Clips
            .Select(static (clip, index) => (Clip: clip, Index: index, Extras: NifAnimationDocumentOracle.Extras(clip)))
            .Where(static c => c.Extras["source"]?.GetValue<string>() == "sequence")
            .OrderBy(static c => c.Extras["block"]!.GetValue<int>())
            .ToList();
        foreach (var (clip, index, extras) in sequences)
        {
            report.Clips++;
            var label = $"clip {index} '{clip.Name}'";
            var block = extras["block"]!.GetValue<int>();
            if (!facts.TryGetValue(block, out var sequence) || sequence["controlledBlocks"] is not JsonArray blocks)
            {
                report.Diffs.Add($"{label}: the probe has no controlled blocks for sequence {block}");
                continue;
            }

            if (RuntimeRefusal(sequence, facts) is { } refusal)
            {
                // The renderer's reader refuses the whole sequence for a documented reason (plan section 0.4).
                report.Skip(refusal);
                continue;
            }

            if (!byName.TryGetValue(clip.Name, out var queue) || !queue.TryDequeue(out var twin))
            {
                report.Diffs.Add($"{label}: the runtime reader returned no clip of that name");
                continue;
            }

            var positions = RuntimePositions(nif, blocks);
            CompareTracks(report, label, clip, extras, blocks, positions, twin);
        }

        return report;
    }

    /// <summary>
    ///     Compares one document B-spline curve's controls, read from <paramref name="shorts" /> (the document's own, or a
    ///     control's changed copy), with the runtime's decoded controls of the same channel.
    /// </summary>
    /// <param name="report">The report to add to.</param>
    /// <param name="label">The track label.</param>
    /// <param name="curve">The document curve.</param>
    /// <param name="shorts">The compact controls to decode (ignored for a Float32 curve).</param>
    /// <param name="property">The channel.</param>
    /// <param name="runtime">The runtime track's data.</param>
    public static void CompareControls(NifAnimationDocumentOracleReport report, string label, SceneBSplineCurve curve,
        IReadOnlyList<short> shorts, SceneTransformProperty property, NifBsplineTransformData runtime)
    {
        var components = RuntimeControls(runtime, property);
        if (components is null)
        {
            report.Diffs.Add($"{label}: the runtime has no {property} controls");
            return;
        }

        if (components.Length != curve.ControlPointCount)
        {
            report.Diffs.Add($"{label}: {curve.ControlPointCount} document controls, {components.Length} runtime");
            return;
        }

        var width = curve.ComponentCount;
        for (var control = 0; control < components.Length; control++)
        {
            for (var component = 0; component < width; component++)
            {
                var index = control * width + component;
                float value;
                if (curve.IsQuantized)
                {
                    var bias = curve.Bias!.Value;
                    var multiplier = curve.Multiplier!.Value;
                    value = bias + shorts[index] / 32767f * multiplier;
                }
                else
                {
                    value = curve.ControlPoints[index];
                }

                var expected = components[control][property == SceneTransformProperty.Scale ? 0 : component];
                if (BitConverter.SingleToUInt32Bits(value) != BitConverter.SingleToUInt32Bits(expected))
                {
                    report.Diffs.Add($"{label}: control {control} component {component} decodes to " +
                                     $"0x{BitConverter.SingleToUInt32Bits(value):X8}, the runtime to " +
                                     $"0x{BitConverter.SingleToUInt32Bits(expected):X8}");
                    return;
                }
            }
        }

        report.Count("bspline", curve.IsQuantized ? "compact" : "float32");
    }

    /// <summary>The runtime's controls of one channel as float rows (X, Y, Z[, W] or the one scale value), or null.</summary>
    public static float[][]? RuntimeControls(NifBsplineTransformData runtime, SceneTransformProperty property)
    {
        return property switch
        {
            SceneTransformProperty.Translation => runtime.TranslationControlPoints?
                .Select(static v => new[] { v.X, v.Y, v.Z }).ToArray(),
            SceneTransformProperty.Rotation => runtime.RotationControlPoints?
                .Select(static q => new[] { q.X, q.Y, q.Z, q.W }).ToArray(),
            _ => runtime.ScaleControlPoints?.Select(static s => new[] { s }).ToArray()
        };
    }

    /// <summary>
    ///     Why the runtime reader refuses a whole sequence the document typed, from the probe's facts: a NULL or blank
    ///     name, a text key whose
    ///     label is NULL, empty or white space (NifTextKeyReader rejects empty keys, plan section 0.4), a frequency below
    ///     zero, a clock whose stop is not after its start, or a cycle outside LOOP, REVERSE and CLAMP; null otherwise.
    /// </summary>
    public static string? RuntimeRefusal(JsonObject sequence, IReadOnlyDictionary<int, JsonObject> facts)
    {
        if (string.IsNullOrWhiteSpace(sequence["name"]?.GetValue<string>()))
        {
            return RuntimeNameReason;
        }

        var frequency = BitConverter.UInt32BitsToSingle(sequence["frequencyBits"]!.GetValue<uint>());
        var start = BitConverter.UInt32BitsToSingle(sequence["startTimeBits"]!.GetValue<uint>());
        var stop = BitConverter.UInt32BitsToSingle(sequence["stopTimeBits"]!.GetValue<uint>());
        if (!(frequency >= 0f) || !(stop - start > 0f))
        {
            return RuntimeClockReason;
        }

        if (sequence["cycleType"]?.GetValue<string>() is not ("LOOP" or "REVERSE" or "CLAMP"))
        {
            return RuntimeClockReason;
        }

        var textKeys = sequence["textKeys"]?.GetValue<int>() ?? -1;
        if (textKeys >= 0 && facts.TryGetValue(textKeys, out var keys) && keys["textKeys"] is JsonArray labels &&
            labels.Any(static key => string.IsNullOrWhiteSpace(key!["value"]?.GetValue<string>())))
        {
            return RuntimeEmptyTextKeyReason;
        }

        return null;
    }

    /// <summary>
    ///     Where each controlled block lands in the runtime clip: its position in the runtime's transform-track list or
    ///     B-spline list, following ReadAll's rule (a block whose node name or interpolator does not resolve is skipped;
    ///     B-spline transform interpolators go to one list, NiTransformInterpolators to the other; everything else to
    ///     neither).
    /// </summary>
    public static Dictionary<int, (bool Bspline, int Position)> RuntimePositions(NifInfo nif, JsonArray blocks)
    {
        var positions = new Dictionary<int, (bool, int)>();
        var tracks = 0;
        var splines = 0;
        for (var ordinal = 0; ordinal < blocks.Count; ordinal++)
        {
            var block = blocks[ordinal]!.AsObject();
            var interpolator = block["interpolator"]?.GetValue<int>() ?? -1;
            if (block["nodeName"]?.GetValue<string>() is not { } name || string.IsNullOrWhiteSpace(name) ||
                interpolator < 0 || interpolator >= nif.Blocks.Count)
            {
                continue;
            }

            var type = nif.Blocks[interpolator].TypeName;
            if (type is "NiBSplineTransformInterpolator" or "NiBSplineCompTransformInterpolator")
            {
                positions[ordinal] = (true, splines++);
            }
            else if (type == "NiTransformInterpolator")
            {
                positions[ordinal] = (false, tracks++);
            }
        }

        return positions;
    }

    private static void CompareTracks(NifAnimationDocumentOracleReport report, string label, SceneAnimation clip,
        JsonObject extras, JsonArray blocks, Dictionary<int, (bool Bspline, int Position)> positions,
        NifNameTargetedAnimationClip twin)
    {
        var entries = extras["tracks"]?.AsArray() ?? new JsonArray();
        for (var index = 0; index < entries.Count && index < clip.TransformTracks.Count; index++)
        {
            var entry = entries[index]!.AsObject();
            var track = clip.TransformTracks[index];
            var ordinal = entry["controlledBlock"]!.GetValue<int>();
            var trackLabel = $"{label} track {index} (controlled block {ordinal}, {track.Property})";
            if (!positions.TryGetValue(ordinal, out var position))
            {
                // The runtime reader targets by name: a NULL or blank Node Name (bound through the palette) is skipped.
                report.Skip(RuntimeSkipsReason);
                continue;
            }

            var nodeName = blocks[ordinal]!["nodeName"]!.GetValue<string>();
            if (position.Bspline)
            {
                if (track.Spline is not { } spline)
                {
                    continue;
                }

                if (twin.BsplineTracks is not { } runtimeSplines || position.Position >= runtimeSplines.Length)
                {
                    report.Diffs.Add($"{trackLabel}: the runtime clip has no B-spline track {position.Position}");
                    continue;
                }

                var runtimeSpline = runtimeSplines[position.Position];
                if (!string.Equals(runtimeSpline.NodeName, nodeName, StringComparison.Ordinal))
                {
                    report.Diffs.Add($"{trackLabel}: runtime B-spline track {position.Position} names " +
                                     $"'{runtimeSpline.NodeName}', the probe '{nodeName}'");
                    continue;
                }

                CompareControls(report, trackLabel, spline, spline.QuantizedControlPoints, track.Property,
                    runtimeSpline.Transform);
                continue;
            }

            if (position.Position >= twin.Tracks.Length)
            {
                report.Diffs.Add($"{trackLabel}: the runtime clip has no track {position.Position}");
                continue;
            }

            var runtimeTrack = twin.Tracks[position.Position];
            if (!string.Equals(runtimeTrack.NodeName, nodeName, StringComparison.Ordinal))
            {
                report.Diffs.Add($"{trackLabel}: runtime track {position.Position} names '{runtimeTrack.NodeName}', " +
                                 $"the probe '{nodeName}'");
                continue;
            }

            if (track.State == SceneAnimationChannelState.Keyed && track.Times.Count > 0)
            {
                CompareKeys(report, trackLabel, track, runtimeTrack);
            }
        }

        var eulerEntries = extras["eulerTracks"]?.AsArray() ?? new JsonArray();
        for (var index = 0; index < eulerEntries.Count && index < clip.EulerRotationTracks.Count; index++)
        {
            var ordinal = eulerEntries[index]!["controlledBlock"]!.GetValue<int>();
            var trackLabel = $"{label} Euler track {index} (controlled block {ordinal})";
            if (!positions.TryGetValue(ordinal, out var position))
            {
                report.Skip(RuntimeSkipsReason);
                continue;
            }

            if (position.Bspline || position.Position >= twin.Tracks.Length)
            {
                report.Diffs.Add($"{trackLabel}: no runtime track");
                continue;
            }

            var runtimeTrack = twin.Tracks[position.Position];
            var track = clip.EulerRotationTracks[index];
            CompareAxis(report, $"{trackLabel} X", track.X, runtimeTrack.EulerXKeys);
            CompareAxis(report, $"{trackLabel} Y", track.Y, runtimeTrack.EulerYKeys);
            CompareAxis(report, $"{trackLabel} Z", track.Z, runtimeTrack.EulerZKeys);
        }
    }

    private static void CompareKeys(NifAnimationDocumentOracleReport report, string label, SceneTransformTrack track,
        NifNodeTrack runtime)
    {
        var count = track.Times.Count;
        var width = track.Property == SceneTransformProperty.Rotation ? 4 : 3;
        var parts = track.Interpolation switch
        {
            SceneInterpolation.Hermite => 3,
            SceneInterpolation.GamebryoSquad => 3,
            _ => 1
        };
        float[] times;
        float[][] values;
        switch (track.Property)
        {
            case SceneTransformProperty.Translation:
                times = runtime.TranslationKeys.Select(static k => k.Time).ToArray();
                values = runtime.TranslationKeys.Select(static k => new[] { k.Value.X, k.Value.Y, k.Value.Z })
                    .ToArray();
                break;
            case SceneTransformProperty.Scale:
                times = runtime.ScaleKeys.Select(static k => k.Time).ToArray();
                values = runtime.ScaleKeys.Select(static k => new[] { k.Value, k.Value, k.Value }).ToArray();
                break;
            default:
                times = runtime.RotationKeys.Select(static k => k.Time).ToArray();
                var raw = runtime.RotationKeys.Select(static k => new[] { k.Value.W, k.Value.X, k.Value.Y, k.Value.Z })
                    .ToList();
                values = track.Interpolation == SceneInterpolation.GamebryoSquad
                    ? AlignedXyzw(raw)
                    : NifAnimationEngineRules.LinearRotationKeys(raw);
                break;
        }

        if (times.Length != count)
        {
            report.Diffs.Add($"{label}: {count} document keys, {times.Length} runtime");
            return;
        }

        for (var key = 0; key < count; key++)
        {
            if (BitConverter.SingleToUInt32Bits(track.Times[key]) != BitConverter.SingleToUInt32Bits(times[key]))
            {
                report.Diffs.Add($"{label}: key {key} time differs from the runtime's");
                return;
            }

            var offset = key * parts * width + (parts == 3 ? width : 0);
            for (var component = 0; component < width; component++)
            {
                if (BitConverter.SingleToUInt32Bits(track.Values[offset + component]) !=
                    BitConverter.SingleToUInt32Bits(values[key][component]))
                {
                    report.Diffs.Add($"{label}: key {key} component {component} is " +
                                     $"0x{BitConverter.SingleToUInt32Bits(track.Values[offset + component]):X8}, " +
                                     $"the runtime's 0x{BitConverter.SingleToUInt32Bits(values[key][component]):X8}");
                    return;
                }
            }
        }

        report.Count("keys", $"{track.Property}/{track.Interpolation}");
    }

    private static void CompareAxis(NifAnimationDocumentOracleReport report, string label, SceneCurve axis,
        NifFloatKey[]? runtime)
    {
        if (axis.State != SceneAnimationChannelState.Keyed)
        {
            if (runtime is { Length: > 0 })
            {
                report.Diffs.Add($"{label}: the document axis is constant, the runtime has {runtime.Length} keys");
            }

            return;
        }

        var parts = axis.Interpolation == SceneInterpolation.Hermite ? 3 : 1;
        if (runtime is null || runtime.Length != axis.Times.Count)
        {
            report.Diffs.Add($"{label}: {axis.Times.Count} document keys, {runtime?.Length ?? 0} runtime");
            return;
        }

        for (var key = 0; key < runtime.Length; key++)
        {
            var value = axis.Values[key * parts + (parts == 3 ? 1 : 0)];
            if (BitConverter.SingleToUInt32Bits(axis.Times[key]) != BitConverter.SingleToUInt32Bits(runtime[key].Time) ||
                BitConverter.SingleToUInt32Bits(value) != BitConverter.SingleToUInt32Bits(runtime[key].Value))
            {
                report.Diffs.Add($"{label}: key {key} differs from the runtime's");
                return;
            }
        }

        report.Count("keys", $"Euler/{axis.Interpolation}");
    }

    /// <summary>RE-17 steps 1 and 2 of raw W, X, Y, Z keys (no normalization), in X, Y, Z, W order.</summary>
    private static float[][] AlignedXyzw(List<float[]> raw)
    {
        var keys = raw.Select(static key => (float[])key.Clone()).ToArray();
        if (keys.Length > 1)
        {
            NifAnimationEngineRules.AlignChainAndClampW(keys);
        }

        return keys.Select(static q => new[] { q[1], q[2], q[3], q[0] }).ToArray();
    }
}
