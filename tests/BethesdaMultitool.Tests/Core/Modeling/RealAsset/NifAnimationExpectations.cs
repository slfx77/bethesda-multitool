using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Derives, from the independent probe's record alone (its <c>animation</c> facts and <c>payloads</c>), the curve each
///     typed channel should carry, through only the conversions the plan declares (hop A1-anim): the rotation
///     permutation (W, X, Y, Z to X, Y, Z, W), scale replication, the Hermite triple [Forward, Value, Backward] as
///     [incoming, value, outgoing] (RE-18), the TBC naming (tension, continuity, bias in file order) with RE-19's
///     endpoints, the state rule (sentinel static to NotDriven, a static without keys to Constant), RE-17 for LINEAR and
///     CONST quaternions, RE-24 for Squad triples, the Euler axis curves (RE-20) and the B-spline slicing. The engine
///     formulas come from <see cref="NifAnimationEngineRules" />; nothing here calls the reader's mapping code.
/// </summary>
internal static class NifAnimationExpectations
{
    /// <summary>The handle of an absent B-spline channel.</summary>
    public const uint AbsentHandle = 0xFFFF;

    private const uint Linear = 1;
    private const uint Quadratic = 2;
    private const uint Tbc = 3;
    private const uint Euler = 4;
    private const uint Constant = 5;

    /// <summary>The probe's animation facts by block index.</summary>
    /// <param name="record">The probe record.</param>
    /// <returns>The facts.</returns>
    public static Dictionary<int, JsonObject> Facts(JsonObject record)
    {
        var facts = new Dictionary<int, JsonObject>();
        if (record["animation"] is JsonObject animation)
        {
            foreach (var (key, node) in animation)
            {
                facts[int.Parse(key, CultureInfo.InvariantCulture)] = node!.AsObject();
            }
        }

        return facts;
    }

    /// <summary>The probe's payloads by block index.</summary>
    /// <param name="record">The probe record.</param>
    /// <returns>The payload objects.</returns>
    public static Dictionary<int, JsonObject> Payloads(JsonObject record)
    {
        var payloads = new Dictionary<int, JsonObject>();
        if (record["payloads"] is JsonArray array)
        {
            foreach (var node in array)
            {
                var entry = node!.AsObject();
                payloads[entry["i"]!.GetValue<int>()] = entry["payload"]!.AsObject();
            }
        }

        return payloads;
    }

    /// <summary>
    ///     The expected transform channel of an NiTransformInterpolator or a transform B-spline interpolator: the state
    ///     rule over the static value, then the key curve or the B-spline.
    /// </summary>
    /// <param name="interpolator">The interpolator block.</param>
    /// <param name="property">The channel.</param>
    /// <param name="facts">The probe facts.</param>
    /// <param name="payloads">The probe payloads.</param>
    /// <param name="squadPolicy">The file's Squad policy; null when the platform has none (PS3).</param>
    /// <param name="options">The control mutations.</param>
    /// <returns>The expectation, or a refusal.</returns>
    public static NifAnimationExpectedCurve TransformChannel(int interpolator, SceneTransformProperty property,
        IReadOnlyDictionary<int, JsonObject> facts, IReadOnlyDictionary<int, JsonObject> payloads,
        SceneGamebryoSquadPolicy? squadPolicy, NifAnimationDocumentOracleOptions options)
    {
        if (!facts.TryGetValue(interpolator, out var fact))
        {
            return NifAnimationExpectedCurve.Refused($"interpolator {interpolator}", "no probe facts for the block");
        }

        var type = fact["type"]!.GetValue<string>();
        return type switch
        {
            "NiTransformInterpolator" => KeyframeChannel(interpolator, fact, property, payloads, squadPolicy, options),
            "NiBSplineTransformInterpolator" or "NiBSplineCompTransformInterpolator" =>
                BsplineTransformChannel(interpolator, fact, property, facts, payloads, options),
            _ => NifAnimationExpectedCurve.Refused($"interpolator {interpolator} {type}",
                "not a transform interpolator the declared conversions type")
        };
    }

    /// <summary>
    ///     The expected Euler axes (RE-20) of an NiTransformInterpolator whose data stores an XYZ_ROTATION: record 0's
    ///     three axis groups, an empty axis Constant 0, a keyed one through the component rules at width 1.
    /// </summary>
    /// <param name="interpolator">The interpolator block.</param>
    /// <param name="facts">The probe facts.</param>
    /// <param name="payloads">The probe payloads.</param>
    /// <param name="options">The control mutations.</param>
    /// <returns>The X, Y and Z axis expectations (each may be a refusal).</returns>
    public static NifAnimationExpectedCurve[] EulerAxes(int interpolator, IReadOnlyDictionary<int, JsonObject> facts,
        IReadOnlyDictionary<int, JsonObject> payloads, NifAnimationDocumentOracleOptions options)
    {
        var origin = $"interpolator {interpolator}";
        if (!facts.TryGetValue(interpolator, out var fact) || fact["data"]?.GetValue<int>() is not { } data ||
            !payloads.TryGetValue(data, out var payload))
        {
            var refused = NifAnimationExpectedCurve.Refused(origin, "no probe payload for the Euler data");
            return [refused, refused, refused];
        }

        origin = $"interpolator {interpolator} data {data}";
        var rotation = payload["rotation"]!.AsObject();
        if (rotation["keyType"]?.GetValue<uint>() != Euler || payload["xyzRotations"] is not JsonArray axes ||
            axes.Count != 3)
        {
            var refused = NifAnimationExpectedCurve.Refused(origin, "the data stores no XYZ_ROTATION record");
            return [refused, refused, refused];
        }

        if (rotation["storedNumRotationKeys"]?.GetValue<uint>() != 1)
        {
            var refused = NifAnimationExpectedCurve.Refused(origin, "eulerRecordCount");
            return [refused, refused, refused];
        }

        var result = new NifAnimationExpectedCurve[3];
        for (var axis = 0; axis < 3; axis++)
        {
            var group = axes[axis]!.AsObject();
            var axisOrigin = $"{origin} axis {"XYZ"[axis]}";
            var keys = group["keys"] as JsonArray;
            if (keys is null || keys.Count == 0)
            {
                result[axis] = new NifAnimationExpectedCurve(1, SceneAnimationChannelState.Constant,
                    SceneInterpolation.Linear, [], [], [Bits(0f)], null, null, null, null, null, axisOrigin);
                continue;
            }

            var keyType = group["keyType"]!.GetValue<uint>();
            result[axis] = keyType is Linear or Quadratic or Tbc or Constant
                ? ComponentCurve(group, 1, null, axisOrigin, options)
                : NifAnimationExpectedCurve.Refused(axisOrigin, "eulerAxisKeyType");
        }

        return result;
    }

    /// <summary>
    ///     The expected curve of a width-1 float interpolator (a morph weight or a scalar property): an
    ///     NiFloatInterpolator's NiFloatData keys with its pose Value as the keyed fallback, or a float B-spline; with no
    ///     keys, a Constant of the pose Value, else of <paramref name="storedWeightBits" /> (an embedded morpher's stored
    ///     weight).
    /// </summary>
    public static NifAnimationExpectedCurve FloatCurve(int interpolator, uint? storedWeightBits,
        IReadOnlyDictionary<int, JsonObject> facts, IReadOnlyDictionary<int, JsonObject> payloads,
        NifAnimationDocumentOracleOptions options)
    {
        var origin = $"interpolator {interpolator}";
        if (interpolator < 0)
        {
            return storedWeightBits is { } stored
                ? ConstantOf([stored], origin)
                : NifAnimationExpectedCurve.Refused(origin, "noInterpolator");
        }

        if (!facts.TryGetValue(interpolator, out var fact))
        {
            return NifAnimationExpectedCurve.Refused(origin, "no probe facts for the block");
        }

        var type = fact["type"]!.GetValue<string>();
        if (type is "NiBSplineCompFloatInterpolator" or "NiBSplineFloatInterpolator")
        {
            return ScalarBspline(interpolator, fact, 1, storedWeightBits, facts, payloads);
        }

        if (type != "NiFloatInterpolator")
        {
            return NifAnimationExpectedCurve.Refused($"{origin} {type}", "notFloatInterpolator");
        }

        var valueBits = fact["valueBits"]!.GetValue<uint>();
        uint[]? fallback = valueBits == NifAnimationEngineRules.InvalidFloatBits ? null : new[] { valueBits };
        var data = fact["data"]!.GetValue<int>();
        var chosen = Chosen(fallback, storedWeightBits);
        if (data < 0)
        {
            return ConstantOf(chosen, origin);
        }

        origin = $"{origin} data {data}";
        if (!payloads.TryGetValue(data, out var group))
        {
            return NifAnimationExpectedCurve.Refused(origin, "no probe payload for the data block");
        }

        if (group["keys"] is not JsonArray { Count: > 0 })
        {
            return ConstantOf(chosen, origin);
        }

        return ComponentCurve(group, 1, FiniteOrNull(fallback), origin, options);
    }

    /// <summary>
    ///     The expected curve of a width-3 color property: an NiPoint3Interpolator's NiPosData keys (stored order) with
    ///     the pose Value as fallback, or a Point3 B-spline; with no keys, a Constant of the pose Value.
    /// </summary>
    public static NifAnimationExpectedCurve ColorCurve(int interpolator, IReadOnlyDictionary<int, JsonObject> facts,
        IReadOnlyDictionary<int, JsonObject> payloads, NifAnimationDocumentOracleOptions options)
    {
        var origin = $"interpolator {interpolator}";
        if (!facts.TryGetValue(interpolator, out var fact))
        {
            return NifAnimationExpectedCurve.Refused(origin, "no probe facts for the block");
        }

        var type = fact["type"]!.GetValue<string>();
        if (type is "NiBSplineCompPoint3Interpolator" or "NiBSplinePoint3Interpolator")
        {
            return ScalarBspline(interpolator, fact, 3, null, facts, payloads);
        }

        if (type != "NiPoint3Interpolator")
        {
            return NifAnimationExpectedCurve.Refused($"{origin} {type}", "notPoint3Interpolator");
        }

        var words = BitsArray(fact["valueBits"]);
        var sentinels = words.Count(static bits => bits == NifAnimationEngineRules.InvalidFloatBits);
        if (sentinels != 0 && sentinels != words.Length)
        {
            return NifAnimationExpectedCurve.Refused(origin, "staticMixesSentinel");
        }

        uint[]? fallback = sentinels == 0 ? words : null;
        var data = fact["data"]!.GetValue<int>();
        if (data < 0)
        {
            return ConstantOf(fallback, origin);
        }

        origin = $"{origin} data {data}";
        if (!payloads.TryGetValue(data, out var group))
        {
            return NifAnimationExpectedCurve.Refused(origin, "no probe payload for the data block");
        }

        if (group["keys"] is not JsonArray { Count: > 0 })
        {
            return ConstantOf(fallback, origin);
        }

        return ComponentCurve(group, 1, FiniteOrNull(fallback), origin, options);
    }

    /// <summary>
    ///     The expected visibility curve: an NiBoolInterpolator or NiBoolTimelineInterpolator's NiBoolData CONST keys as a
    ///     Step curve of the stored bytes (0 or 1) with the pose byte as fallback when it is 0 or 1; with no keys, a
    ///     Constant of the pose byte.
    /// </summary>
    public static NifAnimationExpectedCurve VisibilityCurve(int interpolator, IReadOnlyDictionary<int, JsonObject> facts,
        IReadOnlyDictionary<int, JsonObject> payloads)
    {
        var origin = $"interpolator {interpolator}";
        if (!facts.TryGetValue(interpolator, out var fact))
        {
            return NifAnimationExpectedCurve.Refused(origin, "no probe facts for the block");
        }

        var type = fact["type"]!.GetValue<string>();
        if (type is not ("NiBoolInterpolator" or "NiBoolTimelineInterpolator"))
        {
            return NifAnimationExpectedCurve.Refused($"{origin} {type}", "notBoolInterpolator");
        }

        var value = fact["value"]!.GetValue<int>();
        uint[]? fallback = value is 0 or 1 ? new[] { Bits(value) } : null;
        var data = fact["data"]!.GetValue<int>();
        JsonObject? group = null;
        if (data >= 0)
        {
            origin = $"{origin} data {data}";
            if (!payloads.TryGetValue(data, out group))
            {
                return NifAnimationExpectedCurve.Refused(origin, "no probe payload for the data block");
            }
        }

        if (group?["keys"] is not JsonArray { Count: > 0 } keys)
        {
            // Shared requires step visibility even for a held value, so the held pose byte is a Step Constant.
            return value is 0 or 1 or 2 ? ConstantOf(fallback, origin, SceneInterpolation.Step) : NifAnimationExpectedCurve.Refused(origin,
                "visibilityNotBinary");
        }

        if (group!["keyType"]!.GetValue<uint>() != Constant)
        {
            return NifAnimationExpectedCurve.Refused(origin, "visibilityKeyType");
        }

        var times = new uint[keys.Count];
        var values = new uint[keys.Count];
        for (var key = 0; key < keys.Count; key++)
        {
            var entry = keys[key]!.AsObject();
            times[key] = entry["timeBits"]!.GetValue<uint>();
            var time = BitConverter.UInt32BitsToSingle(times[key]);
            if (!float.IsFinite(time) || (key > 0 && time <= BitConverter.UInt32BitsToSingle(times[key - 1])))
            {
                return NifAnimationExpectedCurve.Refused(origin, "invalidKeyTimes");
            }

            var stored = entry["value"]!.GetValue<int>();
            if (stored > 1)
            {
                return NifAnimationExpectedCurve.Refused(origin, "visibilityNotBinary");
            }

            values[key] = Bits(stored);
        }

        return new NifAnimationExpectedCurve(1, SceneAnimationChannelState.Keyed, SceneInterpolation.Step, times,
            values, fallback, null, null, null, null, null, origin);
    }

    /// <summary>The bits of a float.</summary>
    public static uint Bits(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

    /// <summary>A uint word, or every word of an array, from a probe member.</summary>
    public static uint[] BitsArray(JsonNode? node)
    {
        return node switch
        {
            JsonArray array => array.Select(static item => item!.GetValue<uint>()).ToArray(),
            null => [],
            _ => [node.GetValue<uint>()]
        };
    }

    private static NifAnimationExpectedCurve KeyframeChannel(int interpolator, JsonObject fact,
        SceneTransformProperty property, IReadOnlyDictionary<int, JsonObject> payloads,
        SceneGamebryoSquadPolicy? squadPolicy, NifAnimationDocumentOracleOptions options)
    {
        var transform = fact["transform"]!.AsObject();
        var words = StaticWords(transform, property);
        var data = fact["data"]!.GetValue<int>();
        var origin = $"interpolator {interpolator}";
        JsonObject? group = null;
        if (data >= 0)
        {
            origin = $"{origin} data {data}";
            if (!payloads.TryGetValue(data, out var payload))
            {
                return NifAnimationExpectedCurve.Refused(origin, "no probe payload for the data block");
            }

            group = property switch
            {
                SceneTransformProperty.Translation => payload["translations"]!.AsObject(),
                SceneTransformProperty.Rotation => payload["rotation"]!.AsObject(),
                _ => payload["scales"]!.AsObject()
            };
            if (property == SceneTransformProperty.Rotation && group["keyType"]?.GetValue<uint>() == Euler)
            {
                return NifAnimationExpectedCurve.Refused(origin, "an XYZ_ROTATION rotation is an Euler track");
            }
        }

        var hasKeys = group?["keys"] is JsonArray { Count: > 0 };
        if (!TryState(property, words, hasKeys, options, out var state, out var staticValue, out var stateRefusal))
        {
            return NifAnimationExpectedCurve.Refused(origin, stateRefusal!);
        }

        if (!hasKeys)
        {
            return new NifAnimationExpectedCurve(Width(property), state, SceneInterpolation.Linear, [], [], staticValue,
                null, null, null, null, null, origin);
        }

        return property == SceneTransformProperty.Rotation
            ? RotationCurve(group!, staticValue, squadPolicy, origin)
            : ComponentCurve(group!, property == SceneTransformProperty.Scale ? 3 : 1, staticValue, origin, options);
    }

    private static NifAnimationExpectedCurve BsplineTransformChannel(int interpolator, JsonObject fact,
        SceneTransformProperty property, IReadOnlyDictionary<int, JsonObject> facts,
        IReadOnlyDictionary<int, JsonObject> payloads, NifAnimationDocumentOracleOptions options)
    {
        var origin = $"interpolator {interpolator}";
        if (!payloads.TryGetValue(interpolator, out var payload))
        {
            return NifAnimationExpectedCurve.Refused(origin, "no probe payload for the interpolator");
        }

        var name = property switch
        {
            SceneTransformProperty.Translation => "translation",
            SceneTransformProperty.Rotation => "rotation",
            _ => "scale"
        };
        var channel = payload["channels"]![name]!.AsObject();
        var handle = channel["handle"]!.GetValue<uint>();
        var words = StaticWords(payload["transform"]!.AsObject(), property);
        var hasKeys = handle != AbsentHandle;
        if (!TryState(property, words, hasKeys, options, out var state, out var staticValue, out var stateRefusal))
        {
            return NifAnimationExpectedCurve.Refused(origin, stateRefusal!);
        }

        if (!hasKeys)
        {
            return new NifAnimationExpectedCurve(Width(property), state, SceneInterpolation.Linear, [], [], staticValue,
                null, null, null, null, null, origin);
        }

        var (stored, replication) = property switch
        {
            SceneTransformProperty.Translation => (3, 1),
            SceneTransformProperty.Rotation => (4, 1),
            _ => (1, 3)
        };
        var spline = Spline(interpolator, fact, payload, channel, handle, stored, replication, property, facts, payloads,
            out var refusal, out origin);
        return spline is null
            ? NifAnimationExpectedCurve.Refused(origin, refusal!)
            : new NifAnimationExpectedCurve(stored * replication, state, SceneInterpolation.BSpline, [], [], staticValue,
                spline, null, null, null, null, origin);
    }

    /// <summary>A float (width 1) or Point3 (width 3) B-spline interpolator's one channel.</summary>
    private static NifAnimationExpectedCurve ScalarBspline(int interpolator, JsonObject fact, int width,
        uint? storedWeightBits, IReadOnlyDictionary<int, JsonObject> facts,
        IReadOnlyDictionary<int, JsonObject> payloads)
    {
        var origin = $"interpolator {interpolator}";
        if (!payloads.TryGetValue(interpolator, out var payload) || payload["channels"] is not JsonObject channels ||
            channels.Count != 1)
        {
            return NifAnimationExpectedCurve.Refused(origin, "no probe payload for the interpolator");
        }

        var channel = channels.First().Value!.AsObject();
        var handle = channel["handle"]!.GetValue<uint>();
        var words = BitsArray(payload["valueBits"]);
        var sentinels = words.Count(static bits => bits == NifAnimationEngineRules.InvalidFloatBits);
        if (sentinels != 0 && sentinels != words.Length)
        {
            return NifAnimationExpectedCurve.Refused(origin, "staticMixesSentinel");
        }

        uint[]? fallback = sentinels == 0 ? words : null;
        if (handle == AbsentHandle)
        {
            return ConstantOf(Chosen(fallback, storedWeightBits), origin);
        }

        var spline = Spline(interpolator, fact, payload, channel, handle, width, 1, null, facts, payloads,
            out var refusal, out origin);
        return spline is null
            ? NifAnimationExpectedCurve.Refused(origin, refusal!)
            : new NifAnimationExpectedCurve(width, SceneAnimationChannelState.Keyed, SceneInterpolation.BSpline, [], [],
                FiniteOrNull(fallback), spline, null, null, null, null, origin);
    }

    /// <summary>
    ///     Slices one channel's controls from the NiBSplineData: n = the basis's Num Control Points (at least 4), the
    ///     controls from the handle, each Shared component reading the stored component the permutation names.
    /// </summary>
    private static NifAnimationExpectedSpline? Spline(int interpolator, JsonObject fact, JsonObject payload,
        JsonObject channel, uint handle, int stored, int replication, SceneTransformProperty? property,
        IReadOnlyDictionary<int, JsonObject> facts, IReadOnlyDictionary<int, JsonObject> payloads,
        out string? refusal, out string origin)
    {
        var splineData = fact["splineData"]!.GetValue<int>();
        var basisData = fact["basisData"]!.GetValue<int>();
        origin = $"interpolator {interpolator} spline {splineData} basis {basisData}";
        refusal = null;
        if (!payloads.TryGetValue(splineData, out var store) || !facts.TryGetValue(basisData, out var basis))
        {
            refusal = "bsplineMissingData";
            return null;
        }

        var count = basis["numControlPoints"]!.GetValue<int>();
        if (count < 4)
        {
            refusal = "bsplineControlPointCount";
            return null;
        }

        var startBits = payload["startTimeBits"]!.GetValue<uint>();
        var stopBits = payload["stopTimeBits"]!.GetValue<uint>();
        var start = BitConverter.UInt32BitsToSingle(startBits);
        var stop = BitConverter.UInt32BitsToSingle(stopBits);
        if (!float.IsFinite(start) || !float.IsFinite(stop) || stop <= start)
        {
            refusal = "bsplineInvalidInterval";
            return null;
        }

        var width = stored * replication;
        var compact = channel.ContainsKey("halfRangeBits");
        var source = compact ? store["compactControlPoints"] as JsonArray : store["floatControlPointsBits"] as JsonArray;
        if (source is null || (long)handle + (long)count * stored > source.Count)
        {
            refusal = "bsplineControlsOutOfRange";
            return null;
        }

        if (compact)
        {
            var biasBits = channel["offsetBits"]!.GetValue<uint>();
            var multiplierBits = channel["halfRangeBits"]!.GetValue<uint>();
            var multiplier = BitConverter.UInt32BitsToSingle(multiplierBits);
            if (!float.IsFinite(BitConverter.UInt32BitsToSingle(biasBits)) || !float.IsFinite(multiplier))
            {
                refusal = "nonFiniteValue";
                return null;
            }

            if (multiplier < 0f)
            {
                refusal = "bsplineNegativeHalfRange";
                return null;
            }

            var shorts = new short[count * width];
            for (var control = 0; control < count; control++)
            {
                for (var component = 0; component < width; component++)
                {
                    shorts[control * width + component] = source[(int)handle + control * stored +
                                                                 SourceComponent(property, component)]!
                        .GetValue<short>();
                }
            }

            return new NifAnimationExpectedSpline(3, width, count, startBits, stopBits, shorts, biasBits,
                multiplierBits, null);
        }

        var floats = new uint[count * width];
        for (var control = 0; control < count; control++)
        {
            for (var component = 0; component < width; component++)
            {
                var bits = source[(int)handle + control * stored + SourceComponent(property, component)]!
                    .GetValue<uint>();
                if (!float.IsFinite(BitConverter.UInt32BitsToSingle(bits)))
                {
                    refusal = "nonFiniteValue";
                    return null;
                }

                floats[control * width + component] = bits;
            }
        }

        return new NifAnimationExpectedSpline(3, width, count, startBits, stopBits, null, null, null, floats);
    }

    /// <summary>
    ///     A float or Vector3 key group at width stored * replication: LINEAR to Linear, CONST to Step, QUADRATIC to
    ///     Hermite [Forward, Value, Backward] (exchanged under the control), TBC to Tbc with (T, C, B) in file order and
    ///     RE-19's endpoints.
    /// </summary>
    private static NifAnimationExpectedCurve ComponentCurve(JsonObject group, int replication, uint[]? staticValue,
        string origin, NifAnimationDocumentOracleOptions options)
    {
        var keys = group["keys"]!.AsArray();
        var keyType = group["keyType"]!.GetValue<uint>();
        SceneInterpolation interpolation;
        switch (keyType)
        {
            case Linear:
                interpolation = SceneInterpolation.Linear;
                break;
            case Constant:
                interpolation = SceneInterpolation.Step;
                break;
            case Quadratic:
                interpolation = SceneInterpolation.Hermite;
                break;
            case Tbc:
                interpolation = SceneInterpolation.Tbc;
                break;
            default:
                return NifAnimationExpectedCurve.Refused(origin, "unknownKeyType");
        }

        if (!TryTimes(keys, out var times))
        {
            return NifAnimationExpectedCurve.Refused(origin, "invalidKeyTimes");
        }

        var storedValues = keys.Select(static key => BitsArray(key!["valueBits"])).ToArray();
        var stored = storedValues[0].Length;
        var width = stored * replication;
        var cubic = interpolation == SceneInterpolation.Hermite;
        var values = new uint[times.Length * width * (cubic ? 3 : 1)];
        for (var key = 0; key < times.Length; key++)
        {
            var entry = keys[key]!.AsObject();
            var forward = cubic ? BitsArray(entry["forwardBits"]) : null;
            var backward = cubic ? BitsArray(entry["backwardBits"]) : null;
            for (var component = 0; component < width; component++)
            {
                var source = component / replication;
                var value = storedValues[key][source];
                if (!float.IsFinite(BitConverter.UInt32BitsToSingle(value)))
                {
                    return NifAnimationExpectedCurve.Refused(origin, "nonFiniteValue");
                }

                if (!cubic)
                {
                    values[key * width + component] = value;
                    continue;
                }

                var incoming = options.SwapHermiteForwardBackward ? backward![source] : forward![source];
                var outgoing = options.SwapHermiteForwardBackward ? forward![source] : backward![source];
                if (!float.IsFinite(BitConverter.UInt32BitsToSingle(incoming)) ||
                    !float.IsFinite(BitConverter.UInt32BitsToSingle(outgoing)))
                {
                    return NifAnimationExpectedCurve.Refused(origin, "nonFiniteValue");
                }

                var offset = key * 3 * width;
                values[offset + component] = incoming;
                values[offset + width + component] = value;
                values[offset + 2 * width + component] = outgoing;
            }
        }

        if (interpolation != SceneInterpolation.Tbc)
        {
            return new NifAnimationExpectedCurve(width, SceneAnimationChannelState.Keyed, interpolation, times, values,
                staticValue, null, null, null, null, null, origin);
        }

        var parameters = new uint[times.Length * 3];
        for (var key = 0; key < times.Length; key++)
        {
            var tbc = BitsArray(keys[key]!["tbcBits"]);
            for (var part = 0; part < 3; part++)
            {
                if (!float.IsFinite(BitConverter.UInt32BitsToSingle(tbc[part])))
                {
                    return NifAnimationExpectedCurve.Refused(origin, "nonFiniteValue");
                }

                parameters[key * 3 + part] = tbc[part];
            }
        }

        uint[]? start = null;
        uint[]? end = null;
        if (times.Length >= 2)
        {
            var last = times.Length - 1;
            start = new uint[width];
            end = new uint[width];
            for (var component = 0; component < stored; component++)
            {
                var outgoing = NifAnimationEngineRules.StartOutgoing(F(storedValues[0][component]),
                    F(storedValues[1][component]), F(parameters[0]), F(parameters[1]), F(parameters[2]));
                var incoming = NifAnimationEngineRules.EndIncoming(F(storedValues[last - 1][component]),
                    F(storedValues[last][component]), F(parameters[last * 3]), F(parameters[last * 3 + 1]),
                    F(parameters[last * 3 + 2]));
                if (!float.IsFinite(outgoing) || !float.IsFinite(incoming))
                {
                    return NifAnimationExpectedCurve.Refused(origin, "nonFiniteValue");
                }

                for (var copy = 0; copy < replication; copy++)
                {
                    start[component * replication + copy] = Bits(outgoing);
                    end[component * replication + copy] = Bits(incoming);
                }
            }
        }

        return new NifAnimationExpectedCurve(width, SceneAnimationChannelState.Keyed, SceneInterpolation.Tbc, times,
            values, staticValue, null, parameters, start, end, null, origin);
    }

    /// <summary>
    ///     A quaternion key group: LINEAR and CONST through RE-17 steps 1 to 3 and the permutation; TBC and QUADRATIC
    ///     through RE-24 (chain and clamp only, then the inner points; one key stands as its own inner points).
    /// </summary>
    private static NifAnimationExpectedCurve RotationCurve(JsonObject group, uint[]? staticValue,
        SceneGamebryoSquadPolicy? squadPolicy, string origin)
    {
        var keys = group["keys"]!.AsArray();
        var keyType = group["keyType"]!.GetValue<uint>();
        var raw = keys.Select(static key => BitsArray(key!["valueBits"]).Select(F).ToArray()).ToList();
        switch (keyType)
        {
            case Linear:
            case Constant:
            {
                if (!TryTimes(keys, out var times))
                {
                    return NifAnimationExpectedCurve.Refused(origin, "invalidKeyTimes");
                }

                var normalized = NifAnimationEngineRules.LinearRotationKeys(raw);
                var values = new uint[times.Length * 4];
                for (var key = 0; key < times.Length; key++)
                {
                    var q = normalized[key];
                    if (!q.All(float.IsFinite))
                    {
                        return NifAnimationExpectedCurve.Refused(origin, "nonFiniteValue");
                    }

                    if (MathF.Abs(new Quaternion(q[0], q[1], q[2], q[3]).LengthSquared() - 1f) > 0.0001f)
                    {
                        return NifAnimationExpectedCurve.Refused(origin, "nonUnitRotation");
                    }

                    for (var component = 0; component < 4; component++)
                    {
                        values[key * 4 + component] = Bits(q[component]);
                    }
                }

                return new NifAnimationExpectedCurve(4, SceneAnimationChannelState.Keyed,
                    keyType == Linear ? SceneInterpolation.GamebryoCounterWarpedNlerp : SceneInterpolation.Step,
                    times, values, staticValue, null, null, null, null, null, origin);
            }
            case Tbc:
            case Quadratic:
                return SquadCurve(keys, keyType, raw, staticValue, squadPolicy, origin);
            default:
                return NifAnimationExpectedCurve.Refused(origin, "unknownKeyType");
        }
    }

    private static NifAnimationExpectedCurve SquadCurve(JsonArray keys, uint keyType, List<float[]> raw,
        uint[]? staticValue, SceneGamebryoSquadPolicy? squadPolicy, string origin)
    {
        if (squadPolicy is not { } policy)
        {
            return NifAnimationExpectedCurve.Refused(origin, "squadPolicyPs3");
        }

        var times = new uint[keys.Count];
        var timeValues = new float[keys.Count];
        for (var key = 0; key < keys.Count; key++)
        {
            times[key] = keys[key]!["timeBits"]!.GetValue<uint>();
            timeValues[key] = F(times[key]);
            if (!float.IsFinite(timeValues[key]))
            {
                return NifAnimationExpectedCurve.Refused(origin, "invalidKeyTimes");
            }

            if (key > 0 && timeValues[key] == timeValues[key - 1])
            {
                return NifAnimationExpectedCurve.Refused(origin, "squadZeroLengthSpan");
            }

            if (key > 0 && timeValues[key] < timeValues[key - 1])
            {
                return NifAnimationExpectedCurve.Refused(origin, "invalidKeyTimes");
            }
        }

        var aligned = raw.Select(static key => (float[])key.Clone()).ToArray();
        float[][] incoming;
        float[][] outgoing;
        if (aligned.Length == 1)
        {
            incoming = aligned;
            outgoing = aligned;
        }
        else
        {
            NifAnimationEngineRules.AlignChainAndClampW(aligned);
            if (keyType == Tbc)
            {
                var tbc = keys.Select(static key => BitsArray(key!["tbcBits"])).ToArray();
                (incoming, outgoing) = NifAnimationEngineRules.TbcInnerPoints(timeValues, aligned,
                    tbc.Select(static t => F(t[0])).ToArray(), tbc.Select(static t => F(t[1])).ToArray(),
                    tbc.Select(static t => F(t[2])).ToArray());
            }
            else
            {
                incoming = NifAnimationEngineRules.QuadraticInnerPoints(aligned);
                outgoing = incoming;
            }
        }

        var values = new uint[keys.Count * 12];
        for (var key = 0; key < keys.Count; key++)
        {
            var parts = new[] { incoming[key], aligned[key], outgoing[key] };
            for (var part = 0; part < 3; part++)
            {
                var q = parts[part];
                if (!q.All(float.IsFinite))
                {
                    return NifAnimationExpectedCurve.Refused(origin, "nonFiniteValue");
                }

                if (q.All(static c => c == 0f))
                {
                    return NifAnimationExpectedCurve.Refused(origin, "zeroQuaternion");
                }

                // W, X, Y, Z to X, Y, Z, W.
                values[key * 12 + part * 4] = Bits(q[1]);
                values[key * 12 + part * 4 + 1] = Bits(q[2]);
                values[key * 12 + part * 4 + 2] = Bits(q[3]);
                values[key * 12 + part * 4 + 3] = Bits(q[0]);
            }
        }

        return new NifAnimationExpectedCurve(4, SceneAnimationChannelState.Keyed, SceneInterpolation.GamebryoSquad,
            times, values, staticValue, null, null, null, null, policy, origin);
    }

    /// <summary>
    ///     Plan section 1.3: keys present gives Keyed (a non-sentinel static kept as the fallback); no keys gives Constant
    ///     with a non-sentinel static, NotDriven with the all-sentinel static (Constant holding the sentinel under the
    ///     control). A static that mixes the sentinel with authored words is refused.
    /// </summary>
    private static bool TryState(SceneTransformProperty property, uint[] words, bool hasKeys,
        NifAnimationDocumentOracleOptions options, out SceneAnimationChannelState state, out uint[]? staticValue,
        out string? refusal)
    {
        refusal = null;
        staticValue = null;
        var sentinels = words.Count(static bits => bits == NifAnimationEngineRules.InvalidFloatBits);
        if (sentinels == words.Length)
        {
            if (hasKeys)
            {
                state = SceneAnimationChannelState.Keyed;
            }
            else if (options.SentinelStaticAsConstant)
            {
                state = SceneAnimationChannelState.Constant;
                staticValue = Layout(property, words);
            }
            else
            {
                state = SceneAnimationChannelState.NotDriven;
            }

            return true;
        }

        if (sentinels != 0)
        {
            state = SceneAnimationChannelState.NotDriven;
            refusal = "static value mixes the sentinel with authored components";
            return false;
        }

        state = hasKeys ? SceneAnimationChannelState.Keyed : SceneAnimationChannelState.Constant;
        staticValue = Layout(property, words);
        return true;
    }

    /// <summary>A static in Shared's layout: translation as stored, rotation W, X, Y, Z to X, Y, Z, W, scale (s, s, s).</summary>
    private static uint[] Layout(SceneTransformProperty property, uint[] words)
    {
        return property switch
        {
            SceneTransformProperty.Translation => [words[0], words[1], words[2]],
            SceneTransformProperty.Rotation => [words[1], words[2], words[3], words[0]],
            _ => [words[0], words[0], words[0]]
        };
    }

    private static uint[] StaticWords(JsonObject transform, SceneTransformProperty property)
    {
        return property switch
        {
            SceneTransformProperty.Translation => BitsArray(transform["translationBits"]),
            SceneTransformProperty.Rotation => BitsArray(transform["rotationBits"]),
            _ => BitsArray(transform["scaleBits"])
        };
    }

    private static int Width(SceneTransformProperty property)
    {
        return property == SceneTransformProperty.Rotation ? 4 : 3;
    }

    /// <summary>The stored component a Shared component reads: identity, rotation W, X, Y, Z to X, Y, Z, W, scale 0.</summary>
    private static int SourceComponent(SceneTransformProperty? property, int component)
    {
        return property switch
        {
            SceneTransformProperty.Rotation => component == 3 ? 0 : component + 1,
            SceneTransformProperty.Scale => 0,
            _ => component
        };
    }

    private static bool TryTimes(JsonArray keys, out uint[] times)
    {
        times = new uint[keys.Count];
        for (var key = 0; key < keys.Count; key++)
        {
            times[key] = keys[key]!["timeBits"]!.GetValue<uint>();
            var time = F(times[key]);
            if (!float.IsFinite(time) || (key > 0 && time <= F(times[key - 1])))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A Constant curve of the given bits, or the reader's refusal when there is no value or it is not finite.</summary>
    private static NifAnimationExpectedCurve ConstantOf(uint[]? bits, string origin,
        SceneInterpolation interpolation = SceneInterpolation.Linear)
    {
        if (bits is null)
        {
            return NifAnimationExpectedCurve.Refused(origin, "no value: sentinel static and no keys");
        }

        return bits.All(static b => float.IsFinite(BitConverter.UInt32BitsToSingle(b)))
            ? new NifAnimationExpectedCurve(bits.Length, SceneAnimationChannelState.Constant, interpolation,
                [], [], bits, null, null, null, null, null, origin)
            : NifAnimationExpectedCurve.Refused(origin, "nonFiniteValue");
    }

    /// <summary>The pose value when there is one, else an embedded morpher's stored weight, else none.</summary>
    private static uint[]? Chosen(uint[]? fallback, uint? storedWeightBits)
    {
        if (fallback is not null)
        {
            return fallback;
        }

        return storedWeightBits is { } weight ? new[] { weight } : null;
    }

    private static uint[]? FiniteOrNull(uint[]? bits)
    {
        return bits is not null && bits.All(static b => float.IsFinite(BitConverter.UInt32BitsToSingle(b)))
            ? bits
            : null;
    }

    private static float F(uint bits)
    {
        return BitConverter.UInt32BitsToSingle(bits);
    }
}
