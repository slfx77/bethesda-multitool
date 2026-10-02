using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 2, plan section 1.3: a transform channel's Shared state from whether it has keys and from its static
///     value (NiTransformInterpolator, BSRotAccumTransfInterpolator and the B-spline transform interpolators store one
///     NiQuatTransform). Pure.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>Keys present (a non-empty key group, or a B-spline handle other than 0xFFFF): Keyed, with a valid static value kept as the fallback.</item>
///         <item>No keys and a valid static value: Constant.</item>
///         <item>No keys and every static component equal to nif.xml's #INV_FLT# (-FLT_MAX, bits 0xFF7FFFFF): NotDriven.</item>
///         <item>Some static components #INV_FLT# and some not: <see cref="InvalidDataException" /> (expected never).</item>
///         <item>A valid static rotation that is the zero quaternion: <see cref="InvalidDataException" /> (Shared rejects it).</item>
///         <item>
///             A static component that is not #INV_FLT# and not finite: <see cref="InvalidDataException" />, because Shared
///             cannot hold it and the sentinel test is a bit test (a NaN is not #INV_FLT#).
///         </item>
///     </list>
///     <para>
///         Static rotations are permuted from the file's W, X, Y, Z to Shared's X, Y, Z, W without normalization (Shared
///         normalizes a sampled rotation); a static scale s becomes (s, s, s).
///     </para>
/// </remarks>
internal static class NifModelChannelStateMapping
{
    /// <summary>nif.xml's #INV_FLT#, -FLT_MAX: the static value of a channel the interpolator does not drive.</summary>
    public const uint InvalidFloatBits = 0xFF7FFFFF;

    /// <summary>Classifies one channel from its static value's raw bits in file order.</summary>
    /// <param name="property">The channel.</param>
    /// <param name="fileOrderBits">Translation x, y, z; rotation w, x, y, z; or the one scale word.</param>
    /// <param name="hasKeys">True when the channel has keys (or a B-spline handle).</param>
    /// <returns>The state and the static value in Shared's layout.</returns>
    /// <exception cref="ArgumentException">The bit count does not match the property.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The property is undefined.</exception>
    /// <exception cref="InvalidDataException">
    ///     The static value mixes #INV_FLT# with other components, holds a non-finite component, or is a zero rotation.
    /// </exception>
    public static NifModelChannelState Map(SceneTransformProperty property, ReadOnlySpan<uint> fileOrderBits, bool hasKeys)
    {
        var expected = property switch
        {
            SceneTransformProperty.Translation => 3,
            SceneTransformProperty.Rotation => 4,
            SceneTransformProperty.Scale => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown transform property.")
        };
        if (fileOrderBits.Length != expected)
        {
            throw new ArgumentException(
                $"A {property} static value stores {expected} words, not {fileOrderBits.Length}.", nameof(fileOrderBits));
        }

        var sentinels = 0;
        foreach (var bits in fileOrderBits)
        {
            if (bits == InvalidFloatBits)
            {
                sentinels++;
            }
        }

        if (sentinels == expected)
        {
            return hasKeys
                ? new NifModelChannelState(SceneAnimationChannelState.Keyed, null)
                : new NifModelChannelState(SceneAnimationChannelState.NotDriven, null);
        }

        if (sentinels != 0)
        {
            throw new InvalidDataException(
                $"The {property} static value mixes the #INV_FLT# sentinel with authored components " +
                $"({sentinels} of {expected}).");
        }

        var value = property switch
        {
            SceneTransformProperty.Translation => new[]
            {
                Float(fileOrderBits[0]), Float(fileOrderBits[1]), Float(fileOrderBits[2])
            },
            SceneTransformProperty.Rotation => new[]
            {
                Float(fileOrderBits[1]), Float(fileOrderBits[2]), Float(fileOrderBits[3]), Float(fileOrderBits[0])
            },
            _ => new[] { Float(fileOrderBits[0]), Float(fileOrderBits[0]), Float(fileOrderBits[0]) }
        };
        var squared = 0d;
        foreach (var component in value)
        {
            if (!float.IsFinite(component))
            {
                throw new InvalidDataException($"The {property} static value holds a non-finite component.");
            }

            squared += (double)component * component;
        }

        if (property == SceneTransformProperty.Rotation && squared == 0d)
        {
            throw new InvalidDataException("The static rotation is the zero quaternion.");
        }

        return new NifModelChannelState(
            hasKeys ? SceneAnimationChannelState.Keyed : SceneAnimationChannelState.Constant, value);
    }

    /// <summary>Classifies one channel of an NiTransformInterpolator (or BSRotAccumTransfInterpolator).</summary>
    /// <param name="interpolator">The interpolator view.</param>
    /// <param name="property">The channel.</param>
    /// <param name="hasKeys">True when the channel's key group in the interpolator's data is non-empty.</param>
    /// <returns>The state and the static value in Shared's layout.</returns>
    /// <exception cref="InvalidDataException">See <see cref="Map" />.</exception>
    public static NifModelChannelState FromTransformInterpolator(
        NifTransformInterpolatorView interpolator, SceneTransformProperty property, bool hasKeys)
    {
        Span<uint> bits = stackalloc uint[4];
        int count;
        switch (property)
        {
            case SceneTransformProperty.Translation:
                bits[0] = interpolator.TranslationXBits;
                bits[1] = interpolator.TranslationYBits;
                bits[2] = interpolator.TranslationZBits;
                count = 3;
                break;
            case SceneTransformProperty.Rotation:
                bits[0] = interpolator.RotationWBits;
                bits[1] = interpolator.RotationXBits;
                bits[2] = interpolator.RotationYBits;
                bits[3] = interpolator.RotationZBits;
                count = 4;
                break;
            case SceneTransformProperty.Scale:
                bits[0] = interpolator.ScaleBits;
                count = 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown transform property.");
        }

        return Map(property, bits[..count], hasKeys);
    }

    /// <summary>
    ///     Classifies one channel of a transform B-spline interpolator: keyed when its handle is not 0xFFFF, with the
    ///     static NiQuatTransform (translation x, y, z, rotation w, x, y, z, scale) as the static value.
    /// </summary>
    /// <param name="interpolator">A transform B-spline interpolator view.</param>
    /// <param name="property">The channel.</param>
    /// <returns>The state and the static value in Shared's layout.</returns>
    /// <exception cref="ArgumentException">The view is not a transform B-spline interpolator.</exception>
    /// <exception cref="InvalidDataException">See <see cref="Map" />.</exception>
    public static NifModelChannelState FromBsplineInterpolator(
        NifBsplineInterpolatorView interpolator, SceneTransformProperty property)
    {
        ArgumentNullException.ThrowIfNull(interpolator);
        if (interpolator.Kind != NifBsplineInterpolatorKind.Transform || interpolator.StaticValueBits.Length != 8 ||
            interpolator.Handles.Length != 3)
        {
            throw new ArgumentException(
                $"Only transform B-spline interpolators carry transform channels, not {interpolator.TypeName}.",
                nameof(interpolator));
        }

        var (channel, first, count) = property switch
        {
            SceneTransformProperty.Translation => (0, 0, 3),
            SceneTransformProperty.Rotation => (1, 3, 4),
            SceneTransformProperty.Scale => (2, 7, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown transform property.")
        };
        var bits = new ReadOnlySpan<uint>(interpolator.StaticValueBits, first, count);
        return Map(property, bits, interpolator.Handles[channel] != NifModelBsplineMapping.AbsentHandle);
    }

    /// <summary>A float built from its stored bits.</summary>
    /// <param name="bits">The raw bits.</param>
    /// <returns>The float.</returns>
    private static float Float(uint bits)
    {
        return BitConverter.UInt32BitsToSingle(bits);
    }
}
