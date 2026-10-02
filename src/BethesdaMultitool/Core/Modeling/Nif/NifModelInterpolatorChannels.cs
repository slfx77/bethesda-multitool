using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The three transform channels (translation, rotation, scale) of one interpolator under a transform binding, each
///     mapped by slice 2 (<see cref="NifModelTransformChannelMapping" />), or the reason the whole interpolator stays
///     native. A mapped channel may still be blocked on its own (<see cref="NifModelTransformChannelResult.IsBlocked" />);
///     the others stay typed.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             NiTransformInterpolator: its NiTransformData or NiKeyframeData, which must read and end exactly. Its
///             rotation channel is a quaternion channel, or since slice 13 an Euler rotation
///             (<see cref="NifModelTransformChannelResult.Euler" />), or since slice 16b a Squad curve under the file's
///             platform policy (<see cref="NifModelSquadPolicy" />).
///         </item>
///         <item>
///             NiBSplineTransformInterpolator and NiBSplineCompTransformInterpolator: their NiBSplineData (read exactly)
///             and NiBSplineBasisData; an unresolved ref reaches the B-spline mapping as missing data, which blocks only a
///             channel that has a handle.
///         </item>
///         <item>
///             BSRotAccumTransfInterpolator, BSTreadTransfInterpolator, NiPathInterpolator, NiLookAtInterpolator, the
///             NiBlend*Interpolators and every other type stay native with the plan section 2.1 reasons.
///         </item>
///     </list>
/// </remarks>
internal sealed class NifModelInterpolatorChannels
{
    /// <summary>The channels in the order <see cref="Channels" /> holds them.</summary>
    public static readonly IReadOnlyList<SceneTransformProperty> Properties =
        Array.AsReadOnly(new[] { SceneTransformProperty.Translation, SceneTransformProperty.Rotation, SceneTransformProperty.Scale });

    private const string TransformInterpolatorType = "NiTransformInterpolator";
    private const string BlendInterpolatorType = "NiBlendInterpolator";
    private const int NoRef = -1;

    private NifModelInterpolatorChannels(
        int interpolator,
        IReadOnlyList<int> blocks,
        IReadOnlyList<NifModelTransformChannelResult> channels,
        bool hasCurve,
        string? nativeReason,
        string? nativeCode)
    {
        Interpolator = interpolator;
        Blocks = blocks;
        Channels = channels;
        HasCurve = hasCurve;
        NativeReason = nativeReason;
        NativeCode = nativeCode;
    }

    /// <summary>The interpolator block.</summary>
    public int Interpolator { get; }

    /// <summary>The interpolator and the key-data blocks it resolved, in that order; each decision covers all of them.</summary>
    public IReadOnlyList<int> Blocks { get; }

    /// <summary>Translation, rotation and scale, in <see cref="Properties" /> order; empty when the whole interpolator is native.</summary>
    public IReadOnlyList<NifModelTransformChannelResult> Channels { get; }

    /// <summary>
    ///     True when the interpolator stores keys or a B-spline handle on any channel (mapped or blocked), which RE-22 rule
    ///     3a needs for the double sentinel clock.
    /// </summary>
    public bool HasCurve { get; }

    /// <summary>Why the whole interpolator stays native; null when its channels were mapped.</summary>
    public string? NativeReason { get; }

    /// <summary>The code of <see cref="NativeReason" />; null when its channels were mapped.</summary>
    public string? NativeCode { get; }

    /// <summary>True when the whole interpolator stays native.</summary>
    public bool IsNative => NativeReason is not null;

    /// <summary>Maps the three channels of one interpolator block.</summary>
    /// <param name="source">The file's views.</param>
    /// <param name="interpolator">A block index of the file.</param>
    /// <param name="squad">The file's Squad policy (<see cref="NifModelSquadPolicy.Resolve" />), for TBC and QUADRATIC quaternions.</param>
    /// <returns>The mapped channels, or the reason the interpolator stays native.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index names no block.</exception>
    /// <exception cref="InvalidDataException">
    ///     A static value is malformed (<see cref="NifModelChannelStateMapping" />: a sentinel mixed with authored components,
    ///     a non-finite component, or a zero rotation). The plan keeps BMT's rule and throws; retail never does.
    /// </exception>
    public static NifModelInterpolatorChannels Map(NifModelAnimationSource source, int interpolator,
        in NifModelSquadPolicy squad)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsBlock(interpolator))
        {
            throw new ArgumentOutOfRangeException(nameof(interpolator), interpolator, "The ref names no block.");
        }

        var type = source.TypeOf(interpolator);
        if (string.Equals(type, TransformInterpolatorType, StringComparison.Ordinal))
        {
            return MapKeyframe(source, interpolator, squad);
        }

        if (source.IsBsplineTransform(interpolator))
        {
            return MapBspline(source, interpolator);
        }

        var (reason, code) = type switch
        {
            "BSRotAccumTransfInterpolator" =>
                (NifModelAnimationReasons.RotationAccumulation, NifModelAnimationReasons.RotationAccumulationCode),
            "BSTreadTransfInterpolator" =>
                (NifModelAnimationReasons.TreadTransform, NifModelAnimationReasons.TreadTransformCode),
            "NiPathInterpolator" or "NiLookAtInterpolator" =>
                (NifModelAnimationReasons.PathLookAt, NifModelAnimationReasons.PathLookAtCode),
            _ when source.Inherits(interpolator, BlendInterpolatorType) =>
                (NifModelAnimationReasons.BlendState, NifModelAnimationReasons.BlendStateCode),
            _ => (NifModelAnimationReasons.NotTransformInterpolator,
                NifModelAnimationReasons.NotTransformInterpolatorCode)
        };
        return Native(interpolator, source.InterpolatorBlocks(interpolator), reason, code);
    }

    private static NifModelInterpolatorChannels MapKeyframe(NifModelAnimationSource source, int interpolator,
        in NifModelSquadPolicy squad)
    {
        List<int> blocks = [interpolator];
        if (!source.TryReadTransformInterpolator(interpolator, out var view))
        {
            return Native(interpolator, blocks, NifModelAnimationReasons.InterpolatorUnreadable,
                NifModelAnimationReasons.InterpolatorUnreadableCode);
        }

        NifKeyframeDataView? data = null;
        if (view.DataRef != NoRef)
        {
            if (!source.IsBlock(view.DataRef))
            {
                return Native(interpolator, blocks, NifModelAnimationReasons.DataUnresolved,
                    NifModelAnimationReasons.DataUnresolvedCode);
            }

            blocks.Add(view.DataRef);
            if (!NifKeyframeDataTrackReader.IsTrackDataBlock(source.TypeOf(view.DataRef)))
            {
                return Native(interpolator, blocks, NifModelAnimationReasons.DataUnresolved,
                    NifModelAnimationReasons.DataUnresolvedCode);
            }

            if (!source.TryReadKeyframeData(view.DataRef, out var keyframes) || !keyframes.ConsumedExactly)
            {
                return Native(interpolator, blocks, NifModelAnimationReasons.DataUnreadable,
                    NifModelAnimationReasons.DataUnreadableCode);
            }

            data = keyframes;
        }

        var channels = new NifModelTransformChannelResult[Properties.Count];
        for (var i = 0; i < channels.Length; i++)
        {
            channels[i] = NifModelTransformChannelMapping.MapKeyframe(view, data, Properties[i], squad);
        }

        var hasCurve = data is { } stored &&
                       (stored.Rotation.StoredKeyCount > 0 || stored.Translations.Count > 0 || stored.Scales.Count > 0);
        return new NifModelInterpolatorChannels(interpolator, blocks, channels, hasCurve, null, null);
    }

    private static NifModelInterpolatorChannels MapBspline(NifModelAnimationSource source, int interpolator)
    {
        List<int> blocks = [interpolator];
        if (!source.TryReadBsplineTransform(interpolator, out var view))
        {
            return Native(interpolator, blocks, NifModelAnimationReasons.InterpolatorUnreadable,
                NifModelAnimationReasons.InterpolatorUnreadableCode);
        }

        NifBsplineDataView? data = null;
        if (view.SplineDataRef != NoRef)
        {
            if (!source.IsBlock(view.SplineDataRef))
            {
                return Native(interpolator, blocks, NifModelAnimationReasons.DataUnresolved,
                    NifModelAnimationReasons.DataUnresolvedCode);
            }

            blocks.Add(view.SplineDataRef);
            if (!source.TryReadBsplineData(view.SplineDataRef, out var spline) || !spline.ConsumedExactly)
            {
                return Native(interpolator, blocks, NifModelAnimationReasons.DataUnreadable,
                    NifModelAnimationReasons.DataUnreadableCode);
            }

            data = spline;
        }

        uint? count = null;
        if (view.BasisDataRef != NoRef)
        {
            if (!source.IsBlock(view.BasisDataRef))
            {
                return Native(interpolator, blocks, NifModelAnimationReasons.DataUnresolved,
                    NifModelAnimationReasons.DataUnresolvedCode);
            }

            if (!blocks.Contains(view.BasisDataRef))
            {
                blocks.Add(view.BasisDataRef);
            }

            if (!source.TryReadBsplineBasis(view.BasisDataRef, out var basis))
            {
                return Native(interpolator, blocks, NifModelAnimationReasons.DataUnreadable,
                    NifModelAnimationReasons.DataUnreadableCode);
            }

            count = basis;
        }

        var channels = new NifModelTransformChannelResult[Properties.Count];
        for (var i = 0; i < channels.Length; i++)
        {
            channels[i] = NifModelTransformChannelMapping.MapBspline(view, Properties[i], data, count);
        }

        var hasCurve = Array.Exists(view.Handles, static handle => handle != NifModelBsplineMapping.AbsentHandle);
        return new NifModelInterpolatorChannels(interpolator, blocks, channels, hasCurve, null, null);
    }

    private static NifModelInterpolatorChannels Native(int interpolator, IReadOnlyList<int> blocks, string reason,
        string code)
    {
        return new NifModelInterpolatorChannels(interpolator, blocks, [], false, reason, code);
    }
}
