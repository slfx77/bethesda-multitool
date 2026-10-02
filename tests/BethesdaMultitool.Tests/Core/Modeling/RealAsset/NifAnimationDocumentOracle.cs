using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A1-anim (plan section 3, slice 9): compares every typed track, clock and event of the reader's clips for one
///     file with what the independent probe's record gives through only the declared conversions
///     (<see cref="NifAnimationExpectations" />), bit for bit.
/// </summary>
/// <remarks>
///     <para>
///         Identity comes from the clip extras (<see cref="NifModelAnimationExtras" />, SA10: Shared cannot target one
///         track), and is itself checked against the probe: a sequence track's controlled-block ordinal must name the
///         interpolator the probe records for that ordinal, and a <c>(controllers)</c> track's controller must be the
///         probe's controller of that interpolator. The values then come from the probe alone.
///     </para>
///     <para>
///         Compared per track kind: transform tracks (state, interpolation, times, values, static, B-spline, TBC
///         parameters and endpoints, Squad policy, clock, priority), Euler tracks (order, the three axis curves, clock,
///         priority), per-target morph tracks and whole-vector morph tracks (weights merged in target order), and
///         property tracks (the target the extras name, the curve, clock, priority). Per clip: the name, the clip clock
///         (RE-22 rule 2 for a sequence, none for <c>(controllers)</c>), the Weight bits and the events (time bits and
///         exact text, in file order).
///     </para>
///     <para>
///         A track whose expectation the conversions refuse is a difference (the reader typed what the rules keep native).
///         A curve read straight from an NiUVData has no probe payload (no manifest file carries one) and is counted in
///         <see cref="NifAnimationDocumentOracleReport.NotCompared" />.
///     </para>
/// </remarks>
internal static class NifAnimationDocumentOracle
{
    /// <summary>The NotCompared reason of a property curve read straight from NiUVData.</summary>
    public const string UvDataReason = "NiUVData curve: the probe carries no NiUVData payload";

    private static readonly string[] ColorKinds =
    [
        nameof(ScenePropertyKind.MaterialBaseColor), nameof(ScenePropertyKind.MaterialSpecularColor),
        nameof(ScenePropertyKind.MaterialAmbientColor), nameof(ScenePropertyKind.MaterialEmissiveColor)
    ];

    /// <summary>Compares one read with its probe record.</summary>
    /// <param name="read">The read.</param>
    /// <param name="options">The control mutations (<see cref="NifAnimationDocumentOracleOptions.Faithful" /> for the hop).</param>
    /// <returns>The report.</returns>
    public static NifAnimationDocumentOracleReport Compare(Cut1bAnimationRead read,
        NifAnimationDocumentOracleOptions options)
    {
        ArgumentNullException.ThrowIfNull(read);
        var report = new NifAnimationDocumentOracleReport();
        var context = new NifAnimationOracleContext(read, NifAnimationExpectations.Facts(read.Expectation),
            NifAnimationExpectations.Payloads(read.Expectation), ExpectedSquadPolicy(read), options, report);
        for (var index = 0; index < read.Result.Clips.Count; index++)
        {
            CompareClip(context, index, read.Result.Clips[index]);
        }

        return report;
    }

    /// <summary>The Squad policy the read's platform should give: PC for a little-endian file, X360's estimate, none for PS3.</summary>
    /// <param name="read">The read.</param>
    /// <returns>The policy, or null for PS3.</returns>
    public static SceneGamebryoSquadPolicy? ExpectedSquadPolicy(Cut1bAnimationRead read)
    {
        if (!read.State.Info.IsBigEndian)
        {
            return SceneGamebryoSquadPolicy.PcFloat32;
        }

        return read.Platform.Platform == NifPackedPlatform.Ps3 ? null : SceneGamebryoSquadPolicy.Xbox360Estimate;
    }

    /// <summary>The clip extras object the reader wrote.</summary>
    /// <param name="clip">The clip.</param>
    /// <returns>The object under <see cref="NifModelAnimationExtras.Key" />.</returns>
    public static JsonObject Extras(SceneAnimation clip)
    {
        return JsonNode.Parse(clip.ExtrasJson!)![NifModelAnimationExtras.Key]!.AsObject();
    }

    private static void CompareClip(NifAnimationOracleContext context, int index, SceneAnimation clip)
    {
        var report = context.Report;
        report.Clips++;
        var clipLabel = $"clip {index.ToString(CultureInfo.InvariantCulture)} '{clip.Name}'";
        if (clip.ExtrasJson is null)
        {
            report.Diffs.Add($"{clipLabel}: no extras");
            return;
        }

        var extras = Extras(clip);
        var isSequence = extras["source"]?.GetValue<string>() == NifModelAnimationExtras.SequenceSource;
        JsonObject? sequence = null;
        if (isSequence)
        {
            var block = extras["block"]!.GetValue<int>();
            if (!context.Facts.TryGetValue(block, out sequence))
            {
                report.Diffs.Add($"{clipLabel}: the probe has no facts for sequence block {block}");
                return;
            }

            CompareSequenceClip(context, clipLabel, clip, sequence);
        }
        else
        {
            CompareClock(context, $"{clipLabel} clip clock", clip.Clock, null);
            if (clip.Events.Count != 0)
            {
                report.Diffs.Add($"{clipLabel}: the (controllers) clip carries {clip.Events.Count} events");
            }
        }

        CompareTransformTracks(context, clipLabel, clip, extras, sequence);
        CompareEulerTracks(context, clipLabel, clip, extras, sequence);
        CompareMorphTracks(context, clipLabel, clip, extras, sequence);
        ComparePropertyTracks(context, clipLabel, clip, extras, sequence);
    }

    private static void CompareSequenceClip(NifAnimationOracleContext context, string clipLabel, SceneAnimation clip,
        JsonObject sequence)
    {
        var report = context.Report;
        var name = sequence["name"]?.GetValue<string>() ?? string.Empty;
        if (!string.Equals(name, clip.Name, StringComparison.Ordinal))
        {
            report.Diffs.Add($"{clipLabel}: name expected '{name}'");
        }

        var cycle = sequence["cycleType"]?.GetValue<string>() == "LOOP"
            ? SceneAnimationCycle.Loop
            : SceneAnimationCycle.Clamp;
        var clock = (F(sequence["frequencyBits"]), 0f, F(sequence["startTimeBits"]), F(sequence["stopTimeBits"]),
            cycle);
        CompareClock(context, $"{clipLabel} clip clock", clip.Clock, clock);

        var weightBits = sequence["weightBits"]!.GetValue<uint>();
        if (clip.SourcePolicy?.Weight is not { } weight || Bits(weight) != weightBits)
        {
            report.Diffs.Add($"{clipLabel}: weight expected 0x{weightBits:X8}, actual " +
                             $"{(clip.SourcePolicy?.Weight is { } w ? $"0x{Bits(w):X8}" : "none")}");
        }

        CompareEvents(context, clipLabel, clip, sequence);
    }

    private static void CompareEvents(NifAnimationOracleContext context, string clipLabel, SceneAnimation clip,
        JsonObject sequence)
    {
        var report = context.Report;
        var textKeys = sequence["textKeys"]?.GetValue<int>() ?? -1;
        var typed = textKeys >= 0 &&
                    context.Read.Result.Decisions.Any(decision => decision.Block == textKeys && decision.IsTyped);
        if (!typed || !context.Facts.TryGetValue(textKeys, out var facts) ||
            facts["textKeys"] is not JsonArray keys)
        {
            if (clip.Events.Count != 0)
            {
                report.Diffs.Add($"{clipLabel}: {clip.Events.Count} events from text keys the reader did not type");
            }

            return;
        }

        if (keys.Count != clip.Events.Count)
        {
            report.Diffs.Add($"{clipLabel}: events expected {keys.Count}, actual {clip.Events.Count}");
            return;
        }

        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index]!.AsObject();
            var timeBits = key["timeBits"]!.GetValue<uint>();
            var text = key["value"]?.GetValue<string>() ?? string.Empty;
            if (context.Options.TrimEventCrlf)
            {
                text = text.TrimEnd('\r', '\n');
            }

            var actual = clip.Events[index];
            if (Bits(actual.TimeSeconds) != timeBits)
            {
                report.Diffs.Add($"{clipLabel} event {index}: time expected 0x{timeBits:X8}, actual " +
                                 $"0x{Bits(actual.TimeSeconds):X8}");
            }

            if (!string.Equals(actual.Text, text, StringComparison.Ordinal))
            {
                report.Diffs.Add($"{clipLabel} event {index}: text expected {Quote(text)}, actual {Quote(actual.Text)}");
            }

            report.Events++;
        }
    }

    private static void CompareTransformTracks(NifAnimationOracleContext context, string clipLabel,
        SceneAnimation clip, JsonObject extras, JsonObject? sequence)
    {
        var report = context.Report;
        var entries = extras["tracks"]?.AsArray() ?? new JsonArray();
        if (entries.Count != clip.TransformTracks.Count)
        {
            report.Diffs.Add($"{clipLabel}: {entries.Count} track entries for {clip.TransformTracks.Count} tracks");
            return;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index]!.AsObject();
            var track = clip.TransformTracks[index];
            var interpolator = entry["interpolator"]!.GetValue<int>();
            var property = ParseProperty(entry["property"]!.GetValue<string>());
            var label = $"{clipLabel} track {index} {entry["property"]}";
            if (track.Property != property || track.NodeIndex != entry["node"]!.GetValue<int>())
            {
                report.Diffs.Add($"{label}: extras name {property} on node {entry["node"]}, the track is " +
                                 $"{track.Property} on node {track.NodeIndex}");
            }

            if (!Binding(context, label, entry, interpolator, sequence, out var clock, out var priority))
            {
                continue;
            }

            var expected = NifAnimationExpectations.TransformChannel(interpolator, property, context.Facts,
                context.Payloads, context.SquadPolicy, context.Options);
            label = $"{label} ({expected.Origin})";
            if (expected.IsRefused)
            {
                report.Diffs.Add($"{label}: the declared conversions refuse it ({expected.Refusal}), the reader typed it");
                continue;
            }

            CompareCurveFields(context, label, track.State, track.Interpolation, track.Times, track.Values,
                track.StaticValue, track.Spline, track.TbcParameters, track.TbcEndpoints, expected);
            if (track.GamebryoSquadPolicy != expected.SquadPolicy)
            {
                report.Diffs.Add($"{label}: Squad policy expected {expected.SquadPolicy}, actual " +
                                 $"{track.GamebryoSquadPolicy}");
            }

            CompareClock(context, $"{label} clock", track.Clock, clock);
            ComparePriority(context, label, track.SourcePolicy?.Priority, priority);
            report.Count("transform", KindOf(expected));
        }
    }

    private static void CompareEulerTracks(NifAnimationOracleContext context, string clipLabel, SceneAnimation clip,
        JsonObject extras, JsonObject? sequence)
    {
        var report = context.Report;
        var entries = extras["eulerTracks"]?.AsArray() ?? new JsonArray();
        if (entries.Count != clip.EulerRotationTracks.Count)
        {
            report.Diffs.Add($"{clipLabel}: {entries.Count} Euler entries for {clip.EulerRotationTracks.Count} tracks");
            return;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index]!.AsObject();
            var track = clip.EulerRotationTracks[index];
            var interpolator = entry["interpolator"]!.GetValue<int>();
            var label = $"{clipLabel} Euler track {index} interpolator {interpolator}";
            if (track.NodeIndex != entry["node"]!.GetValue<int>() || track.Order != SceneEulerOrder.Xyz)
            {
                report.Diffs.Add($"{label}: node or order differs from the extras (XYZ, node {entry["node"]})");
            }

            if (!Binding(context, label, entry, interpolator, sequence, out var clock, out var priority))
            {
                continue;
            }

            var axes = NifAnimationExpectations.EulerAxes(interpolator, context.Facts, context.Payloads,
                context.Options);
            var actual = new[] { track.X, track.Y, track.Z };
            for (var axis = 0; axis < 3; axis++)
            {
                var axisLabel = $"{label} axis {"XYZ"[axis]} ({axes[axis].Origin})";
                if (axes[axis].IsRefused)
                {
                    report.Diffs.Add($"{axisLabel}: the declared conversions refuse it ({axes[axis].Refusal})");
                    continue;
                }

                CompareSceneCurve(context, axisLabel, actual[axis], axes[axis]);
            }

            CompareClock(context, $"{label} clock", track.Clock, clock);
            ComparePriority(context, label, track.SourcePolicy?.Priority, priority);
            report.Count("euler", string.Join(",", axes.Select(KindOf)));
        }
    }

    private static void CompareMorphTracks(NifAnimationOracleContext context, string clipLabel, SceneAnimation clip,
        JsonObject extras, JsonObject? sequence)
    {
        var report = context.Report;
        var entries = extras["morphTracks"]?.AsArray() ?? new JsonArray();
        var targetCursor = 0;
        var vectorCursor = 0;
        foreach (var node in entries)
        {
            var entry = node!.AsObject();
            var form = entry["form"]!.GetValue<string>();
            var label = $"{clipLabel} morph {form} {targetCursor + vectorCursor}";
            var controller = entry["controller"]?.GetValue<int>();
            if (form == NifModelMorphTrackSource.TargetForm)
            {
                if (targetCursor >= clip.MorphTargetTracks.Count)
                {
                    report.Diffs.Add($"{label}: more per-target entries than tracks");
                    return;
                }

                var track = clip.MorphTargetTracks[targetCursor++];
                var interpolator = entry["interpolator"]!.GetValue<int>();
                var morph = entry["morph"]!.GetValue<int>();
                if (track.NodeIndex != entry["node"]!.GetValue<int>() ||
                    track.TargetIndex != entry["target"]!.GetValue<int>() || track.TargetIndex != morph - 1)
                {
                    report.Diffs.Add($"{label}: node or target differs from the extras (morph {morph})");
                }

                if (!MorphBinding(context, label, entry, interpolator, morph, sequence, controller, out var stored,
                        out var clock, out var priority))
                {
                    continue;
                }

                var expected = NifAnimationExpectations.FloatCurve(interpolator, stored, context.Facts,
                    context.Payloads, context.Options);
                var curveLabel = $"{label} ({expected.Origin})";
                if (expected.IsRefused)
                {
                    report.Diffs.Add($"{curveLabel}: the declared conversions refuse it ({expected.Refusal})");
                    continue;
                }

                CompareSceneCurve(context, curveLabel, track.Weight, expected);
                CompareClock(context, $"{curveLabel} clock", track.Clock, clock);
                ComparePriority(context, curveLabel, track.SourcePolicy?.Priority, priority);
                report.Count("morphTarget", KindOf(expected));
            }
            else
            {
                if (vectorCursor >= clip.MorphTracks.Count)
                {
                    report.Diffs.Add($"{label}: more vector entries than tracks");
                    return;
                }

                CompareMorphVector(context, label, clip.MorphTracks[vectorCursor++], entry, sequence, controller);
            }
        }

        if (targetCursor != clip.MorphTargetTracks.Count || vectorCursor != clip.MorphTracks.Count)
        {
            report.Diffs.Add($"{clipLabel}: morph entries cover {targetCursor} of {clip.MorphTargetTracks.Count} " +
                             $"per-target and {vectorCursor} of {clip.MorphTracks.Count} vector tracks");
        }
    }

    private static void CompareMorphVector(NifAnimationOracleContext context, string label, SceneMorphTrack track,
        JsonObject entry, JsonObject? sequence, int? controller)
    {
        var report = context.Report;
        var slots = entry["slots"]!.AsArray();
        var expected = new List<NifAnimationExpectedCurve>();
        foreach (var slotNode in slots)
        {
            var slot = slotNode!.AsObject();
            var morph = slot["morph"]!.GetValue<int>();
            var interpolator = slot["interpolator"]!.GetValue<int>();
            uint? stored = controller is { } block ? StoredWeight(context, block, morph) : null;
            expected.Add(NifAnimationExpectations.FloatCurve(interpolator, stored, context.Facts, context.Payloads,
                context.Options));
        }

        if (track.NodeIndex != entry["node"]!.GetValue<int>() || track.TargetCount != slots.Count)
        {
            report.Diffs.Add($"{label}: node or target count differs from the extras ({slots.Count} slots)");
        }

        var first = expected.FirstOrDefault();
        if (first is null || expected.Any(static curve => curve.IsRefused || curve.Times.Length == 0) ||
            expected.Any(curve => curve.Interpolation != first.Interpolation ||
                                  !curve.Times.SequenceEqual(first.Times)))
        {
            report.Diffs.Add($"{label}: the slots' expectations do not merge into one vector track " +
                             string.Join("; ", expected.Select(static curve => curve.Refusal ?? curve.Origin)));
            return;
        }

        var parts = first.Interpolation is SceneInterpolation.Hermite or SceneInterpolation.CubicSpline ? 3 : 1;
        var targets = expected.Count;
        var weights = new uint[first.Times.Length * parts * targets];
        for (var key = 0; key < first.Times.Length; key++)
        {
            for (var part = 0; part < parts; part++)
            {
                for (var target = 0; target < targets; target++)
                {
                    weights[(key * parts + part) * targets + target] = expected[target].Values[key * parts + part];
                }
            }
        }

        var curveLabel = $"{label} ({first.Origin} and {targets - 1} more)";
        if (track.Interpolation != first.Interpolation)
        {
            report.Diffs.Add($"{curveLabel}: interpolation expected {first.Interpolation}, actual {track.Interpolation}");
        }

        CompareBits(context, $"{curveLabel} times", track.Times, first.Times);
        CompareBits(context, $"{curveLabel} weights", track.Weights, weights);
        CompareTbc(context, curveLabel, track.TbcParameters, first.TbcParameters);
        if (first.Interpolation == SceneInterpolation.Tbc && first.StartOutgoing is not null)
        {
            var start = expected.Select(static curve => curve.StartOutgoing![0]).ToArray();
            var end = expected.Select(static curve => curve.EndIncoming![0]).ToArray();
            CompareEndpoints(context, curveLabel, track.TbcEndpoints, start, end);
        }
        else if (track.TbcEndpoints is not null)
        {
            report.Diffs.Add($"{curveLabel}: endpoints present where none are expected");
        }

        var firstMorph = slots[0]!["morph"]!.GetValue<int>();
        if (!MorphBinding(context, curveLabel, entry, -1, firstMorph, sequence, controller, out _, out var clock,
                out var priority))
        {
            return;
        }

        CompareClock(context, $"{curveLabel} clock", track.Clock, clock);
        ComparePriority(context, curveLabel, track.SourcePolicy?.Priority, priority);
        report.Count("morphVector", first.Interpolation.ToString());
    }

    private static void ComparePropertyTracks(NifAnimationOracleContext context, string clipLabel,
        SceneAnimation clip, JsonObject extras, JsonObject? sequence)
    {
        var report = context.Report;
        var entries = extras["propertyTracks"]?.AsArray() ?? new JsonArray();
        if (entries.Count != clip.PropertyTracks.Count)
        {
            report.Diffs.Add($"{clipLabel}: {entries.Count} property entries for {clip.PropertyTracks.Count} tracks");
            return;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index]!.AsObject();
            var track = clip.PropertyTracks[index];
            var kind = entry["kind"]!.GetValue<string>();
            var interpolator = entry["interpolator"]!.GetValue<int>();
            var label = $"{clipLabel} property {index} {kind}";
            if (track.Target.Kind.ToString() != kind || track.Target.Index != entry["index"]!.GetValue<int>() ||
                track.Target.LayerIndex != entry["layerIndex"]!.GetValue<int>())
            {
                report.Diffs.Add($"{label}: the target differs from the extras");
            }

            if (interpolator < 0)
            {
                report.Skip(UvDataReason);
                continue;
            }

            var controllerBlock = entry["controllerBlock"]?.GetValue<int>();
            if (!PropertyBinding(context, label, entry, interpolator, sequence, controllerBlock, out var clock,
                    out var priority))
            {
                continue;
            }

            var expected = kind == nameof(ScenePropertyKind.NodeVisibility)
                ? NifAnimationExpectations.VisibilityCurve(interpolator, context.Facts, context.Payloads)
                : ColorKinds.Contains(kind, StringComparer.Ordinal)
                    ? NifAnimationExpectations.ColorCurve(interpolator, context.Facts, context.Payloads, context.Options)
                    : NifAnimationExpectations.FloatCurve(interpolator, null, context.Facts, context.Payloads,
                        context.Options);
            var curveLabel = $"{label} ({expected.Origin})";
            if (expected.IsRefused)
            {
                report.Diffs.Add($"{curveLabel}: the declared conversions refuse it ({expected.Refusal})");
                continue;
            }

            CompareSceneCurve(context, curveLabel, track.Curve, expected);
            CompareClock(context, $"{curveLabel} clock", track.Clock, clock);
            ComparePriority(context, curveLabel, track.SourcePolicy?.Priority, priority);
            report.Count("property", KindOf(expected));
        }
    }

    /// <summary>
    ///     The binding of a transform or Euler entry, checked against the probe, with the clock and priority it should
    ///     carry: a sequence track none (or its controller's clock under the control) and its controlled block's
    ///     Priority; a <c>(controllers)</c> track its controller's RE-22 clock and no priority.
    /// </summary>
    private static bool Binding(NifAnimationOracleContext context, string label, JsonObject entry, int interpolator,
        JsonObject? sequence, out (float, float, float, float, SceneAnimationCycle)? clock, out int? priority)
    {
        clock = null;
        priority = null;
        var report = context.Report;
        if (sequence is not null)
        {
            var ordinal = entry["controlledBlock"]!.GetValue<int>();
            if (sequence["controlledBlocks"] is not JsonArray blocks || ordinal < 0 || ordinal >= blocks.Count)
            {
                report.Diffs.Add($"{label}: controlled block {ordinal} is not in the probe's sequence");
                return false;
            }

            var block = blocks[ordinal]!.AsObject();
            if (block["interpolator"]?.GetValue<int>() != interpolator)
            {
                report.Diffs.Add($"{label}: the probe's controlled block {ordinal} names interpolator " +
                                 $"{block["interpolator"]}, the extras {interpolator}");
                return false;
            }

            priority = block["priority"]?.GetValue<int>();
            clock = SequenceTrackClock(context, block);
            return true;
        }

        var controller = entry["controller"]!.GetValue<int>();
        if (!context.Facts.TryGetValue(controller, out var facts) || facts["interpolator"]?.GetValue<int>() !=
            interpolator)
        {
            report.Diffs.Add($"{label}: the probe's controller {controller} does not name interpolator {interpolator}");
            return false;
        }

        return ControllerClock(context, label, facts, out clock);
    }

    /// <summary>The binding of a morph entry (see <see cref="Binding" />), and an embedded morpher's stored weight.</summary>
    private static bool MorphBinding(NifAnimationOracleContext context, string label, JsonObject entry,
        int interpolator, int morph, JsonObject? sequence, int? controller, out uint? stored,
        out (float, float, float, float, SceneAnimationCycle)? clock, out int? priority)
    {
        stored = null;
        clock = null;
        priority = null;
        var report = context.Report;
        if (sequence is not null)
        {
            var ordinal = entry["controlledBlock"]!.GetValue<int>();
            if (sequence["controlledBlocks"] is not JsonArray blocks || ordinal < 0 || ordinal >= blocks.Count)
            {
                report.Diffs.Add($"{label}: controlled block {ordinal} is not in the probe's sequence");
                return false;
            }

            var block = blocks[ordinal]!.AsObject();
            if (interpolator >= 0 && block["interpolator"]?.GetValue<int>() != interpolator)
            {
                report.Diffs.Add($"{label}: the probe's controlled block {ordinal} names interpolator " +
                                 $"{block["interpolator"]}, the extras {interpolator}");
                return false;
            }

            priority = block["priority"]?.GetValue<int>();
            clock = SequenceTrackClock(context, block);
            return true;
        }

        if (controller is not { } controllerBlock || !context.Facts.TryGetValue(controllerBlock, out var facts) ||
            facts["interpolators"] is not JsonArray items || morph < 0 || morph >= items.Count)
        {
            report.Diffs.Add($"{label}: the probe has no morpher item {morph} for controller {controller}");
            return false;
        }

        var item = items[morph]!.AsObject();
        if (interpolator >= 0 && item["interpolator"]?.GetValue<int>() != interpolator)
        {
            report.Diffs.Add($"{label}: the probe's morpher item {morph} names interpolator {item["interpolator"]}, " +
                             $"the extras {interpolator}");
            return false;
        }

        stored = item["weightBits"]?.GetValue<uint>();
        return ControllerClock(context, label, facts, out clock);
    }

    /// <summary>The binding of a property entry (see <see cref="Binding" />).</summary>
    private static bool PropertyBinding(NifAnimationOracleContext context, string label, JsonObject entry,
        int interpolator, JsonObject? sequence, int? controllerBlock,
        out (float, float, float, float, SceneAnimationCycle)? clock, out int? priority)
    {
        clock = null;
        priority = null;
        if (sequence is not null)
        {
            return Binding(context, label, entry, interpolator, sequence, out clock, out priority);
        }

        var controller = entry["controller"]?.GetValue<int>() ?? controllerBlock ?? -1;
        if (!context.Facts.TryGetValue(controller, out var facts) || facts["interpolator"]?.GetValue<int>() !=
            interpolator)
        {
            context.Report.Diffs.Add($"{label}: the probe's controller {controller} does not name interpolator " +
                                     $"{interpolator}");
            return false;
        }

        return ControllerClock(context, label, facts, out clock);
    }

    /// <summary>A sequence track's expected clock: none, or under the control its controlled block's controller clock.</summary>
    private static (float, float, float, float, SceneAnimationCycle)? SequenceTrackClock(
        NifAnimationOracleContext context, JsonObject controlledBlock)
    {
        if (!context.Options.ControllerClockInSequence)
        {
            return null;
        }

        var controller = controlledBlock["controller"]?.GetValue<int>() ?? -1;
        if (controller < 0 || !context.Facts.TryGetValue(controller, out var facts) ||
            !facts.ContainsKey("frequencyBits"))
        {
            return null;
        }

        return RuleClock(facts, out _);
    }

    /// <summary>A free-running controller's RE-22 clock; a track the rule gives no clock is a difference.</summary>
    private static bool ControllerClock(NifAnimationOracleContext context, string label, JsonObject facts,
        out (float, float, float, float, SceneAnimationCycle)? clock)
    {
        clock = RuleClock(facts, out var reason);
        if (clock is null)
        {
            context.Report.Diffs.Add($"{label}: RE-22 gives the controller no clock ({reason}), the reader typed a track");
            return false;
        }

        return true;
    }

    private static (float, float, float, float, SceneAnimationCycle)? RuleClock(JsonObject facts, out string? reason)
    {
        return NifAnimationEngineRules.ControllerClock(facts["frequencyBits"]!.GetValue<uint>(),
            facts["phaseBits"]!.GetValue<uint>(), facts["startTimeBits"]!.GetValue<uint>(),
            facts["stopTimeBits"]!.GetValue<uint>(), facts["cycleType"]?.GetValue<string>(),
            facts["active"]?.GetValue<bool>() == true, facts["playBackwards"]?.GetValue<bool>() == true, out reason);
    }

    private static uint? StoredWeight(NifAnimationOracleContext context, int controller, int morph)
    {
        return context.Facts.TryGetValue(controller, out var facts) && facts["interpolators"] is JsonArray items &&
               morph >= 0 && morph < items.Count
            ? items[morph]!["weightBits"]?.GetValue<uint>()
            : null;
    }

    private static void CompareSceneCurve(NifAnimationOracleContext context, string label, SceneCurve actual,
        NifAnimationExpectedCurve expected)
    {
        if (actual.ComponentCount != expected.ComponentCount)
        {
            context.Report.Diffs.Add($"{label}: width expected {expected.ComponentCount}, actual {actual.ComponentCount}");
        }

        CompareCurveFields(context, label, actual.State, actual.Interpolation, actual.Times, actual.Values,
            actual.StaticValue, actual.Spline, actual.TbcParameters, actual.TbcEndpoints, expected);
    }

    private static void CompareCurveFields(NifAnimationOracleContext context, string label,
        SceneAnimationChannelState state, SceneInterpolation interpolation, IReadOnlyList<float> times,
        IReadOnlyList<float> values, IReadOnlyList<float>? staticValue, SceneBSplineCurve? spline,
        IReadOnlyList<SceneTbcParameters>? tbc, SceneTbcEndpointTangents? endpoints,
        NifAnimationExpectedCurve expected)
    {
        var report = context.Report;
        if (state != expected.State)
        {
            report.Diffs.Add($"{label}: state expected {expected.State}, actual {state}");
        }

        if (interpolation != expected.Interpolation)
        {
            report.Diffs.Add($"{label}: interpolation expected {expected.Interpolation}, actual {interpolation}");
        }

        CompareBits(context, $"{label} times", times, expected.Times);
        CompareBits(context, $"{label} values", values, expected.Values);
        if (staticValue is null != expected.StaticValue is null)
        {
            report.Diffs.Add($"{label}: static value expected {(expected.StaticValue is null ? "none" : "present")}, " +
                             $"actual {(staticValue is null ? "none" : "present")}");
        }
        else if (staticValue is not null)
        {
            CompareBits(context, $"{label} static", staticValue, expected.StaticValue!);
        }

        CompareSpline(context, label, spline, expected.Spline);
        CompareTbc(context, label, tbc, expected.TbcParameters);
        if (expected.StartOutgoing is null)
        {
            if (endpoints is not null)
            {
                report.Diffs.Add($"{label}: endpoints present where none are expected");
            }
        }
        else
        {
            CompareEndpoints(context, label, endpoints, expected.StartOutgoing, expected.EndIncoming!);
        }
    }

    private static void CompareSpline(NifAnimationOracleContext context, string label, SceneBSplineCurve? actual,
        NifAnimationExpectedSpline? expected)
    {
        var report = context.Report;
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
            {
                report.Diffs.Add($"{label}: B-spline expected {(expected is null ? "none" : "present")}, actual " +
                                 $"{(actual is null ? "none" : "present")}");
            }

            return;
        }

        if (actual.Degree != expected.Degree || actual.ComponentCount != expected.ComponentCount ||
            actual.ControlPointCount != expected.ControlPointCount || actual.IsQuantized != expected.IsQuantized ||
            Bits(actual.StartSeconds) != expected.StartBits || Bits(actual.StopSeconds) != expected.StopBits)
        {
            report.Diffs.Add($"{label}: B-spline shape expected degree {expected.Degree} width " +
                             $"{expected.ComponentCount} n {expected.ControlPointCount} quantized {expected.IsQuantized} " +
                             $"[0x{expected.StartBits:X8}, 0x{expected.StopBits:X8}], actual degree {actual.Degree} " +
                             $"width {actual.ComponentCount} n {actual.ControlPointCount} quantized {actual.IsQuantized} " +
                             $"[0x{Bits(actual.StartSeconds):X8}, 0x{Bits(actual.StopSeconds):X8}]");
            return;
        }

        if (expected.IsQuantized)
        {
            if (actual.Bias is not { } bias || actual.Multiplier is not { } multiplier ||
                Bits(bias) != expected.BiasBits || Bits(multiplier) != expected.MultiplierBits)
            {
                report.Diffs.Add($"{label}: B-spline bias or multiplier differs");
            }

            var shorts = actual.QuantizedControlPoints;
            if (shorts.Count != expected.Shorts!.Length)
            {
                report.Diffs.Add($"{label}: {shorts.Count} compact controls, expected {expected.Shorts.Length}");
                return;
            }

            for (var index = 0; index < shorts.Count; index++)
            {
                if (shorts[index] != expected.Shorts[index])
                {
                    report.Diffs.Add($"{label}: compact control {index} expected {expected.Shorts[index]}, actual " +
                                     $"{shorts[index]}");
                    return;
                }
            }

            return;
        }

        CompareBits(context, $"{label} float controls", actual.ControlPoints, expected.FloatBits!);
    }

    private static void CompareTbc(NifAnimationOracleContext context, string label,
        IReadOnlyList<SceneTbcParameters>? actual, uint[]? expected)
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
            {
                context.Report.Diffs.Add($"{label}: TBC parameters expected {(expected is null ? "none" : "present")}, " +
                                         $"actual {(actual is null ? "none" : "present")}");
            }

            return;
        }

        var flat = actual.SelectMany(static p => new[] { p.Tension, p.Continuity, p.Bias }).ToArray();
        CompareBits(context, $"{label} TBC (tension, continuity, bias)", flat, expected);
    }

    private static void CompareEndpoints(NifAnimationOracleContext context, string label,
        SceneTbcEndpointTangents? actual, uint[] start, uint[] end)
    {
        if (actual is null)
        {
            context.Report.Diffs.Add($"{label}: RE-19 endpoints expected, none present");
            return;
        }

        CompareBits(context, $"{label} StartOutgoing", actual.StartOutgoing, start);
        CompareBits(context, $"{label} EndIncoming", actual.EndIncoming, end);
    }

    private static void CompareClock(NifAnimationOracleContext context, string label, SceneAnimationClock? actual,
        (float Frequency, float Phase, float Start, float Stop, SceneAnimationCycle Cycle)? expected)
    {
        context.Report.Clocks++;
        if (actual is null || expected is not { } clock)
        {
            if (actual is not null || expected is not null)
            {
                context.Report.Diffs.Add($"{label}: expected {Describe(expected)}, actual {Describe(actual)}");
            }

            return;
        }

        if (Bits(actual.Frequency) != Bits(clock.Frequency) || Bits(actual.PhaseSeconds) != Bits(clock.Phase) ||
            Bits(actual.StartSeconds) != Bits(clock.Start) || Bits(actual.StopSeconds) != Bits(clock.Stop) ||
            actual.Cycle != clock.Cycle)
        {
            context.Report.Diffs.Add($"{label}: expected {Describe(expected)}, actual {Describe(actual)}");
        }
    }

    private static void ComparePriority(NifAnimationOracleContext context, string label, int? actual, int? expected)
    {
        if (actual != expected)
        {
            var wanted = expected?.ToString(CultureInfo.InvariantCulture) ?? "none";
            var found = actual?.ToString(CultureInfo.InvariantCulture) ?? "none";
            context.Report.Diffs.Add($"{label}: priority expected {wanted}, actual {found}");
        }
    }

    private static void CompareBits(NifAnimationOracleContext context, string label, IReadOnlyList<float> actual,
        uint[] expected)
    {
        if (actual.Count != expected.Length)
        {
            context.Report.Diffs.Add($"{label}: {actual.Count} floats, expected {expected.Length}");
            return;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (Bits(actual[index]) != expected[index])
            {
                context.Report.Diffs.Add($"{label}[{index}]: expected 0x{expected[index]:X8}, actual " +
                                         $"0x{Bits(actual[index]):X8}");
                return;
            }
        }
    }

    private static string Describe((float Frequency, float Phase, float Start, float Stop, SceneAnimationCycle Cycle)?
        clock)
    {
        return clock is { } c
            ? $"({Bits(c.Frequency):X8}, {Bits(c.Phase):X8}, {Bits(c.Start):X8}, {Bits(c.Stop):X8}, {c.Cycle})"
            : "no clock";
    }

    private static string Describe(SceneAnimationClock? clock)
    {
        return clock is { } c
            ? $"({Bits(c.Frequency):X8}, {Bits(c.PhaseSeconds):X8}, {Bits(c.StartSeconds):X8}, " +
              $"{Bits(c.StopSeconds):X8}, {c.Cycle})"
            : "no clock";
    }

    private static string KindOf(NifAnimationExpectedCurve curve)
    {
        return curve.State == SceneAnimationChannelState.Keyed ? curve.Interpolation.ToString() : curve.State.ToString();
    }

    private static SceneTransformProperty ParseProperty(string name)
    {
        return name switch
        {
            "translation" => SceneTransformProperty.Translation,
            "rotation" => SceneTransformProperty.Rotation,
            "scale" => SceneTransformProperty.Scale,
            _ => throw new InvalidDataException($"Unknown track property '{name}' in the clip extras.")
        };
    }

    private static string Quote(string text)
    {
        return "\"" + text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) +
               "\"";
    }

    private static float F(JsonNode? node)
    {
        return BitConverter.UInt32BitsToSingle(node!.GetValue<uint>());
    }

    private static uint Bits(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }
}
