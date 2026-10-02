using System.Globalization;
using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Reports the NiBillboardNode occurrences whose world scale is not positive, at rest or at a scale value one of the
///     document's clips states, because the contract cannot carry that sign (review F1 of the 2026-09-28 billboard reader;
///     DESIGN-rev4 6.7). The encoding is not changed.
/// </summary>
/// <remarks>
///     <para>
///         The engine replaces only the world rotation and keeps the uniform world scale, sign included, as a separate
///         scalar (RE-25 item 1), so under a negative scale it draws s F. <see cref="SceneBillboard.SourceScale" />
///         retains the rest sign, but only as metadata no writer lowers, and has no member for a scale value a clip
///         states; the pre-facing matrix cannot carry either: an improper stored rotation, which the engine drops, reads
///         to the same matrix (<see cref="NifModelTransform" /> writes it as a negated TRS scale). A writer therefore
///         presents |s| with the encoding's own handedness. For effective modes 0 and 2 to 5 that misses the engine's card
///         by up to a point reflection (VERIFY-rev4-math W7; element error over |s| from 1.36 to 2.0 on the 18 RE-25
///         vectors). RE-25's mode-1 formula divides the camera offset by s, which cancels a negative sign, and is
///         undefined at 0, as mode 5's is. RE-25 did not exercise a nonpositive scale.
///     </para>
///     <para>
///         Rule, per occurrence: the rest world scale is the signed product of the stored uniform Scale fields along
///         the chain, read from the occurrence's <see cref="SceneBillboard.SourceScale" /> so the report and the
///         declaration always carry the same value (<see cref="NifModelBillboards.SourceScale" />: root first, in
///         double); an occurrence whose product is left unstated
///         (<see cref="NifModelLayerReader.SourceScaleDiagnostic" />) is checked on its stated values only. A stated
///         scale value is a key value, a B-spline control (a clamped B-spline stays inside the hull of its controls) or
///         the constant of a Scale track of any clip in this document on the node or an ancestor. The occurrence is
///         reported when either is negative (every facing mode), or zero for effective modes 1 and 5 (a zero scale gives
///         modes 0, 2, 3 and 4 a zero-extent card in the engine and in a writer alike). Not seen: a value an
///         interpolated segment reaches between positive keys, and clips outside this file (<c>.kf</c> streams). Only the
///         reader knows the stored Scale fields, so it reports the gap: one diagnostic per block
///         (<see cref="Diagnostic" />) and a <see cref="PayloadKey" /> entry in the block's native <c>billboard</c> payload.
///     </para>
///     <para>
///         The rest half is kept beside the <see cref="SceneBillboard.SourceScale" /> declaration, whose writer rows
///         (<c>billboard/source-scale</c>, Degraded for a negative or zero scalar) state the same gap, because the
///         foundation's adapter note for Shared 853b6f1 says to keep the existing reader diagnostics until engine handling
///         is implemented and checked, and the RE-25 runtime evidence excludes nonpositive scales. It may be retired once
///         Shared implements and checks engine facing for nonpositive scales; the stated-value half stays until the
///         contract can carry a sampled scale.
///     </para>
///     <para>
///         The check runs after the animation stage because the Scale keys are decoded there, by the reader's one
///         animation decode path; it adds its entry to the payload <see cref="NifModelLayerReader" /> built, before the
///         native state is assembled.
///     </para>
/// </remarks>
internal static class NifModelBillboardScaleSign
{
    /// <summary>Diagnostic code for a billboard block with an occurrence whose world scale is not positive.</summary>
    public const string Diagnostic = "bmt.nif.billboard-scale-sign";

    /// <summary>The key of the entry this check adds to a reported block's native <c>billboard</c> payload.</summary>
    public const string PayloadKey = "scaleSign";

    /// <summary>The recorded rule of the check.</summary>
    public const string Rule =
        "reported: an occurrence whose rest world scale (the signed product of the stored Scale fields along its " +
        "chain, the same value its SceneBillboard.SourceScale carries) or a stated Scale value on its chain (a key, " +
        "B-spline control or constant of a Scale track in this document's clips) is negative, or is zero for " +
        "effective modes 1 and 5; not seen: values an interpolated segment reaches between positive keys, and clips " +
        "outside this file";

    /// <summary>The contract gap every report states.</summary>
    public const string ContractGap =
        "The engine replaces only the world rotation and keeps the uniform scale's sign (RE-25 item 1), but " +
        "SceneBillboard.SourceScale retains only the rest sign, as metadata no writer lowers, and has no member for a " +
        "stated clip value, and the pre-facing matrix cannot carry either (an improper stored rotation, which the " +
        "engine drops, reads to the same matrix), so a writer presents |s| with the encoding's " +
        "handedness: for effective modes 0 and 2 to 5 the card misses the engine's by up to a point reflection " +
        "(VERIFY-rev4-math W7); RE-25's mode-1 formula divides by s, which cancels a negative sign, and modes 1 and 5 " +
        "are undefined at a zero scale (RE-25 did not exercise a nonpositive scale). The encoding is kept.";

    /// <summary>
    ///     Checks every billboard occurrence and adds the <see cref="PayloadKey" /> entry to each reported block's
    ///     native payload.
    /// </summary>
    /// <param name="graph">The placed node graph (its occurrences, children and per-block facts).</param>
    /// <param name="layers">The layer reader's result: the billboards and the per-block native payloads.</param>
    /// <param name="clips">The document's clips in document order.</param>
    /// <param name="cancellationToken">Observed per track and per occurrence.</param>
    /// <returns>One diagnostic per reported block, bounded per code.</returns>
    public static IReadOnlyList<SceneDiagnostic> Report(NifModelNodeGraph graph, NifModelLayerResult layers,
        IReadOnlyList<SceneAnimation> clips, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(clips);
        var sink = new NifModelDiagnosticSink();
        if (layers.BillboardByNode.Count == 0)
        {
            return sink.ToList();
        }

        var parentByNode = new int[graph.Nodes.Count];
        Array.Fill(parentByNode, -1);
        for (var node = 0; node < graph.Nodes.Count; node++)
        {
            foreach (var child in graph.Nodes[node].Children)
            {
                parentByNode[child] = node;
            }
        }

        var blockByNode = new int[graph.Nodes.Count];
        Array.Fill(blockByNode, -1);
        for (var block = 0; block < graph.OccurrencesByBlock.Count; block++)
        {
            foreach (var node in graph.OccurrencesByBlock[block])
            {
                blockByNode[node] = block;
            }
        }

        var stated = SmallestStatedScales(clips, cancellationToken);
        foreach (var group in layers.BillboardByNode.GroupBy(pair => blockByNode[pair.Key]).OrderBy(g => g.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reported = new List<Occurrence>();
            foreach (var (node, billboard) in group.OrderBy(pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Check(parentByNode, stated, node, billboard) is { } occurrence)
                {
                    reported.Add(occurrence);
                }
            }

            if (reported.Count == 0 || !layers.NodeFactsByBlock.TryGetValue(group.Key, out var facts) ||
                facts["billboard"] is not JsonObject payload)
            {
                continue;
            }

            var occurrences = group.Count();
            payload[PayloadKey] = Payload(reported, occurrences);
            sink.Add(Diagnostic, Message(graph.FactsByBlock[group.Key], reported, occurrences));
        }

        return sink.ToList();
    }

    /// <summary>
    ///     The occurrence's findings, or null when its world scale is positive at rest and at every stated one. The rest
    ///     world scale is the occurrence's declared <see cref="SceneBillboard.SourceScale" />, never recomputed here.
    /// </summary>
    private static Occurrence? Check(int[] parentByNode, Dictionary<int, StatedScale> stated, int node,
        SceneBillboard billboard)
    {
        var effective = billboard.RawMode is { } raw ? NifModelBillboards.EffectiveMode(raw) : -1;
        var zeroMatters = effective is 1 or 5;
        var rest = billboard.SourceScale?.RestWorldScale;
        var keys = new List<StatedScale>();
        for (var current = node; current >= 0; current = parentByNode[current])
        {
            if (stated.TryGetValue(current, out var value) && (value.Value < 0 || (value.Value == 0 && zeroMatters)))
            {
                keys.Add(value);
            }
        }

        var restReported = rest is { } restScale && (restScale < 0 || (restScale == 0 && zeroMatters));
        return restReported || keys.Count > 0 ? new Occurrence(node, rest, restReported, keys) : null;
    }

    /// <summary>
    ///     Per node, the smallest nonpositive scale value any clip's Scale track states for it (a key's value, a B-spline
    ///     control, or a Constant track's value), with the first clip that states it.
    /// </summary>
    private static Dictionary<int, StatedScale> SmallestStatedScales(IReadOnlyList<SceneAnimation> clips,
        CancellationToken cancellationToken)
    {
        var smallest = new Dictionary<int, StatedScale>();
        for (var clip = 0; clip < clips.Count; clip++)
        {
            foreach (var track in clips[clip].TransformTracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (track.Property != SceneTransformProperty.Scale || SmallestStated(track) is not { } value ||
                    value > 0 || (smallest.TryGetValue(track.NodeIndex, out var kept) && kept.Value <= value))
                {
                    continue;
                }

                smallest[track.NodeIndex] = new StatedScale(track.NodeIndex, clip, clips[clip].Name, value);
            }
        }

        return smallest;
    }

    /// <summary>
    ///     The smallest scale value a track states: over a keyed curve's key values (the middle of each incoming, value,
    ///     outgoing triple for cubic and Hermite keys), a keyed B-spline's decoded controls, or a Constant track's value;
    ///     null when it states none (NotDriven, or no keys).
    /// </summary>
    private static double? SmallestStated(SceneTransformTrack track)
    {
        double? smallest = null;
        switch (track.State)
        {
            case SceneAnimationChannelState.Constant when track.StaticValue is { } constant:
                foreach (var value in constant)
                {
                    smallest = Math.Min(smallest ?? double.PositiveInfinity, value);
                }

                break;
            case SceneAnimationChannelState.Keyed when track.Spline is { } spline:
                if (spline.IsQuantized)
                {
                    var bias = spline.Bias ?? 0f;
                    var multiplier = spline.Multiplier ?? 0f;
                    foreach (var control in spline.QuantizedControlPoints)
                    {
                        // SceneBSplineCurve's documented decode, in Float32 and in that order.
                        var decoded = bias + control / 32767f * multiplier;
                        smallest = Math.Min(smallest ?? double.PositiveInfinity, decoded);
                    }
                }
                else
                {
                    foreach (var control in spline.ControlPoints)
                    {
                        smallest = Math.Min(smallest ?? double.PositiveInfinity, control);
                    }
                }

                break;
            case SceneAnimationChannelState.Keyed when track.Times.Count > 0:
                var triples = track.Interpolation is SceneInterpolation.CubicSpline or SceneInterpolation.Hermite;
                var stride = triples ? 3 : 1;
                var components = track.Values.Count / (track.Times.Count * stride);
                for (var key = 0; key < track.Times.Count; key++)
                {
                    var start = key * stride * components + (triples ? components : 0);
                    for (var component = 0; component < components; component++)
                    {
                        smallest = Math.Min(smallest ?? double.PositiveInfinity, track.Values[start + component]);
                    }
                }

                break;
        }

        return smallest;
    }

    /// <summary>The native entry: the rule, the contract gap, the counts and the reported occurrences (bounded).</summary>
    private static JsonObject Payload(List<Occurrence> reported, int occurrences)
    {
        var listed = new JsonArray();
        foreach (var occurrence in reported.Take(NifModelNativeValues.MaximumInlineElements))
        {
            var keys = new JsonArray();
            foreach (var key in occurrence.Keys)
            {
                keys.Add(new JsonObject
                {
                    ["node"] = key.Node,
                    ["clip"] = key.Clip,
                    ["clipName"] = key.ClipName,
                    ["smallest"] = NifModelNativeValues.Double(key.Value)
                });
            }

            listed.Add(new JsonObject
            {
                ["node"] = occurrence.Node,
                ["restWorldScale"] = occurrence.RestWorldScale is { } rest ? NifModelNativeValues.Double(rest) : null,
                ["restReported"] = occurrence.RestReported,
                ["statedScales"] = keys
            });
        }

        return new JsonObject
        {
            ["rule"] = Rule,
            ["contractGap"] = ContractGap,
            ["occurrences"] = occurrences,
            ["reportedOccurrences"] = reported.Count,
            ["reported"] = listed
        };
    }

    /// <summary>The block's diagnostic: its first reported occurrence in words, the counts and the contract gap.</summary>
    private static string Message(NifModelNodeFacts facts, List<Occurrence> reported, int occurrences)
    {
        var first = reported[0];
        var where = first.RestReported
            ? string.Create(CultureInfo.InvariantCulture,
                $"node {first.Node} at rest (the product of the stored Scale fields is " +
                $"{first.RestWorldScale.GetValueOrDefault():R})")
            : string.Create(CultureInfo.InvariantCulture,
                $"node {first.Node} in clip {first.Keys[0].Clip} '{first.Keys[0].ClipName}' (a Scale value of node " +
                $"{first.Keys[0].Node} is {first.Keys[0].Value:R})");
        return string.Create(CultureInfo.InvariantCulture,
            $"Block {facts.BlockIndex} ({facts.TypeName}): {reported.Count} of {occurrences} occurrence(s) have a world " +
            $"scale that is not positive, first {where}; see the native '{PayloadKey}' entry. {ContractGap}");
    }

    /// <summary>A nonpositive scale value a clip states for one node.</summary>
    /// <param name="Node">The node the track drives.</param>
    /// <param name="Clip">The clip's index in the document.</param>
    /// <param name="ClipName">The clip's name.</param>
    /// <param name="Value">The smallest value the track states.</param>
    private sealed record StatedScale(int Node, int Clip, string ClipName, double Value);

    /// <summary>One reported billboard occurrence.</summary>
    /// <param name="Node">The occurrence's node index.</param>
    /// <param name="RestWorldScale">
    ///     The occurrence's declared <see cref="SceneBillboard.SourceScale" /> value, or null when it is unstated.
    /// </param>
    /// <param name="RestReported">Whether the rest world scale alone reports the occurrence.</param>
    /// <param name="Keys">The reported stated values on the chain, nearest node first.</param>
    private sealed record Occurrence(int Node, double? RestWorldScale, bool RestReported, List<StatedScale> Keys);
}
