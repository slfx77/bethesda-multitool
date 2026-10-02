using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 14 (SA4): maps the interpolator behind one property track onto a Shared <see cref="SceneCurve" />
///     of the kind's width, through the slice-2 mappings (<see cref="NifModelCurveMapping.MapComponents" /> for keys,
///     <see cref="NifModelBsplineMapping" /> for B-splines) and the slice-1 lossless views. Pure over the source views;
///     <see cref="NifModelPropertyTracks" /> binds the results to targets and clips.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             Width 1 (alpha, emissive strength, the layer members): an NiFloatInterpolator's NiFloatData keys (LINEAR,
///             CONST, QUADRATIC as Hermite, TBC with the RE-19 endpoints), or an NiBSplineCompFloatInterpolator or
///             NiBSplineFloatInterpolator through <see cref="NifModelBsplineMapping.MapFloatChannel" />; with no keys, the
///             interpolator's pose Value as a Constant when it is not #INV_FLT#. A valid pose Value beside keys is kept as
///             the curve's static fallback.
///         </item>
///         <item>
///             Width 3 (the material colors): an NiPoint3Interpolator's NiPosData keys (x, y, z in stored order, the same
///             key rules), or an NiBSplineCompPoint3Interpolator or NiBSplinePoint3Interpolator through
///             <see cref="NifModelBsplineMapping.MapPoint3Channel" />; with no keys, the pose Value as a Constant when it
///             is not #INV_VEC3#. A pose Value that mixes the sentinel with authored components stays native (fail closed).
///         </item>
///         <item>
///             Visibility: an NiBoolInterpolator or NiBoolTimelineInterpolator's NiBoolData keys as a <see cref="SceneInterpolation.Step" />
///             curve whose values are the stored bytes, only when every byte is 0 or 1 (Shared refuses anything else) and
///             the stored key type is CONST (5): retail stores nothing else (556 of 556 embedded NiBoolData groups), and
///             the engine's evaluation of a LINEAR, QUADRATIC or TBC bool key is not established, so those fail closed.
///             With no keys, the pose Value byte as a Constant when it is 0 or 1 (2 is nif.xml's no-value default).
///         </item>
///         <item>
///             An NiUVData group (<see cref="MapUvGroup" />): the group's float keys as a width-1 curve; an empty group
///             gives no curve and no track.
///         </item>
///         <item>An NiBlend*Interpolator stays native as manager blend state; any other type is refused by kind.</item>
///     </list>
/// </remarks>
internal static class NifModelPropertyCurveMapping
{
    /// <summary>The Shared width of a color track.</summary>
    public const int ColorWidth = 3;

    private const string BlendInterpolatorType = "NiBlendInterpolator";
    private const string FloatInterpolatorType = "NiFloatInterpolator";
    private const string FloatDataType = "NiFloatData";
    private const string PosDataType = "NiPosData";
    private const string BoolDataType = "NiBoolData";
    private const int NoRef = -1;

    /// <summary>Maps the interpolator of one width-1 property track (alpha, emissive strength, a layer member).</summary>
    /// <param name="source">The file's views.</param>
    /// <param name="interpolatorRef">The stored Interpolator ref.</param>
    /// <returns>The result.</returns>
    public static NifModelPropertyCurveResult MapScalar(NifModelAnimationSource source, int interpolatorRef)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsBlock(interpolatorRef))
        {
            return NifModelPropertyCurveResult.Native(NoRef, [], NifModelAnimationReasons.NoInterpolator,
                NifModelAnimationReasons.NoInterpolatorCode);
        }

        if (source.TryReadFloatInterpolator(interpolatorRef, out var view))
        {
            return MapFloatInterpolator(source, interpolatorRef, view);
        }

        if (source.IsBsplineFloat(interpolatorRef))
        {
            return MapBspline(source, interpolatorRef, 1);
        }

        return Refused(source, interpolatorRef, FloatInterpolatorType, NifModelAnimationReasons.NotFloatInterpolator,
            NifModelAnimationReasons.NotFloatInterpolatorCode);
    }

    /// <summary>Maps the interpolator of one width-3 property track (a material color).</summary>
    /// <param name="source">The file's views.</param>
    /// <param name="interpolatorRef">The stored Interpolator ref.</param>
    /// <returns>The result.</returns>
    public static NifModelPropertyCurveResult MapColor(NifModelAnimationSource source, int interpolatorRef)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsBlock(interpolatorRef))
        {
            return NifModelPropertyCurveResult.Native(NoRef, [], NifModelAnimationReasons.NoInterpolator,
                NifModelAnimationReasons.NoInterpolatorCode);
        }

        if (source.TryReadPoint3Interpolator(interpolatorRef, out var view))
        {
            return MapPoint3Interpolator(source, interpolatorRef, view);
        }

        if (source.IsBsplinePoint3(interpolatorRef))
        {
            return MapBspline(source, interpolatorRef, ColorWidth);
        }

        return Refused(source, interpolatorRef, "NiPoint3Interpolator", NifModelAnimationReasons.NotPoint3Interpolator,
            NifModelAnimationReasons.NotPoint3InterpolatorCode);
    }

    /// <summary>Maps the interpolator of one visibility track.</summary>
    /// <param name="source">The file's views.</param>
    /// <param name="interpolatorRef">The stored Interpolator ref.</param>
    /// <returns>The result.</returns>
    public static NifModelPropertyCurveResult MapVisibility(NifModelAnimationSource source, int interpolatorRef)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsBlock(interpolatorRef))
        {
            return NifModelPropertyCurveResult.Native(NoRef, [], NifModelAnimationReasons.NoInterpolator,
                NifModelAnimationReasons.NoInterpolatorCode);
        }

        if (source.TryReadBoolInterpolator(interpolatorRef, out var view))
        {
            return MapBoolInterpolator(source, interpolatorRef, view);
        }

        return Refused(source, interpolatorRef, "NiBoolInterpolator", NifModelAnimationReasons.NotBoolInterpolator,
            NifModelAnimationReasons.NotBoolInterpolatorCode);
    }

    /// <summary>Maps one NiUVData group (a float key group) to a width-1 curve; an empty group gives no curve.</summary>
    /// <param name="group">The group.</param>
    /// <param name="dataBlock">The NiUVData block, which the decision covers.</param>
    /// <returns>The result; null for an empty group.</returns>
    /// <exception cref="ArgumentException">The group is not a float group.</exception>
    public static NifModelPropertyCurveResult? MapUvGroup(in NifKeyGroupView group, int dataBlock)
    {
        if (group.Layout != NifKeyValueLayout.Float)
        {
            throw new ArgumentException("An NiUVData group is a float key group.", nameof(group));
        }

        if (group.Count == 0)
        {
            return null;
        }

        var mapped = NifModelCurveMapping.MapComponents(group, 1);
        int[] blocks = [dataBlock];
        if (mapped.Curve is not { } curve)
        {
            return NifModelPropertyCurveResult.Native(NoRef, blocks, NifModelAnimationReasons.Reason(mapped.Block),
                NifModelAnimationReasons.Code(mapped.Block), true);
        }

        return NifModelPropertyCurveResult.Typed(NoRef, blocks, Keyed(curve, null), curve.KeyType, true);
    }

    private static NifModelPropertyCurveResult MapFloatInterpolator(NifModelAnimationSource source, int interpolator,
        NifFloatInterpolatorView view)
    {
        List<int> blocks = [interpolator];
        float[]? fallback = view.HasNoValue ? null : new[] { BitConverter.UInt32BitsToSingle(view.ValueBits) };
        if (view.DataRef == NoRef)
        {
            return Constant(interpolator, blocks, fallback, false);
        }

        if (!source.Is(view.DataRef, FloatDataType))
        {
            return DataUnresolved(source, interpolator, blocks, view.DataRef);
        }

        blocks.Add(view.DataRef);
        if (!source.TryReadFloatData(view.DataRef, out var group) || group.EndOffset != source.BlockEnd(view.DataRef))
        {
            return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.DataUnreadable,
                NifModelAnimationReasons.DataUnreadableCode);
        }

        return MapKeys(interpolator, blocks, group, 1, fallback);
    }

    private static NifModelPropertyCurveResult MapPoint3Interpolator(NifModelAnimationSource source, int interpolator,
        NifPoint3InterpolatorView view)
    {
        List<int> blocks = [interpolator];
        if (view.MixesSentinel)
        {
            return NifModelPropertyCurveResult.Native(interpolator, blocks,
                NifModelAnimationReasons.StaticMixesSentinel, NifModelAnimationReasons.StaticMixesSentinelCode,
                source.IsBlock(view.DataRef));
        }

        float[]? fallback = view.HasNoValue
            ? null
            : new[]
            {
                BitConverter.UInt32BitsToSingle(view.XBits), BitConverter.UInt32BitsToSingle(view.YBits),
                BitConverter.UInt32BitsToSingle(view.ZBits)
            };
        if (view.DataRef == NoRef)
        {
            return Constant(interpolator, blocks, fallback, false);
        }

        if (!source.Is(view.DataRef, PosDataType))
        {
            return DataUnresolved(source, interpolator, blocks, view.DataRef);
        }

        blocks.Add(view.DataRef);
        if (!source.TryReadPosData(view.DataRef, out var group) || group.EndOffset != source.BlockEnd(view.DataRef))
        {
            return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.DataUnreadable,
                NifModelAnimationReasons.DataUnreadableCode);
        }

        return MapKeys(interpolator, blocks, group, ColorWidth, fallback);
    }

    private static NifModelPropertyCurveResult MapBoolInterpolator(NifModelAnimationSource source, int interpolator,
        NifBoolInterpolatorView view)
    {
        List<int> blocks = [interpolator];
        float[]? fallback = view.HasBinaryValue ? new float[] { view.Value } : null;
        if (view.DataRef == NoRef)
        {
            return view.Value <= 1 || view.Value == NifBoolInterpolatorView.NoValue
                ? Constant(interpolator, blocks, fallback, false, SceneInterpolation.Step)
                : NifModelPropertyCurveResult.Native(interpolator, blocks,
                    NifModelAnimationReasons.VisibilityNotBinary, NifModelAnimationReasons.VisibilityNotBinaryCode);
        }

        if (!source.Is(view.DataRef, BoolDataType))
        {
            return DataUnresolved(source, interpolator, blocks, view.DataRef);
        }

        blocks.Add(view.DataRef);
        if (!source.TryReadBoolData(view.DataRef, out var group) || group.EndOffset != source.BlockEnd(view.DataRef))
        {
            return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.DataUnreadable,
                NifModelAnimationReasons.DataUnreadableCode);
        }

        if (group.Count == 0)
        {
            return view.Value <= 1 || view.Value == NifBoolInterpolatorView.NoValue
                ? Constant(interpolator, blocks, fallback, false, SceneInterpolation.Step)
                : NifModelPropertyCurveResult.Native(interpolator, blocks,
                    NifModelAnimationReasons.VisibilityNotBinary, NifModelAnimationReasons.VisibilityNotBinaryCode);
        }

        if (group.KeyType != (uint)NifKeyInterpolation.Constant)
        {
            return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.VisibilityKeyType,
                NifModelAnimationReasons.VisibilityKeyTypeCode, true);
        }

        var times = new float[group.Count];
        var values = new float[group.Count];
        for (var key = 0; key < times.Length; key++)
        {
            var time = group.Time(key);
            if (!float.IsFinite(time) || (key > 0 && time <= times[key - 1]))
            {
                var block = NifModelCurveBlock.InvalidKeyTimes;
                return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.Reason(block),
                    NifModelAnimationReasons.Code(block), true);
            }

            var value = group.ByteValue(key);
            if (value > 1)
            {
                return NifModelPropertyCurveResult.Native(interpolator, blocks,
                    NifModelAnimationReasons.VisibilityNotBinary, NifModelAnimationReasons.VisibilityNotBinaryCode,
                    true);
            }

            times[key] = time;
            values[key] = value;
        }

        var curve = new SceneCurve(1, times, values, SceneInterpolation.Step, SceneAnimationChannelState.Keyed,
            fallback);
        return NifModelPropertyCurveResult.Typed(interpolator, blocks, curve, group.KeyType, true);
    }

    /// <summary>A float or Point3 B-spline interpolator: its controls through the slice-2 B-spline mapping, or its static value.</summary>
    private static NifModelPropertyCurveResult MapBspline(NifModelAnimationSource source, int interpolator, int width)
    {
        List<int> blocks = [interpolator];
        NifBsplineInterpolatorView? view;
        var readable = width == 1
            ? source.TryReadBsplineFloat(interpolator, out view)
            : source.TryReadBsplinePoint3(interpolator, out view);
        if (!readable || view is null)
        {
            return NifModelPropertyCurveResult.Native(interpolator, blocks,
                NifModelAnimationReasons.InterpolatorUnreadable, NifModelAnimationReasons.InterpolatorUnreadableCode);
        }

        NifBsplineDataView? data = null;
        if (view.SplineDataRef != NoRef)
        {
            if (!source.IsBlock(view.SplineDataRef))
            {
                return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.DataUnresolved,
                    NifModelAnimationReasons.DataUnresolvedCode);
            }

            blocks.Add(view.SplineDataRef);
            if (!source.TryReadBsplineData(view.SplineDataRef, out var spline) || !spline.ConsumedExactly)
            {
                return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.DataUnreadable,
                    NifModelAnimationReasons.DataUnreadableCode);
            }

            data = spline;
        }

        uint? count = null;
        if (view.BasisDataRef != NoRef)
        {
            if (!source.IsBlock(view.BasisDataRef))
            {
                return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.DataUnresolved,
                    NifModelAnimationReasons.DataUnresolvedCode);
            }

            if (!blocks.Contains(view.BasisDataRef))
            {
                blocks.Add(view.BasisDataRef);
            }

            if (!source.TryReadBsplineBasis(view.BasisDataRef, out var basis))
            {
                return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.DataUnreadable,
                    NifModelAnimationReasons.DataUnreadableCode);
            }

            count = basis;
        }

        var fallback = StaticFallback(view.StaticValueBits);
        if (fallback is null && HasPartialSentinel(view.StaticValueBits))
        {
            return NifModelPropertyCurveResult.Native(interpolator, blocks,
                NifModelAnimationReasons.StaticMixesSentinel, NifModelAnimationReasons.StaticMixesSentinelCode,
                view.Handles[0] != NifModelBsplineMapping.AbsentHandle);
        }

        var result = width == 1
            ? NifModelBsplineMapping.MapFloatChannel(view, data, count)
            : NifModelBsplineMapping.MapPoint3Channel(view, data, count);
        if (result.IsEmpty)
        {
            return Constant(interpolator, blocks, fallback, false);
        }

        if (result.Spline is not { } curve)
        {
            return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.Reason(result.Block),
                NifModelAnimationReasons.Code(result.Block), true);
        }

        var scene = new SceneCurve(width, [], [], SceneInterpolation.BSpline, SceneAnimationChannelState.Keyed,
            FiniteFallback(fallback), curve);
        return NifModelPropertyCurveResult.Typed(interpolator, blocks, scene, 0, true);
    }

    /// <summary>A key group through the slice-2 mapping at the given width, with the pose value as the fallback.</summary>
    private static NifModelPropertyCurveResult MapKeys(int interpolator, List<int> blocks, in NifKeyGroupView group,
        int width, float[]? fallback)
    {
        if (group.Count == 0)
        {
            return Constant(interpolator, blocks, fallback, false);
        }

        var mapped = NifModelCurveMapping.MapComponents(group, 1);
        if (mapped.Curve is not { } curve)
        {
            return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.Reason(mapped.Block),
                NifModelAnimationReasons.Code(mapped.Block), true);
        }

        if (curve.ComponentCount != width)
        {
            throw new InvalidOperationException(
                $"A {group.Layout} group mapped to width {curve.ComponentCount}, not the track's {width}.");
        }

        return NifModelPropertyCurveResult.Typed(interpolator, blocks, Keyed(curve, FiniteFallback(fallback)),
            curve.KeyType, true);
    }

    /// <summary>The Shared curve of a mapped key curve: keyed, with the slice-2 interpolation, TBC parameters and endpoints.</summary>
    private static SceneCurve Keyed(NifModelCurve curve, float[]? fallback)
    {
        return new SceneCurve(curve.ComponentCount, curve.Times, curve.Values, curve.Interpolation,
            SceneAnimationChannelState.Keyed, fallback, null, curve.TbcParameters, curve.TbcEndpoints);
    }

    /// <summary>A Constant curve over the pose value, or the native reason when there is no finite value to hold.</summary>
    private static NifModelPropertyCurveResult Constant(int interpolator, IReadOnlyList<int> blocks, float[]? value,
        bool hasCurve, SceneInterpolation interpolation = SceneInterpolation.Linear)
    {
        if (value is null)
        {
            return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.PropertyNoValue,
                NifModelAnimationReasons.PropertyNoValueCode, hasCurve);
        }

        foreach (var component in value)
        {
            if (!float.IsFinite(component))
            {
                var block = NifModelCurveBlock.NonFiniteValue;
                return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.Reason(block),
                    NifModelAnimationReasons.Code(block), hasCurve);
            }
        }

        // Visibility passes Step: Shared requires binary step visibility even for a held value.
        var curve = new SceneCurve(value.Length, [], [], interpolation, SceneAnimationChannelState.Constant, value);
        return NifModelPropertyCurveResult.Typed(interpolator, blocks, curve, 0, hasCurve);
    }

    /// <summary>A keyed curve's static fallback: the pose value when every component is finite (Shared holds no other), else none.</summary>
    private static float[]? FiniteFallback(float[]? fallback)
    {
        if (fallback is null)
        {
            return null;
        }

        foreach (var component in fallback)
        {
            if (!float.IsFinite(component))
            {
                return null;
            }
        }

        return fallback;
    }

    /// <summary>A B-spline's static value as floats, or null when every word is the -FLT_MAX sentinel.</summary>
    private static float[]? StaticFallback(uint[] staticBits)
    {
        var sentinels = 0;
        foreach (var bits in staticBits)
        {
            if (bits == NifFloatInterpolatorView.InvalidValueBits)
            {
                sentinels++;
            }
        }

        if (sentinels != 0)
        {
            return null;
        }

        var value = new float[staticBits.Length];
        for (var index = 0; index < value.Length; index++)
        {
            value[index] = BitConverter.UInt32BitsToSingle(staticBits[index]);
        }

        return value;
    }

    /// <summary>True when some but not all static words are the sentinel.</summary>
    private static bool HasPartialSentinel(uint[] staticBits)
    {
        var sentinels = 0;
        foreach (var bits in staticBits)
        {
            if (bits == NifFloatInterpolatorView.InvalidValueBits)
            {
                sentinels++;
            }
        }

        return sentinels != 0 && sentinels != staticBits.Length;
    }

    private static NifModelPropertyCurveResult DataUnresolved(NifModelAnimationSource source, int interpolator,
        List<int> blocks, int dataRef)
    {
        if (source.IsBlock(dataRef))
        {
            blocks.Add(dataRef);
        }

        return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.DataUnresolved,
            NifModelAnimationReasons.DataUnresolvedCode);
    }

    /// <summary>The refusal of an interpolator whose type does not drive the kind: blend state, an unreadable block of the right type, or the wrong type.</summary>
    private static NifModelPropertyCurveResult Refused(NifModelAnimationSource source, int interpolator,
        string expectedType, string reason, string code)
    {
        int[] blocks = [interpolator];
        if (source.Inherits(interpolator, BlendInterpolatorType))
        {
            return NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.BlendState,
                NifModelAnimationReasons.BlendStateCode);
        }

        var type = source.TypeOf(interpolator);
        var rightType = string.Equals(type, expectedType, StringComparison.Ordinal) ||
                        (string.Equals(expectedType, "NiBoolInterpolator", StringComparison.Ordinal) &&
                         NifPropertyInterpolatorReader.IsBoolInterpolatorType(type));
        return rightType
            ? NifModelPropertyCurveResult.Native(interpolator, blocks, NifModelAnimationReasons.InterpolatorUnreadable,
                NifModelAnimationReasons.InterpolatorUnreadableCode)
            : NifModelPropertyCurveResult.Native(interpolator, blocks, reason, code);
    }
}
