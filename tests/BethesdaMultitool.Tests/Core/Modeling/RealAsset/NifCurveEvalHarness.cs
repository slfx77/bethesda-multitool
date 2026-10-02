using System.Text;
using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A8-sample's C# half: samples every keyed transform track and every Euler track of a read through Shared's
///     <see cref="ScenePoseEvaluator" /> (<see cref="NifAnimationPoseSampler" />), writes the curves and the sampled poses as
///     JSON (schema <c>nif-curve-eval/1</c>), runs the independent evaluator <c>tools/scripts/nif_curve_eval.py</c>
///     (<see cref="NifCurveEvalProcess" />) and returns its verdict.
/// </summary>
/// <remarks>
///     <para>
///         Sample times: every key time and the quarter points of every segment (a B-spline: every knot of its uniform
///         parameterization and the quarter points between), each inverted through the clip and track clocks to a
///         nonnegative absolute time and sent at the track-local time the evaluator actually sampled.
///     </para>
///     <para>
///         Not sampled, and counted by reason: a Squad rotation under the X360 estimate policy (Shared samples it at the
///         unit-normalization centre, which this oracle's evaluator does not model), and a track on a node without an authored TRS (the evaluator drives components only). A channel that is
///         Constant or NotDriven has no curve to evaluate. Property and morph curves are not sampled here: A1-anim
///         compares their payloads bit for bit and the evaluator exposes them only through materials and meshes that the
///         node-only document does not carry.
///     </para>
/// </remarks>
internal static class NifCurveEvalHarness
{
    /// <summary>The input schema the script accepts.</summary>
    public const string InputSchema = "nif-curve-eval/1";

    /// <summary>The NotSampled reason of a Squad rotation under the X360 estimate policy.</summary>
    public const string X360SquadReason = "Squad under the X360 estimate policy: Shared samples the unit-normalization centre, which this oracle does not model";

    /// <summary>The NotSampled reason of a track on a node without an authored TRS.</summary>
    public const string MatrixNodeReason = "node without an authored TRS";

    /// <summary>
    ///     Builds the input, runs the evaluator (skipping when no Python with numpy is available) and returns the summary
    ///     of what was sampled and the evaluator's output (null when nothing was sampled).
    /// </summary>
    /// <param name="read">The read.</param>
    /// <returns>The sampling summary and the evaluator output.</returns>
    public static (JsonObject Summary, JsonObject? Result) Run(Cut1bAnimationRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var (input, summary) = BuildInput(read);
        if (input["tracks"]!.AsArray().Count == 0)
        {
            return (summary, null);
        }

        Assert.SkipWhen(NifCurveEvalProcess.Interpreter is null,
            "No Python interpreter with numpy is on the PATH (python, py -3, python3), so hop A8's independent " +
            "evaluator cannot run.");
        Assert.True(File.Exists(NifCurveEvalProcess.ScriptPath),
            $"{NifCurveEvalProcess.RelativeScriptPath} is missing from the checkout.");
        var directory = Path.Combine(Path.GetTempPath(), "bmt-cut1b-a8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.json");
            var outputPath = Path.Combine(directory, "output.json");
            File.WriteAllText(inputPath, input.ToJsonString(), new UTF8Encoding(false));
            var (exitCode, error) = NifCurveEvalProcess.Run(inputPath, outputPath,
                TestContext.Current.CancellationToken);
            Assert.True(exitCode == 0, $"{read}: nif_curve_eval.py exited {exitCode}: {error}");
            var result = JsonNode.Parse(File.ReadAllText(outputPath))!.AsObject();
            return (summary, result);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>The evaluator input for one read and the summary of what was sampled.</summary>
    /// <param name="read">The read.</param>
    /// <returns>The input document and the summary.</returns>
    public static (JsonObject Input, JsonObject Summary) BuildInput(Cut1bAnimationRead read)
    {
        var facts = NifAnimationExpectations.Facts(read.Expectation);
        var payloads = NifAnimationExpectations.Payloads(read.Expectation);
        var tracks = new JsonArray();
        var notSampled = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var errors = new JsonArray();
        var sampleCount = 0;
        for (var clipIndex = 0; clipIndex < read.Result.Clips.Count; clipIndex++)
        {
            var clip = read.Result.Clips[clipIndex];
            var entries = NifAnimationDocumentOracle.Extras(clip)["tracks"]?.AsArray() ?? new JsonArray();
            for (var index = 0; index < clip.TransformTracks.Count; index++)
            {
                var track = clip.TransformTracks[index];
                if (track.State != SceneAnimationChannelState.Keyed)
                {
                    continue;
                }

                var id = $"clip {clipIndex} '{clip.Name}' track {index} {track.Property} node {track.NodeIndex}";
                if (track.Interpolation == SceneInterpolation.GamebryoSquad &&
                    track.GamebryoSquadPolicy != SceneGamebryoSquadPolicy.PcFloat32)
                {
                    Count(notSampled, X360SquadReason);
                    continue;
                }

                if (read.Graph.Nodes[track.NodeIndex].LocalTrs is not { } rest)
                {
                    Count(notSampled, MatrixNodeReason);
                    continue;
                }

                var desired = track.Spline is { } spline
                    ? NifAnimationPoseSampler.KnotTimes(spline)
                    : NifAnimationPoseSampler.KeyTimes(track.Times);
                var squadTbc = index < entries.Count && track.Interpolation == SceneInterpolation.GamebryoSquad
                    ? SquadTbc(entries[index]!["interpolator"]!.GetValue<int>(), facts, payloads)
                    : null;
                var curve = CurveJson(track.Interpolation, track.Property == SceneTransformProperty.Rotation ? 4 : 3,
                    track.State, track.Times, track.Values, track.StaticValue, track.Spline, track.TbcParameters,
                    track.TbcEndpoints);
                curve["squadTbc"] = squadTbc;
                var kind = track.Property switch
                {
                    SceneTransformProperty.Translation => "translation",
                    SceneTransformProperty.Rotation => "rotation",
                    _ => "scale"
                };
                var samples = SampleTrack(NifAnimationPoseSampler.Isolated(read, clip, track), track.NodeIndex, rest,
                    kind, desired, clip.Clock, track.Clock, id, errors);
                if (samples is null)
                {
                    continue;
                }

                sampleCount += samples.Count;
                tracks.Add(new JsonObject { ["id"] = id, ["kind"] = kind, ["curve"] = curve, ["samples"] = samples });
            }

            for (var index = 0; index < clip.EulerRotationTracks.Count; index++)
            {
                var track = clip.EulerRotationTracks[index];
                var id = $"clip {clipIndex} '{clip.Name}' Euler track {index} node {track.NodeIndex}";
                if (read.Graph.Nodes[track.NodeIndex].LocalTrs is not { } rest)
                {
                    Count(notSampled, MatrixNodeReason);
                    continue;
                }

                var times = new SortedSet<float>(track.X.Times.Concat(track.Y.Times).Concat(track.Z.Times));
                var desired = times.Count == 0 ? new List<float> { 0f } : NifAnimationPoseSampler.KeyTimes(times.ToList());
                var axes = new JsonArray(new[] { track.X, track.Y, track.Z }.Select(static axis =>
                    (JsonNode?)CurveJson(axis.Interpolation, axis.ComponentCount, axis.State, axis.Times, axis.Values,
                        axis.StaticValue, axis.Spline, axis.TbcParameters, axis.TbcEndpoints)).ToArray());
                var samples = SampleTrack(NifAnimationPoseSampler.Isolated(read, clip, track), track.NodeIndex, rest,
                    "euler", desired, clip.Clock, track.Clock, id, errors);
                if (samples is null)
                {
                    continue;
                }

                sampleCount += samples.Count;
                tracks.Add(new JsonObject { ["id"] = id, ["kind"] = "euler", ["axes"] = axes, ["samples"] = samples });
            }
        }

        var reasons = new JsonObject();
        foreach (var (reason, count) in notSampled)
        {
            reasons[reason] = count;
        }

        var input = new JsonObject
        {
            ["schema"] = InputSchema,
            ["file"] = new JsonObject { ["entry"] = read.File.Entry, ["sha256"] = read.File.Sha256 },
            ["tracks"] = tracks
        };
        var summary = new JsonObject
        {
            ["tracksSampled"] = tracks.Count,
            ["samples"] = sampleCount,
            ["notSampled"] = reasons,
            ["evaluatorErrors"] = errors
        };
        return (input, summary);
    }

    /// <summary>
    ///     Samples one isolated track at the desired times: each inverted to an absolute time, deduplicated by the mapped
    ///     track time, observed as translation, scale or rotation rows. Null (with the error recorded) when Shared's
    ///     evaluator refuses the document or a sample.
    /// </summary>
    private static JsonArray? SampleTrack(ModelDocument document, int node, SceneTrs rest, string kind,
        IReadOnlyList<float> desired, SceneAnimationClock? clipClock, SceneAnimationClock? trackClock, string id,
        JsonArray errors)
    {
        var absolute = new List<float>();
        var mapped = new List<float>();
        var seen = new HashSet<uint>();
        foreach (var local in desired)
        {
            var time = NifAnimationPoseSampler.Absolute(local, clipClock, trackClock);
            var trackTime = NifAnimationPoseSampler.Mapped(time, clipClock, trackClock);
            if (seen.Add(BitConverter.SingleToUInt32Bits(trackTime)))
            {
                absolute.Add(time);
                mapped.Add(trackTime);
            }
        }

        List<System.Numerics.Matrix4x4> matrices;
        try
        {
            matrices = NifAnimationPoseSampler.Sample(document, node, absolute);
        }
        catch (Exception failure) when (failure is InvalidDataException or ArithmeticException or
                                            NotSupportedException or ArgumentException)
        {
            errors.Add($"{id}: {failure.GetType().Name}: {failure.Message}");
            return null;
        }

        var samples = new JsonArray();
        for (var index = 0; index < matrices.Count; index++)
        {
            var sample = new JsonObject { ["t"] = BitConverter.SingleToUInt32Bits(mapped[index]) };
            var matrix = matrices[index];
            switch (kind)
            {
                case "translation":
                    sample["observed"] = Doubles(NifAnimationPoseSampler.Translation(matrix));
                    break;
                case "scale":
                    sample["observed"] = Doubles(NifAnimationPoseSampler.Scale(matrix, rest));
                    break;
                default:
                    if (NifAnimationPoseSampler.RotationRows(matrix, rest) is not { } rows)
                    {
                        continue;
                    }

                    sample["observedRows"] = new JsonArray(rows.Select(static row => (JsonNode?)Doubles(row)).ToArray());
                    break;
            }

            samples.Add(sample);
        }

        return samples;
    }

    /// <summary>A curve as the script reads it: every float as its bits.</summary>
    private static JsonObject CurveJson(SceneInterpolation interpolation, int width, SceneAnimationChannelState state,
        IReadOnlyList<float> times, IReadOnlyList<float> values, IReadOnlyList<float>? staticValue,
        SceneBSplineCurve? spline, IReadOnlyList<SceneTbcParameters>? tbc, SceneTbcEndpointTangents? endpoints)
    {
        var node = new JsonObject
        {
            ["interpolation"] = interpolation.ToString(),
            ["width"] = width,
            ["state"] = state.ToString(),
            ["times"] = Bits(times),
            ["values"] = Bits(values),
            ["static"] = staticValue is null ? null : Bits(staticValue),
            ["tbc"] = tbc is null
                ? null
                : Bits(tbc.SelectMany(static p => new[] { p.Tension, p.Continuity, p.Bias }).ToArray()),
            ["startOutgoing"] = endpoints is null ? null : Bits(endpoints.StartOutgoing),
            ["endIncoming"] = endpoints is null ? null : Bits(endpoints.EndIncoming)
        };
        if (spline is not null)
        {
            node["spline"] = new JsonObject
            {
                ["degree"] = spline.Degree,
                ["count"] = spline.ControlPointCount,
                ["start"] = BitConverter.SingleToUInt32Bits(spline.StartSeconds),
                ["stop"] = BitConverter.SingleToUInt32Bits(spline.StopSeconds),
                ["quantized"] = spline.IsQuantized,
                ["shorts"] = new JsonArray(spline.QuantizedControlPoints
                    .Select(static s => (JsonNode?)JsonValue.Create((int)s)).ToArray()),
                ["bias"] = spline.Bias is { } bias ? BitConverter.SingleToUInt32Bits(bias) : null,
                ["multiplier"] = spline.Multiplier is { } multiplier ? BitConverter.SingleToUInt32Bits(multiplier) : null,
                ["floats"] = Bits(spline.ControlPoints)
            };
        }

        return node;
    }

    /// <summary>The probe's stored (T, C, B) bits of a TBC quaternion group, flattened per key; null for other groups.</summary>
    private static JsonArray? SquadTbc(int interpolator, IReadOnlyDictionary<int, JsonObject> facts,
        IReadOnlyDictionary<int, JsonObject> payloads)
    {
        if (!facts.TryGetValue(interpolator, out var fact) || fact["data"]?.GetValue<int>() is not { } data ||
            !payloads.TryGetValue(data, out var payload) || payload["rotation"] is not JsonObject rotation ||
            rotation["keyType"]?.GetValue<uint>() != 3 || rotation["keys"] is not JsonArray keys)
        {
            return null;
        }

        var bits = new JsonArray();
        foreach (var key in keys)
        {
            foreach (var word in NifAnimationExpectations.BitsArray(key!["tbcBits"]))
            {
                bits.Add(word);
            }
        }

        return bits;
    }

    private static JsonArray Bits(IEnumerable<float> values)
    {
        return new JsonArray(values.Select(static v => (JsonNode?)JsonValue.Create(BitConverter.SingleToUInt32Bits(v)))
            .ToArray());
    }

    private static JsonArray Doubles(IEnumerable<double> values)
    {
        return new JsonArray(values.Select(static v => (JsonNode?)JsonValue.Create(v)).ToArray());
    }

    private static void Count(SortedDictionary<string, int> counts, string reason)
    {
        counts[reason] = counts.TryGetValue(reason, out var seen) ? seen + 1 : 1;
    }
}
