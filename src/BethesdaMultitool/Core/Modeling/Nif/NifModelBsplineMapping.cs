using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 2, plan section 1.4: maps one channel of an NiBSplineCompTransformInterpolator or
///     NiBSplineTransformInterpolator (the slice-1 views <see cref="NifBsplineInterpolatorView" /> and
///     <see cref="NifBsplineDataView" />) onto a Shared <see cref="SceneBSplineCurve" />; since slice 7 also the one
///     channel of an NiBSplineCompFloatInterpolator or NiBSplineFloatInterpolator (a morph weight), and since slice 14
///     the one channel of an NiBSplineCompPoint3Interpolator or NiBSplinePoint3Interpolator (a material color, width
///     3 in stored order). Pure; it allocates only the owned control array.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             A handle of 0xFFFF means the channel has no curve. Otherwise its controls are the
///             <c>n * width</c> scalars from the handle, with n the NiBSplineBasisData Num Control Points, admitted only
///             when n is at least 4 (degree 3).
///         </item>
///         <item>
///             Compact channels keep their signed shorts: <c>SceneBSplineCurve(3, width, start, stop, shorts, bias = Offset,
///             multiplier = Half Range)</c>, which Shared decodes in Float32 as bias + (s / 32767) * multiplier, the
///             runtime formula (NifBsplineTransformReader). A negative Half Range is refused, as BMT's reader refuses it.
///         </item>
///         <item>Uncompressed channels keep their Float32 controls, exactly as stored.</item>
///         <item>
///             Rotation controls are permuted per control from the file's W, X, Y, Z to Shared's X, Y, Z, W and are
///             otherwise untouched: no sign alignment and no normalization (RE-17 rule step 5; Shared normalizes after
///             the component spline, as the engine does). Scale controls are replicated three times. Both are exact,
///             because one bias and one multiplier serve every component of a channel.
///         </item>
///         <item>A float channel is one stored scalar, width 1, neither permuted nor replicated.</item>
///     </list>
/// </remarks>
internal static class NifModelBsplineMapping
{
    /// <summary>The handle of an absent channel.</summary>
    public const uint AbsentHandle = 0xFFFF;

    /// <summary>The degree of every NIF B-spline (NiBSplineBasis&lt;float, 3&gt;).</summary>
    public const int Degree = 3;

    /// <summary>The fewest controls a degree-3 curve admits.</summary>
    public const int MinimumControlPointCount = Degree + 1;

    /// <summary>Maps one channel of a transform B-spline interpolator.</summary>
    /// <param name="interpolator">An NiBSplineTransformInterpolator or NiBSplineCompTransformInterpolator view.</param>
    /// <param name="property">The channel: translation (handle 0), rotation (handle 1) or scale (handle 2).</param>
    /// <param name="data">The NiBSplineData the Spline Data ref resolves to, or null when it did not resolve.</param>
    /// <param name="controlPointCount">
    ///     The NiBSplineBasisData Num Control Points the Basis Data ref resolves to, or null when it did not resolve.
    /// </param>
    /// <returns>The curve, a blocked reason, or <see cref="NifModelBsplineResult.NoCurve" /> for a 0xFFFF handle.</returns>
    /// <exception cref="ArgumentException">The view is not a transform B-spline interpolator.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The property is undefined.</exception>
    public static NifModelBsplineResult MapTransformChannel(
        NifBsplineInterpolatorView interpolator,
        SceneTransformProperty property,
        NifBsplineDataView? data,
        uint? controlPointCount)
    {
        ArgumentNullException.ThrowIfNull(interpolator);
        if (interpolator.Kind != NifBsplineInterpolatorKind.Transform || interpolator.Handles.Length != 3 ||
            (interpolator.Compact && (interpolator.OffsetBits.Length != 3 || interpolator.HalfRangeBits.Length != 3)))
        {
            throw new ArgumentException(
                $"Only transform B-spline interpolators map to transform channels, not {interpolator.TypeName}.",
                nameof(interpolator));
        }

        var (channel, stored, replication) = property switch
        {
            SceneTransformProperty.Translation => (0, 3, 1),
            SceneTransformProperty.Rotation => (1, 4, 1),
            SceneTransformProperty.Scale => (2, 1, 3),
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown transform property.")
        };

        return MapChannel(interpolator, channel, stored, replication, property, data, controlPointCount);
    }

    /// <summary>
    ///     Maps the one channel of a Point3 B-spline interpolator (slice 14: a material color) to a width-3 curve, the
    ///     stored x, y, z in identity order.
    /// </summary>
    /// <param name="interpolator">An NiBSplinePoint3Interpolator or NiBSplineCompPoint3Interpolator view.</param>
    /// <param name="data">The NiBSplineData the Spline Data ref resolves to, or null when it did not resolve.</param>
    /// <param name="controlPointCount">
    ///     The NiBSplineBasisData Num Control Points the Basis Data ref resolves to, or null when it did not resolve.
    /// </param>
    /// <returns>The curve, a blocked reason, or <see cref="NifModelBsplineResult.NoCurve" /> for a 0xFFFF handle.</returns>
    /// <exception cref="ArgumentException">The view is not a Point3 B-spline interpolator.</exception>
    public static NifModelBsplineResult MapPoint3Channel(
        NifBsplineInterpolatorView interpolator,
        NifBsplineDataView? data,
        uint? controlPointCount)
    {
        ArgumentNullException.ThrowIfNull(interpolator);
        if (interpolator.Kind != NifBsplineInterpolatorKind.Point3 || interpolator.Handles.Length != 1 ||
            (interpolator.Compact && (interpolator.OffsetBits.Length != 1 || interpolator.HalfRangeBits.Length != 1)))
        {
            throw new ArgumentException(
                $"Only Point3 B-spline interpolators map to a Point3 channel, not {interpolator.TypeName}.",
                nameof(interpolator));
        }

        return MapChannel(interpolator, 0, 3, 1, null, data, controlPointCount);
    }

    /// <summary>Maps the one channel of a float B-spline interpolator (slice 7: a morph weight) to a width-1 curve.</summary>
    /// <param name="interpolator">An NiBSplineFloatInterpolator or NiBSplineCompFloatInterpolator view.</param>
    /// <param name="data">The NiBSplineData the Spline Data ref resolves to, or null when it did not resolve.</param>
    /// <param name="controlPointCount">
    ///     The NiBSplineBasisData Num Control Points the Basis Data ref resolves to, or null when it did not resolve.
    /// </param>
    /// <returns>The curve, a blocked reason, or <see cref="NifModelBsplineResult.NoCurve" /> for a 0xFFFF handle.</returns>
    /// <exception cref="ArgumentException">The view is not a float B-spline interpolator.</exception>
    public static NifModelBsplineResult MapFloatChannel(
        NifBsplineInterpolatorView interpolator,
        NifBsplineDataView? data,
        uint? controlPointCount)
    {
        ArgumentNullException.ThrowIfNull(interpolator);
        if (interpolator.Kind != NifBsplineInterpolatorKind.Float || interpolator.Handles.Length != 1 ||
            (interpolator.Compact && (interpolator.OffsetBits.Length != 1 || interpolator.HalfRangeBits.Length != 1)))
        {
            throw new ArgumentException(
                $"Only float B-spline interpolators map to a float channel, not {interpolator.TypeName}.",
                nameof(interpolator));
        }

        return MapChannel(interpolator, 0, 1, 1, null, data, controlPointCount);
    }

    /// <summary>
    ///     Copies one channel's controls (<paramref name="stored" /> scalars per control, each Shared component reading
    ///     the stored component <see cref="SourceComponent" /> names) into a Shared curve.
    /// </summary>
    /// <param name="interpolator">The interpolator view.</param>
    /// <param name="channel">The channel's index into the handles, offsets and half ranges.</param>
    /// <param name="stored">The stored scalars per control.</param>
    /// <param name="replication">How many times each stored scalar is repeated in the Shared vector.</param>
    /// <param name="property">The transform channel, or null for a float channel (identity component order).</param>
    /// <param name="data">The NiBSplineData, or null.</param>
    /// <param name="controlPointCount">The NiBSplineBasisData Num Control Points, or null.</param>
    /// <returns>The curve, a blocked reason, or no curve.</returns>
    private static NifModelBsplineResult MapChannel(
        NifBsplineInterpolatorView interpolator,
        int channel,
        int stored,
        int replication,
        SceneTransformProperty? property,
        NifBsplineDataView? data,
        uint? controlPointCount)
    {
        var handle = interpolator.Handles[channel];
        if (handle == AbsentHandle)
        {
            return NifModelBsplineResult.NoCurve;
        }

        if (data is not { } store || controlPointCount is not { } count)
        {
            return NifModelBsplineResult.Blocked(NifModelCurveBlock.BsplineMissingData);
        }

        if (count < MinimumControlPointCount || count > SceneBSplineCurve.MaximumControlPointCount)
        {
            return NifModelBsplineResult.Blocked(NifModelCurveBlock.BsplineControlPointCount);
        }

        var start = BitConverter.UInt32BitsToSingle(interpolator.StartTimeBits);
        var stop = BitConverter.UInt32BitsToSingle(interpolator.StopTimeBits);
        if (!float.IsFinite(start) || !float.IsFinite(stop) || stop <= start)
        {
            return NifModelBsplineResult.Blocked(NifModelCurveBlock.BsplineInvalidInterval);
        }

        var controls = (int)count;
        var available = interpolator.Compact ? store.CompactControlPointCount : store.FloatControlPointCount;
        if ((long)handle + (long)controls * stored > available)
        {
            return NifModelBsplineResult.Blocked(NifModelCurveBlock.BsplineControlsOutOfRange);
        }

        var width = stored * replication;
        var first = (int)handle;
        if (interpolator.Compact)
        {
            var bias = BitConverter.UInt32BitsToSingle(interpolator.OffsetBits[channel]);
            var multiplier = BitConverter.UInt32BitsToSingle(interpolator.HalfRangeBits[channel]);
            if (!float.IsFinite(bias) || !float.IsFinite(multiplier))
            {
                return NifModelBsplineResult.Blocked(NifModelCurveBlock.NonFiniteValue);
            }

            if (multiplier < 0f)
            {
                return NifModelBsplineResult.Blocked(NifModelCurveBlock.BsplineNegativeHalfRange);
            }

            var shorts = new short[controls * width];
            for (var control = 0; control < controls; control++)
            {
                for (var component = 0; component < width; component++)
                {
                    shorts[control * width + component] =
                        store.CompactControlPoint(first + control * stored + SourceComponent(property, component));
                }
            }

            return NifModelBsplineResult.Mapped(
                new SceneBSplineCurve(Degree, width, start, stop, shorts, bias, multiplier));
        }

        var floats = new float[controls * width];
        for (var control = 0; control < controls; control++)
        {
            for (var component = 0; component < width; component++)
            {
                var value = BitConverter.UInt32BitsToSingle(
                    store.FloatControlPointBits(first + control * stored + SourceComponent(property, component)));
                if (!float.IsFinite(value))
                {
                    return NifModelBsplineResult.Blocked(NifModelCurveBlock.NonFiniteValue);
                }

                floats[control * width + component] = value;
            }
        }

        return NifModelBsplineResult.Mapped(new SceneBSplineCurve(Degree, width, start, stop, floats));
    }

    /// <summary>
    ///     The stored component (within one control) that Shared component <paramref name="component" /> reads: the
    ///     identity for translation, a float channel and a Point3 channel, W, X, Y, Z to X, Y, Z, W for rotation, and the
    ///     single scale value for all three scale components.
    /// </summary>
    /// <param name="property">The channel, or null for a float channel.</param>
    /// <param name="component">The Shared component index.</param>
    /// <returns>The stored component index.</returns>
    private static int SourceComponent(SceneTransformProperty? property, int component)
    {
        return property switch
        {
            SceneTransformProperty.Rotation => component == 3 ? 0 : component + 1,
            SceneTransformProperty.Scale => 0,
            _ => component
        };
    }
}
