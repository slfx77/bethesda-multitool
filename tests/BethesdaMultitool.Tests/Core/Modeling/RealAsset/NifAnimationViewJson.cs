using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Renders the runtime readers' lossless views in the schema of the cut-1b probe expectations
///     (<c>tools/scripts/nif_feature_probe.py</c> PAYLOADS, <c>nif_cover_expectations.py</c> animation facts, floats as
///     bits only), so <see cref="NifAnimationJsonComparer" /> can compare them member for member. Every value comes from
///     the view; the schema's derived labels (key type and value type names, cycle names) are recomputed from the stored
///     words, never copied from the expectation.
/// </summary>
internal static class NifAnimationViewJson
{
    private static readonly string[] CycleNames = ["LOOP", "REVERSE", "CLAMP"];
    private static readonly string[] TransformChannels = ["translation", "rotation", "scale"];

    /// <summary>One key group (nif.xml <c>KeyGroup&lt;T&gt;</c>, or quaternion keys) as the probe's group payload.</summary>
    public static JsonObject KeyGroup(in NifKeyGroupView group, NifAnimationViewJsonOptions options)
    {
        var keys = new JsonArray();
        for (var key = 0; key < group.Count; key++)
        {
            keys.Add(Key(group, key, options));
        }

        return new JsonObject
        {
            ["numKeys"] = group.NumKeys,
            ["keyType"] = group.KeyType,
            ["keyTypeName"] = group.NumKeys == 0 ? null : KeyTypeName(group.KeyType),
            ["valueType"] = ValueTypeName(group.Layout),
            ["keys"] = keys
        };
    }

    /// <summary>
    ///     An NiTransformData / NiKeyframeData payload: rotation, translations, scales, and xyzRotations for Euler (the
    ///     first record's axes, the one the engine evaluates).
    /// </summary>
    public static JsonObject TransformDataPayload(in NifKeyframeDataView view, NifAnimationViewJsonOptions options)
    {
        var rotation = view.Rotation;
        var payload = new JsonObject
        {
            ["rotation"] = rotation.IsEuler ? EulerRotation(rotation, options) : KeyGroup(rotation.Keys, options),
            ["translations"] = KeyGroup(view.Translations, options),
            ["scales"] = KeyGroup(view.Scales, options)
        };
        if (rotation.IsEuler)
        {
            payload["xyzRotations"] = new JsonArray(
                KeyGroup(rotation.EulerX, options),
                KeyGroup(rotation.EulerY, options),
                KeyGroup(rotation.EulerZ, options));
        }

        return payload;
    }

    /// <summary>An NiTransformData / NiKeyframeData block's animation facts: counts and key types.</summary>
    public static JsonObject TransformDataFacts(in NifKeyframeDataView view)
    {
        var rotation = view.Rotation;
        var facts = new JsonObject { ["numRotationKeys"] = rotation.StoredKeyCount };
        if (rotation.StoredKeyCount != 0)
        {
            facts["rotationType"] = rotation.KeyType;
        }

        if (rotation.IsEuler)
        {
            facts["xyzRotations"] = new JsonArray(
                CountAndType(rotation.EulerX),
                CountAndType(rotation.EulerY),
                CountAndType(rotation.EulerZ));
        }

        facts["translations"] = CountAndType(view.Translations);
        facts["scales"] = CountAndType(view.Scales);
        return facts;
    }

    /// <summary>A single-group key-data block's animation facts: <c>keys</c> = [count, key type].</summary>
    public static JsonObject KeyDataFacts(in NifKeyGroupView group)
    {
        return new JsonObject { ["keys"] = CountAndType(group) };
    }

    /// <summary>An NiBSpline*Interpolator payload: times, the static value, and per channel its handle (and Comp scalars).</summary>
    public static JsonObject BsplinePayload(NifBsplineInterpolatorView view)
    {
        var payload = new JsonObject
        {
            ["startTimeBits"] = view.StartTimeBits,
            ["stopTimeBits"] = view.StopTimeBits
        };
        AddStaticValue(payload, view);
        var channels = new JsonObject();
        var names = ChannelNames(view.Kind);
        for (var channel = 0; channel < view.ChannelCount; channel++)
        {
            var entry = new JsonObject { ["handle"] = view.Handles[channel] };
            if (view.Compact)
            {
                entry["offsetBits"] = view.OffsetBits[channel];
                entry["halfRangeBits"] = view.HalfRangeBits[channel];
            }

            channels[names[channel]] = entry;
        }

        payload["channels"] = channels;
        return payload;
    }

    /// <summary>An NiBSpline*Interpolator's animation facts: times, refs, the static value, handles and Comp scalars.</summary>
    public static JsonObject BsplineFacts(NifBsplineInterpolatorView view)
    {
        var facts = new JsonObject
        {
            ["startTimeBits"] = view.StartTimeBits,
            ["stopTimeBits"] = view.StopTimeBits,
            ["splineData"] = view.SplineDataRef,
            ["basisData"] = view.BasisDataRef
        };
        AddStaticValue(facts, view);
        if (view.Kind == NifBsplineInterpolatorKind.Transform)
        {
            facts["handles"] = Words(view.Handles);
        }
        else
        {
            facts["handle"] = view.Handles[0];
        }

        if (view.Compact)
        {
            var offsets = new JsonArray();
            for (var channel = 0; channel < view.ChannelCount; channel++)
            {
                offsets.Add(JsonValue.Create(view.OffsetBits[channel]));
                offsets.Add(JsonValue.Create(view.HalfRangeBits[channel]));
            }

            facts["offsetsBits"] = offsets;
        }

        return facts;
    }

    /// <summary>An NiBSplineData payload: both arrays whole.</summary>
    public static JsonObject BsplineDataPayload(in NifBsplineDataView view)
    {
        var floats = new JsonArray();
        for (var index = 0; index < view.FloatControlPointCount; index++)
        {
            floats.Add(JsonValue.Create(view.FloatControlPointBits(index)));
        }

        var compact = new JsonArray();
        for (var index = 0; index < view.CompactControlPointCount; index++)
        {
            compact.Add(JsonValue.Create(view.CompactControlPoint(index)));
        }

        return new JsonObject { ["floatControlPointsBits"] = floats, ["compactControlPoints"] = compact };
    }

    /// <summary>An NiBSplineData block's animation facts: the two counts.</summary>
    public static JsonObject BsplineDataFacts(in NifBsplineDataView view)
    {
        return new JsonObject
        {
            ["numFloatControlPoints"] = view.FloatControlPointCount,
            ["numCompactControlPoints"] = view.CompactControlPointCount
        };
    }

    /// <summary>The NiTimeController header members of any controller's animation facts.</summary>
    public static JsonObject ControllerHeader(NifTimeControllerHeader header)
    {
        return new JsonObject
        {
            ["nextController"] = header.NextControllerRef,
            ["flags"] = header.Flags,
            ["animType"] = header.IsAppInit ? 1 : 0,
            ["cycleType"] = CycleName((uint)header.RawCycle),
            ["active"] = header.IsActive,
            ["playBackwards"] = header.PlayBackwards,
            ["managerControlled"] = header.IsManagerControlled,
            ["frequencyBits"] = header.FrequencyBits,
            ["phaseBits"] = header.PhaseBits,
            ["startTimeBits"] = header.StartTimeBits,
            ["stopTimeBits"] = header.StopTimeBits,
            ["target"] = header.TargetRef
        };
    }

    /// <summary>An NiControllerSequence's animation facts, strings resolved through the raw table as Latin-1.</summary>
    public static JsonObject Sequence(NifControllerSequenceView view, NifHeaderStringTable strings)
    {
        var blocks = new JsonArray();
        foreach (var block in view.ControlledBlocks)
        {
            var entry = new JsonObject
            {
                ["interpolator"] = block.InterpolatorRef,
                ["controller"] = block.ControllerRef
            };
            if (block.Priority is { } priority)
            {
                entry["priority"] = priority;
            }

            entry["nodeName"] = Text(strings, block.NodeNameIndex);
            entry["propertyType"] = Text(strings, block.PropertyTypeIndex);
            entry["controllerType"] = Text(strings, block.ControllerTypeIndex);
            entry["controllerId"] = Text(strings, block.ControllerIdIndex);
            entry["interpolatorId"] = Text(strings, block.InterpolatorIdIndex);
            blocks.Add(entry);
        }

        var sequence = new JsonObject
        {
            ["name"] = Text(strings, view.NameIndex),
            ["arrayGrowBy"] = view.ArrayGrowBy,
            ["controlledBlocks"] = blocks,
            ["weightBits"] = view.WeightBits,
            ["textKeys"] = view.TextKeysRef,
            ["cycleType"] = CycleName(view.RawCycle),
            ["frequencyBits"] = view.FrequencyBits,
            ["startTimeBits"] = view.StartTimeBits,
            ["stopTimeBits"] = view.StopTimeBits,
            ["manager"] = view.ManagerRef,
            ["accumRootName"] = Text(strings, view.AccumRootNameIndex)
        };
        if (view.AnimNotesRef is { } animNotes)
        {
            sequence["animNotes"] = animNotes;
        }

        if (view.AnimNoteArrayRefs is { } animNoteArrays)
        {
            var refs = new JsonArray();
            foreach (var reference in animNoteArrays)
            {
                refs.Add(JsonValue.Create(reference));
            }

            sequence["animNoteArrays"] = refs;
        }

        return sequence;
    }

    /// <summary>
    ///     The probe's record of a 20.0.0.4 NiControllerSequence (cut 2, <c>p_sequence</c> at the inline-string
    ///     branch): the inline Name and Accum Root Name as Latin-1 text of their stored bytes, each controlled block's
    ///     refs, Priority, String Palette ref, the five stored offsets and the five resolved strings, the raw clock and the
    ///     sequence's own palette ref.
    /// </summary>
    public static JsonObject OblivionSequence(NifOblivionControllerSequenceView view)
    {
        var blocks = new JsonArray();
        foreach (var block in view.ControlledBlocks)
        {
            blocks.Add(new JsonObject
            {
                ["interpolator"] = block.InterpolatorRef,
                ["controller"] = block.ControllerRef,
                ["priority"] = block.Priority,
                ["stringPalette"] = block.StringPaletteRef,
                ["nodeNameOffset"] = block.NodeNameOffset,
                ["propertyTypeOffset"] = block.PropertyTypeOffset,
                ["controllerTypeOffset"] = block.ControllerTypeOffset,
                ["controllerIdOffset"] = block.ControllerIdOffset,
                ["interpolatorIdOffset"] = block.InterpolatorIdOffset,
                ["nodeName"] = block.NodeName,
                ["propertyType"] = block.PropertyType,
                ["controllerType"] = block.ControllerType,
                ["controllerId"] = block.ControllerId,
                ["interpolatorId"] = block.InterpolatorId
            });
        }

        return new JsonObject
        {
            ["name"] = Encoding.Latin1.GetString(view.NameBytes.Span),
            ["arrayGrowBy"] = view.ArrayGrowBy,
            ["controlledBlocks"] = blocks,
            ["weightBits"] = view.WeightBits,
            ["textKeys"] = view.TextKeysRef,
            ["cycleType"] = CycleName(view.RawCycle),
            ["frequencyBits"] = view.FrequencyBits,
            ["startTimeBits"] = view.StartTimeBits,
            ["stopTimeBits"] = view.StopTimeBits,
            ["manager"] = view.ManagerRef,
            ["accumRootName"] = Encoding.Latin1.GetString(view.AccumRootNameBytes.Span),
            ["stringPalette"] = view.StringPaletteRef
        };
    }

    /// <summary>The probe's record of an NiStringPalette (<c>p_stringpalette</c>): the palette as Latin-1 text and the repeated Length.</summary>
    public static JsonObject StringPalette(in NifStringPaletteView view)
    {
        return new JsonObject
        {
            ["palette"] = Encoding.Latin1.GetString(view.Palette.Span),
            ["length"] = view.Length
        };
    }

    /// <summary>An NiTextKeyExtraData's animation facts: the name and every key as stored, labels as Latin-1.</summary>
    public static JsonObject TextKeys(NifTextKeyExtraDataView view, NifHeaderStringTable strings)
    {
        var keys = new JsonArray();
        foreach (var key in view.Keys)
        {
            keys.Add(new JsonObject
            {
                ["timeBits"] = key.TimeBits,
                ["value"] = view.InlineStrings
                    ? Encoding.Latin1.GetString(key.InlineLabel.Span)
                    : Text(strings, key.LabelIndex)
            });
        }

        return new JsonObject
        {
            ["name"] = view.InlineStrings ? Encoding.Latin1.GetString(view.InlineName.Span) : Text(strings, view.NameIndex),
            ["textKeys"] = keys
        };
    }

    /// <summary>An NiTransformInterpolator's animation facts: the static transform and the Data ref.</summary>
    public static JsonObject TransformInterpolator(NifTransformInterpolatorView view)
    {
        return new JsonObject
        {
            ["transform"] = new JsonObject
            {
                ["translationBits"] = Words([view.TranslationXBits, view.TranslationYBits, view.TranslationZBits]),
                ["rotationBits"] = Words([view.RotationWBits, view.RotationXBits, view.RotationYBits,
                    view.RotationZBits]),
                ["scaleBits"] = view.ScaleBits
            },
            ["data"] = view.DataRef
        };
    }

    /// <summary>
    ///     The rotation member of an Euler payload, every value from the view: the quaternion group (numKeys, valueType and
    ///     keys come from <see cref="NifRotationKeysView.Keys" />, which an Euler block leaves empty), plus the stored
    ///     record count and the stored rotation type, which the quaternion group does not carry.
    /// </summary>
    private static JsonObject EulerRotation(in NifRotationKeysView rotation, NifAnimationViewJsonOptions options)
    {
        var group = KeyGroup(rotation.Keys, options);
        group["storedNumRotationKeys"] = rotation.StoredKeyCount;
        group["keyType"] = rotation.KeyType;
        group["keyTypeName"] = KeyTypeName(rotation.KeyType);
        return group;
    }

    private static JsonObject Key(in NifKeyGroupView group, int key, NifAnimationViewJsonOptions options)
    {
        var entry = new JsonObject { ["timeBits"] = group.TimeBits(key) };
        if (group.Layout == NifKeyValueLayout.Byte)
        {
            entry["value"] = group.ByteValue(key);
            if (group.HasTangents)
            {
                var forward = group.ForwardByte(key);
                var backward = group.BackwardByte(key);
                entry["forward"] = options.SwapForwardAndBackward ? backward : forward;
                entry["backward"] = options.SwapForwardAndBackward ? forward : backward;
            }
        }
        else
        {
            entry["valueBits"] = Value(group, key, options);
            if (group.HasTangents)
            {
                var forward = Tangent(group, key, true);
                var backward = Tangent(group, key, false);
                entry["forwardBits"] = options.SwapForwardAndBackward ? backward : forward;
                entry["backwardBits"] = options.SwapForwardAndBackward ? forward : backward;
            }
        }

        if (group.HasTbc)
        {
            entry["tbcBits"] = options.SwapContinuityAndBias
                ? Words([group.TensionBits(key), group.BiasBits(key), group.ContinuityBits(key)])
                : Words([group.TensionBits(key), group.ContinuityBits(key), group.BiasBits(key)]);
        }

        return entry;
    }

    private static JsonNode Value(in NifKeyGroupView group, int key, NifAnimationViewJsonOptions options)
    {
        if (group.Layout == NifKeyValueLayout.Float)
        {
            return JsonValue.Create(group.ValueBits(key, 0));
        }

        var components = new uint[group.ComponentCount];
        for (var component = 0; component < components.Length; component++)
        {
            components[component] = group.ValueBits(key, component);
        }

        if (group.Layout == NifKeyValueLayout.Quaternion && options.QuaternionXyzw)
        {
            components = [components[1], components[2], components[3], components[0]];
        }

        return Words(components);
    }

    private static JsonNode Tangent(in NifKeyGroupView group, int key, bool forward)
    {
        if (group.Layout == NifKeyValueLayout.Float)
        {
            return JsonValue.Create(forward ? group.ForwardBits(key, 0) : group.BackwardBits(key, 0));
        }

        var components = new uint[group.ComponentCount];
        for (var component = 0; component < components.Length; component++)
        {
            components[component] = forward ? group.ForwardBits(key, component) : group.BackwardBits(key, component);
        }

        return Words(components);
    }

    private static void AddStaticValue(JsonObject target, NifBsplineInterpolatorView view)
    {
        var words = view.StaticValueBits;
        switch (view.Kind)
        {
            case NifBsplineInterpolatorKind.Transform:
                target["transform"] = new JsonObject
                {
                    ["translationBits"] = Words(words[..3]),
                    ["rotationBits"] = Words(words[3..7]),
                    ["scaleBits"] = words[7]
                };
                break;
            case NifBsplineInterpolatorKind.Point3:
                target["valueBits"] = Words(words);
                break;
            default:
                target["valueBits"] = words[0];
                break;
        }
    }

    private static string[] ChannelNames(NifBsplineInterpolatorKind kind)
    {
        return kind switch
        {
            NifBsplineInterpolatorKind.Transform => TransformChannels,
            NifBsplineInterpolatorKind.Point3 => ["position"],
            _ => ["float"]
        };
    }

    private static JsonArray CountAndType(in NifKeyGroupView group)
    {
        return new JsonArray(JsonValue.Create(group.NumKeys), JsonValue.Create(group.KeyType));
    }

    private static JsonArray Words(IEnumerable<uint> words)
    {
        var array = new JsonArray();
        foreach (var word in words)
        {
            array.Add(JsonValue.Create(word));
        }

        return array;
    }

    /// <summary>A string index as the probe renders it: Latin-1 text, null for none, a marker when out of range.</summary>
    private static string? Text(NifHeaderStringTable strings, int index)
    {
        return NifAnimationStrings.TryGetLatin1(strings, index, out var text)
            ? text
            : $"<string index {index.ToString(CultureInfo.InvariantCulture)} out of range>";
    }

    private static string? KeyTypeName(uint keyType)
    {
        return keyType switch
        {
            1 => "LINEAR_KEY",
            2 => "QUADRATIC_KEY",
            3 => "TBC_KEY",
            4 => "XYZ_ROTATION_KEY",
            5 => "CONST_KEY",
            _ => null
        };
    }

    private static string ValueTypeName(NifKeyValueLayout layout)
    {
        return layout switch
        {
            NifKeyValueLayout.Byte => "byte",
            NifKeyValueLayout.Float => "float",
            NifKeyValueLayout.Vector3 => "Vector3",
            NifKeyValueLayout.Color4 => "Color4",
            _ => "Quaternion"
        };
    }

    private static string CycleName(uint rawCycle)
    {
        return rawCycle < CycleNames.Length
            ? CycleNames[rawCycle]
            : rawCycle.ToString(CultureInfo.InvariantCulture);
    }
}
