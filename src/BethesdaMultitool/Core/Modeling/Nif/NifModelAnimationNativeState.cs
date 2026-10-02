using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 8 (plan section 2.2): the <c>bmt.nif.animation.clip</c> native-state row of every clip
///     <see cref="NifModelAnimationReader" /> produced, built the way slice 3's
///     <see cref="NifModelSkeletonProvenance.ToNativeState" /> builds its row: one <see cref="SceneNativeState" /> per
///     clip, target Animation k, version <see cref="PayloadVersion" />, located at the NiControllerSequence block (or at
///     the <c>(controllers)</c> pseudo-element). Since slice 10 <see cref="NifModelReader" /> emits the rows, clip 0 at
///     animation index 0.
/// </summary>
/// <remarks>
///     <para>The payload (version 1):</para>
///     <list type="bullet">
///         <item><c>version</c>, <c>source</c> (<c>sequence</c> or <c>(controllers)</c>), <c>block</c> (the sequence; null otherwise).</item>
///         <item>
///             <c>clip</c>: the clip's D11 extras object, reused verbatim (<see cref="NifModelAnimationExtras" />): the
///             weight, cycle, frequency, start and stop bits, accumulation root, manager, text-key and anim-note refs,
///             every controlled block's fields, the transform and morph track maps; or the typed embedded controllers.
///         </item>
///         <item>
///             <c>name</c> and <c>accumRootName</c>: the stored bytes as <see cref="NifModelNativeValues.Text" /> (Latin-1
///             text plus raw hex when a byte is at or above 0x80); <c>controlledBlockStrings</c>: the five strings of every
///             controlled block in that raw form.
///         </item>
///         <item>
///             <c>tracks</c>: track i of the clip's transform tracks with its source (from the extras), <c>state</c>,
///             <c>interpolation</c>, <c>keyType</c> (the stored key type the interpolation came from), <c>keyCount</c> and
///             <c>conversion</c> (the rotation permutation, the scale replication, or none). <c>morphTracks</c>: each
///             emitted morph track likewise (<c>form</c>, source, <c>state</c>, <c>interpolation</c>, <c>keyCount</c>).
///         </item>
///         <item>
///             <c>eulerTracks</c>: track i of the clip's Euler rotation tracks (slice 13) with its source (from the
///             extras), the <c>order</c>, one entry per <c>axis</c> (<c>state</c>, <c>interpolation</c>, <c>keyType</c>,
///             <c>keyCount</c>) and the composition <c>conversion</c>. A Squad transform track (slice 16b) names its
///             stored key type and platform policy in <c>keyType</c> and the extras' <c>squadPolicy</c>.
///         </item>
///         <item>
///             <c>propertyTracks</c>: track i of the clip's property tracks (slice 14) with its source (from the extras:
///             kind, target index, layer index, node, property and controller blocks), <c>state</c>,
///             <c>interpolation</c>, <c>keyType</c>, <c>keyCount</c>, <c>width</c>, <c>hasClock</c> and <c>conversion</c>.
///         </item>
///         <item><c>nativeTracks</c>: every NativeOnly decision the reader made inside this clip's source, with its code and reason.</item>
///         <item><c>events</c>: the sequence's text keys as stored (time bits, label index, raw label bytes), or why they stay native.</item>
///     </list>
///     <para>
///         A payload that would exceed <see cref="SceneNativeState.MaximumPayloadCharacters" /> keeps its identity and
///         summarizes the clip object, the controlled-block strings and the events as counts (<see cref="SummarizedNote" />).
///     </para>
/// </remarks>
internal static class NifModelAnimationNativeState
{
    /// <summary>The row kind.</summary>
    public const string Kind = NifModelAnimationExtras.Key;

    /// <summary>The payload schema version.</summary>
    public const int PayloadVersion = 1;

    /// <summary>The note a summarized payload carries.</summary>
    public const string SummarizedNote = "clip payload exceeds the per-row text budget: clip, strings and events summarized";

    /// <summary>The conversion an Euler rotation track went through (RE-20 rules 8 and 9).</summary>
    public const string EulerConversion =
        "three scalar axis curves, angles radians as stored, composed as qZ * qY * qX (RE-20 rule 9); no quaternion track";

    /// <summary>The conversion a Squad rotation track went through (RE-17 steps 1 and 2, RE-24 step 2).</summary>
    public const string SquadConversion =
        "W, X, Y, Z permuted to X, Y, Z, W (no conjugation); RE-17 chain alignment and W clamp, no normalization; " +
        "RE-24 Float32 inner points as incoming, key, outgoing";

    /// <summary>The stored key type of a visibility track (slice 14: only CONST byte keys map).</summary>
    public const string VisibilityKeyType = "CONST (5) byte keys";

    /// <summary>The conversion a visibility track went through (slice 14).</summary>
    public const string VisibilityConversion = "byte keys 0 or 1 as a Step curve; any other value refused";

    private const int NoRef = -1;

    /// <summary>Builds one row per clip, in clip order.</summary>
    /// <param name="state">The read state the clips were read from.</param>
    /// <param name="result">The reader's result.</param>
    /// <param name="firstAnimationIndex">The document animation index of clip 0 (the clips sit consecutively after it).</param>
    /// <param name="cancellationToken">Observed per clip.</param>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<SceneNativeState> Build(NifModelReadState state, NifModelAnimationResult result,
        int firstAnimationIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentOutOfRangeException.ThrowIfNegative(firstAnimationIndex);
        var rows = new SceneNativeState[result.Clips.Count];
        var reference = state.Item.Reference;
        for (var clip = 0; clip < rows.Length; clip++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = Payload(state, result, clip);
            var text = payload.ToJsonString();
            if (text.Length > SceneNativeState.MaximumPayloadCharacters)
            {
                text = Summarize(payload).ToJsonString();
            }

            var block = payload["block"] is JsonValue value ? value.GetValue<int>() : NoRef;
            var location = block >= 0 && block < state.Blocks.Count
                ? new SceneSourceLocation(reference.SourceId, NifModelCoverage.Identity(block),
                    state.Blocks[block].Offset, state.Blocks[block].Size, reference)
                : new SceneSourceLocation(reference.SourceId, NifModelAnimationReader.ControllersClipName, null, null,
                    reference);
            rows[clip] = new SceneNativeState(new SceneElementRef(SceneElementKind.Animation, firstAnimationIndex + clip),
                Kind, PayloadVersion, text, location);
        }

        return rows;
    }

    /// <summary>The unbounded payload object of one clip (see the type remarks).</summary>
    /// <param name="state">The read state the clips were read from.</param>
    /// <param name="result">The reader's result.</param>
    /// <param name="clipIndex">The clip's index in <see cref="NifModelAnimationResult.Clips" />.</param>
    /// <returns>A new payload object.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The clip index is outside the result.</exception>
    /// <exception cref="InvalidDataException">The clip carries no extras object of the expected shape.</exception>
    public static JsonObject Payload(NifModelReadState state, NifModelAnimationResult result, int clipIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentOutOfRangeException.ThrowIfNegative(clipIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(clipIndex, result.Clips.Count);
        var clip = result.Clips[clipIndex];
        var extras = ReadExtras(clip);
        var source = new NifModelAnimationSource(state);
        var isSequence = string.Equals(extras["source"]?.GetValue<string>(), NifModelAnimationExtras.SequenceSource,
            StringComparison.Ordinal);
        var block = isSequence && extras["block"] is JsonValue stored ? stored.GetValue<int>() : NoRef;

        var payload = new JsonObject
        {
            ["version"] = PayloadVersion,
            ["source"] = extras["source"]?.DeepClone(),
            ["block"] = block == NoRef ? null : JsonValue.Create(block),
            ["clip"] = extras.DeepClone()
        };

        NifControllerSequenceView? view = null;
        if (block != NoRef && source.TryReadSequence(block, out view))
        {
            payload["name"] = RawText(source.Strings, view.NameIndex);
            payload["accumRootName"] = RawText(source.Strings, view.AccumRootNameIndex);
            payload["controlledBlockStrings"] = ControlledBlockStrings(source.Strings, view);
        }
        else
        {
            payload["name"] = new JsonObject { ["text"] = clip.Name };
            payload["accumRootName"] = null;
            payload["controlledBlockStrings"] = null;
        }

        payload["tracks"] = Tracks(clip, extras);
        payload["eulerTracks"] = EulerTracks(clip, extras);
        payload["morphTracks"] = MorphTracks(clip, extras);
        payload["propertyTracks"] = PropertyTracks(clip, extras);
        payload["nativeTracks"] = NativeTracks(source, result, block);
        payload["events"] = view is null ? null : Events(source, view);
        return payload;
    }

    /// <summary>The clip's extras object, which the reader always writes.</summary>
    private static JsonObject ReadExtras(SceneAnimation clip)
    {
        if (clip.ExtrasJson is null || JsonNode.Parse(clip.ExtrasJson) is not JsonObject root ||
            root[NifModelAnimationExtras.Key] is not JsonObject extras)
        {
            throw new InvalidDataException($"Clip '{clip.Name}' carries no {NifModelAnimationExtras.Key} extras object.");
        }

        return extras;
    }

    private static JsonNode? RawText(NifHeaderStringTable strings, int index)
    {
        return NifAnimationStrings.TryGetRaw(strings, index, out var bytes, out var isNone) && !isNone
            ? NifModelNativeValues.Text(bytes.Span)
            : null;
    }

    private static JsonArray ControlledBlockStrings(NifHeaderStringTable strings,
        NifControllerSequenceView view)
    {
        var array = new JsonArray();
        for (var ordinal = 0; ordinal < view.ControlledBlocks.Length; ordinal++)
        {
            var block = view.ControlledBlocks[ordinal];
            array.Add(new JsonObject
            {
                ["ordinal"] = ordinal,
                ["nodeName"] = RawText(strings, block.NodeNameIndex),
                ["propertyType"] = RawText(strings, block.PropertyTypeIndex),
                ["controllerType"] = RawText(strings, block.ControllerTypeIndex),
                ["controllerId"] = RawText(strings, block.ControllerIdIndex),
                ["interpolatorId"] = RawText(strings, block.InterpolatorIdIndex)
            });
        }

        return array;
    }

    private static JsonArray Tracks(SceneAnimation clip, JsonObject extras)
    {
        var sources = extras["tracks"] as JsonArray;
        var array = new JsonArray();
        for (var index = 0; index < clip.TransformTracks.Count; index++)
        {
            var track = clip.TransformTracks[index];
            var entry = sources is not null && index < sources.Count && sources[index] is JsonObject source
                ? (JsonObject)source.DeepClone()
                : new JsonObject();
            entry["index"] = index;
            entry["state"] = track.State.ToString();
            entry["interpolation"] = track.Interpolation.ToString();
            var squad = track.Interpolation == SceneInterpolation.GamebryoSquad;
            entry["keyType"] = squad ? SquadKeyType(entry) : KeyType(track.Interpolation, track.Spline is not null);
            entry["keyCount"] = track.Spline is { } spline ? spline.ControlPointCount : track.Times.Count;
            entry["conversion"] = squad ? SquadConversion : Conversion(track.Property);
            entry["hasClock"] = track.Clock is not null;
            array.Add(entry);
        }

        return array;
    }

    private static JsonArray EulerTracks(SceneAnimation clip, JsonObject extras)
    {
        var sources = extras["eulerTracks"] as JsonArray;
        var array = new JsonArray();
        for (var index = 0; index < clip.EulerRotationTracks.Count; index++)
        {
            var track = clip.EulerRotationTracks[index];
            var entry = sources is not null && index < sources.Count && sources[index] is JsonObject source
                ? (JsonObject)source.DeepClone()
                : new JsonObject();
            entry["index"] = index;
            entry["order"] = track.Order.ToString();
            entry["axes"] = new JsonArray(Axis("x", track.X), Axis("y", track.Y), Axis("z", track.Z));
            entry["conversion"] = EulerConversion;
            entry["hasClock"] = track.Clock is not null;
            array.Add(entry);
        }

        return array;
    }

    /// <summary>One Euler axis curve: its state, interpolation, the stored key type it came from and its key count.</summary>
    private static JsonObject Axis(string name, SceneCurve curve)
    {
        return new JsonObject
        {
            ["axis"] = name,
            ["state"] = curve.State.ToString(),
            ["interpolation"] = curve.Interpolation.ToString(),
            ["keyType"] = curve.State == SceneAnimationChannelState.Constant
                ? "none (empty axis, angle 0)"
                : KeyType(curve.Interpolation, curve.Spline is not null),
            ["keyCount"] = curve.Times.Count
        };
    }

    /// <summary>The stored key type of a Squad track, from the extras' <c>squadKeyType</c> (2 or 3) when present.</summary>
    private static string SquadKeyType(JsonObject entry)
    {
        return entry["squadKeyType"] is JsonValue stored && stored.TryGetValue<uint>(out var keyType)
            ? keyType switch
            {
                (uint)NifKeyInterpolation.Tbc => "TBC (3) quaternion, Squad (RE-24)",
                (uint)NifKeyInterpolation.Quadratic => "QUADRATIC (2) quaternion, Squad (RE-24)",
                _ => KeyType(SceneInterpolation.GamebryoSquad, false)
            }
            : KeyType(SceneInterpolation.GamebryoSquad, false);
    }

    private static JsonArray MorphTracks(SceneAnimation clip, JsonObject extras)
    {
        var sources = extras["morphTracks"] as JsonArray;
        var array = new JsonArray();
        var targetIndex = 0;
        var vectorIndex = 0;
        var count = sources?.Count ?? 0;
        for (var index = 0; index < count; index++)
        {
            if (sources![index] is not JsonObject source)
            {
                continue;
            }

            var entry = (JsonObject)source.DeepClone();
            entry["index"] = index;
            var form = source["form"]?.GetValue<string>();
            if (string.Equals(form, NifModelMorphTrackSource.VectorForm, StringComparison.Ordinal) &&
                vectorIndex < clip.MorphTracks.Count)
            {
                var track = clip.MorphTracks[vectorIndex++];
                entry["targetCount"] = track.TargetCount;
                entry["state"] = SceneAnimationChannelState.Keyed.ToString();
                entry["interpolation"] = track.Interpolation.ToString();
                entry["keyType"] = KeyType(track.Interpolation, false);
                entry["keyCount"] = track.Times.Count;
                entry["hasClock"] = track.Clock is not null;
            }
            else if (string.Equals(form, NifModelMorphTrackSource.TargetForm, StringComparison.Ordinal) &&
                     targetIndex < clip.MorphTargetTracks.Count)
            {
                var track = clip.MorphTargetTracks[targetIndex++];
                entry["state"] = track.Weight.State.ToString();
                entry["interpolation"] = track.Weight.Interpolation.ToString();
                entry["keyType"] = KeyType(track.Weight.Interpolation, track.Weight.Spline is not null);
                entry["keyCount"] = track.Weight.Spline is { } spline
                    ? spline.ControlPointCount
                    : track.Weight.Times.Count;
                entry["hasClock"] = track.Clock is not null;
            }

            array.Add(entry);
        }

        return array;
    }

    /// <summary>
    ///     Track i of the clip's property tracks (slice 14) with its source (from the extras), <c>state</c>,
    ///     <c>interpolation</c>, <c>keyType</c>, <c>keyCount</c>, <c>width</c>, <c>hasClock</c> and the conversion it went
    ///     through: none for a material or layer member, the byte-key rule for visibility.
    /// </summary>
    private static JsonArray PropertyTracks(SceneAnimation clip, JsonObject extras)
    {
        var sources = extras["propertyTracks"] as JsonArray;
        var array = new JsonArray();
        for (var index = 0; index < clip.PropertyTracks.Count; index++)
        {
            var track = clip.PropertyTracks[index];
            var entry = sources is not null && index < sources.Count && sources[index] is JsonObject source
                ? (JsonObject)source.DeepClone()
                : new JsonObject();
            var curve = track.Curve;
            entry["index"] = index;
            entry["kind"] = NifModelAnimationExtras.KindName(track.Target.Kind);
            entry["state"] = curve.State.ToString();
            entry["interpolation"] = curve.Interpolation.ToString();
            entry["keyType"] = curve.State == SceneAnimationChannelState.Constant
                ? "none (pose value)"
                : track.Target.Kind == ScenePropertyKind.NodeVisibility
                    ? VisibilityKeyType
                    : KeyType(curve.Interpolation, curve.Spline is not null);
            entry["keyCount"] = curve.Spline is { } spline ? spline.ControlPointCount : curve.Times.Count;
            entry["width"] = curve.ComponentCount;
            entry["conversion"] = track.Target.Kind == ScenePropertyKind.NodeVisibility ? VisibilityConversion : "none";
            entry["hasClock"] = track.Clock is not null;
            array.Add(entry);
        }

        return array;
    }

    private static JsonArray NativeTracks(NifModelAnimationSource source, NifModelAnimationResult result, int block)
    {
        var array = new JsonArray();
        foreach (var decision in result.Decisions)
        {
            if (decision.IsTyped || decision.SourceBlock < 0)
            {
                continue;
            }

            var inClip = block == NoRef
                ? !source.Is(decision.SourceBlock, "NiControllerSequence")
                : decision.SourceBlock == block;
            if (!inClip)
            {
                continue;
            }

            array.Add(new JsonObject
            {
                ["block"] = decision.Block,
                ["source"] = decision.SourceBlock,
                ["controlledBlock"] = decision.ControlledBlock < 0 ? null : JsonValue.Create(decision.ControlledBlock),
                ["property"] = decision.Property is { } property
                    ? NifModelAnimationExtras.PropertyName(property)
                    : null,
                ["code"] = decision.Code,
                ["reason"] = decision.Reason
            });
        }

        return array;
    }

    private static JsonNode? Events(NifModelAnimationSource source, NifControllerSequenceView view)
    {
        if (view.TextKeysRef == NoRef)
        {
            return new JsonArray();
        }

        if (!source.Is(view.TextKeysRef, "NiTextKeyExtraData") || !source.TryReadTextKeys(view.TextKeysRef, out var keys))
        {
            return new JsonObject
            {
                ["block"] = view.TextKeysRef,
                ["blocked"] = NifModelAnimationReasons.TextKeysUnreadable
            };
        }

        var mapped = NifModelTextKeyEvents.Map(keys, source.Strings);
        if (mapped.IsBlocked)
        {
            return new JsonObject
            {
                ["block"] = view.TextKeysRef,
                ["blocked"] = NifModelAnimationReasons.Reason(mapped.Block),
                ["code"] = NifModelAnimationReasons.Code(mapped.Block),
                ["keyIndex"] = mapped.BlockedKeyIndex is { } index ? JsonValue.Create(index) : null
            };
        }

        var array = new JsonArray();
        foreach (var key in mapped.Keys)
        {
            array.Add(new JsonObject
            {
                ["timeBits"] = key.TimeBits,
                ["labelIndex"] = key.LabelIndex,
                ["isNull"] = key.IsNullLabel,
                ["label"] = NifModelNativeValues.Text(key.RawLabel.Span)
            });
        }

        return array;
    }

    /// <summary>The stored key type an interpolation came from (the slice-2 mapping is one to one).</summary>
    private static string KeyType(SceneInterpolation interpolation, bool spline)
    {
        if (spline)
        {
            return "B-spline (NiBSplineData controls)";
        }

        return interpolation switch
        {
            SceneInterpolation.Linear => "LINEAR (1)",
            SceneInterpolation.GamebryoCounterWarpedNlerp => "LINEAR (1)",
            SceneInterpolation.Step => "CONST (5)",
            SceneInterpolation.Hermite => "QUADRATIC (2)",
            SceneInterpolation.Tbc => "TBC (3)",
            SceneInterpolation.GamebryoSquad => "TBC (3) or QUADRATIC (2) quaternion",
            _ => "not a NIF key type"
        };
    }

    /// <summary>The conversion a transform channel went through (plan section 1.2; nothing else is applied).</summary>
    private static string Conversion(SceneTransformProperty property)
    {
        return property switch
        {
            SceneTransformProperty.Rotation => "W, X, Y, Z permuted to X, Y, Z, W (no conjugation)",
            SceneTransformProperty.Scale => "s replicated to (s, s, s)",
            _ => "none"
        };
    }

    /// <summary>The bounded form: identity kept, the large members summarized as counts.</summary>
    private static JsonObject Summarize(JsonObject payload)
    {
        var summary = new JsonObject
        {
            ["version"] = PayloadVersion,
            ["source"] = payload["source"]?.DeepClone(),
            ["block"] = payload["block"]?.DeepClone(),
            ["name"] = payload["name"]?.DeepClone(),
            ["summarized"] = SummarizedNote,
            ["clip"] = new JsonObject
            {
                ["controlledBlocks"] = Count(payload["clip"]?["controlledBlocks"]),
                ["tracks"] = Count(payload["clip"]?["tracks"]),
                ["morphTracks"] = Count(payload["clip"]?["morphTracks"]),
                ["propertyTracks"] = Count(payload["clip"]?["propertyTracks"])
            },
            ["controlledBlockStrings"] = Count(payload["controlledBlockStrings"]),
            ["tracks"] = Count(payload["tracks"]),
            ["eulerTracks"] = Count(payload["eulerTracks"]),
            ["morphTracks"] = Count(payload["morphTracks"]),
            ["propertyTracks"] = Count(payload["propertyTracks"]),
            ["nativeTracks"] = Count(payload["nativeTracks"]),
            ["events"] = Count(payload["events"])
        };
        return summary;
    }

    private static JsonNode? Count(JsonNode? node)
    {
        return node is JsonArray array
            ? JsonValue.Create(string.Create(CultureInfo.InvariantCulture, $"{array.Count} entries summarized"))
            : null;
    }
}
