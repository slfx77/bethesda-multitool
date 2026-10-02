using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 2: composes the channel-state rule (<see cref="NifModelChannelStateMapping" />) with the key-curve
///     rule (<see cref="NifModelCurveMapping" />) or the B-spline rule (<see cref="NifModelBsplineMapping" />) for one
///     channel of a transform interpolator. Pure. The static value is classified first, so malformed statics throw even
///     when the curve is blocked.
/// </summary>
/// <remarks>
///     Since slice 13 the rotation channel of an NiTransformInterpolator whose data stores an XYZ_ROTATION (type 4) maps
///     to an Euler rotation (<see cref="NifModelCurveMapping.MapEulerRotation" />, RE-20): the interpolator's static
///     rotation is then irrelevant to the engine's output and rides along for native state only. Since slice 16b a TBC
///     or QUADRATIC quaternion rotation maps to a Squad curve under the file's platform policy (RE-24).
/// </remarks>
internal static class NifModelTransformChannelMapping
{
    /// <summary>Maps one channel of an NiTransformInterpolator (or BSRotAccumTransfInterpolator) and its key data.</summary>
    /// <param name="interpolator">The interpolator view.</param>
    /// <param name="data">The NiTransformData (or NiKeyframeData) its Data ref resolves to; null for no data.</param>
    /// <param name="property">The channel.</param>
    /// <param name="squad">The file's Squad policy (<see cref="NifModelSquadPolicy.Resolve" />); consulted for TBC and QUADRATIC quaternions only.</param>
    /// <returns>The channel, the Euler rotation, or the reason its keys cannot be mapped.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The property is undefined.</exception>
    /// <exception cref="InvalidDataException">The static value is malformed (see <see cref="NifModelChannelStateMapping" />).</exception>
    public static NifModelTransformChannelResult MapKeyframe(
        NifTransformInterpolatorView interpolator, NifKeyframeDataView? data, SceneTransformProperty property,
        in NifModelSquadPolicy squad)
    {
        if (data is { } eulerData && property == SceneTransformProperty.Rotation && eulerData.Rotation.IsEuler)
        {
            // The static is classified first (a malformed one throws), then kept for native state only (RE-20).
            var eulerState = NifModelChannelStateMapping.FromTransformInterpolator(interpolator, property, true);
            var euler = NifModelCurveMapping.MapEulerRotation(eulerData.Rotation);
            if (euler.IsBlocked)
            {
                return NifModelTransformChannelResult.Blocked(euler.Block);
            }

            return NifModelTransformChannelResult.EulerMapped(
                euler.Euler! with { StaticRotation = eulerState.StaticValue });
        }

        var curve = NifModelCurveResult.NoKeys;
        if (data is { } keyframes)
        {
            curve = property switch
            {
                SceneTransformProperty.Translation => NifModelCurveMapping.MapTranslation(keyframes.Translations),
                SceneTransformProperty.Rotation => NifModelCurveMapping.MapRotation(keyframes.Rotation, squad),
                SceneTransformProperty.Scale => NifModelCurveMapping.MapScale(keyframes.Scales),
                _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown transform property.")
            };
        }

        var state = NifModelChannelStateMapping.FromTransformInterpolator(interpolator, property, !curve.IsEmpty);
        if (curve.IsBlocked)
        {
            return NifModelTransformChannelResult.Blocked(curve.Block);
        }

        return NifModelTransformChannelResult.Mapped(
            new NifModelTransformChannel(property, state.State, state.StaticValue, curve.Curve, null));
    }

    /// <summary>Maps one channel of an NiBSplineTransformInterpolator or NiBSplineCompTransformInterpolator.</summary>
    /// <param name="interpolator">The interpolator view.</param>
    /// <param name="property">The channel.</param>
    /// <param name="data">The NiBSplineData its Spline Data ref resolves to, or null.</param>
    /// <param name="controlPointCount">The NiBSplineBasisData Num Control Points its Basis Data ref resolves to, or null.</param>
    /// <returns>The channel, or the reason its curve cannot be mapped.</returns>
    /// <exception cref="ArgumentException">The view is not a transform B-spline interpolator.</exception>
    /// <exception cref="InvalidDataException">The static value is malformed (see <see cref="NifModelChannelStateMapping" />).</exception>
    public static NifModelTransformChannelResult MapBspline(
        NifBsplineInterpolatorView interpolator,
        SceneTransformProperty property,
        NifBsplineDataView? data,
        uint? controlPointCount)
    {
        var state = NifModelChannelStateMapping.FromBsplineInterpolator(interpolator, property);
        var spline = NifModelBsplineMapping.MapTransformChannel(interpolator, property, data, controlPointCount);
        if (spline.IsBlocked)
        {
            return NifModelTransformChannelResult.Blocked(spline.Block);
        }

        return NifModelTransformChannelResult.Mapped(
            new NifModelTransformChannel(property, state.State, state.StaticValue, null, spline.Spline));
    }
}
