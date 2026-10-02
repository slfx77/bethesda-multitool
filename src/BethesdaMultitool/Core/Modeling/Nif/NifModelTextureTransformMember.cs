using System.Globalization;
using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 14: whether one animated NiTextureTransform member (an NiTextureTransformController Operation, or an
///     NiUVData group) maps onto exactly one member of the Shared <see cref="SceneTextureTransform" /> the cut-1a reader
///     composed for the map's rest transform (<see cref="NifModelTextureTransform" />: scale, then counterclockwise
///     rotation, then offset), so that replacing that Shared member with the sampled value is what the engine draws.
///     Pure; fail closed.
/// </summary>
/// <remarks>
///     <para>
///         The engine rebuilds the map's UV matrix from the NIF members under the map's Transform Method every frame
///         (nif.xml TransformMethod products on column UV vectors: 0 Maya-deprecated C R B T S, 1 Max C S R T B, 2 Maya
///         C R B F T S; C = +Center, B = -Center, F: v to 1 - v). A member maps exactly when, with the other members at
///         their rest values, the Shared member is that member itself and no other Shared member moves with it:
///     </para>
///     <list type="bullet">
///         <item>
///             Method 0 (offset O = R(T - C) + C, scale K = S, rotation R): TRANSLATE_U and TRANSLATE_V need R = 0 (the
///             center then cancels); SCALE_U and SCALE_V always map; ROTATE needs T = C.
///         </item>
///         <item>
///             Method 1 (O = S R (T - C) + C, K = S, rotation R): TRANSLATE_U needs R = 0 and S.x = 1, TRANSLATE_V needs
///             R = 0 and S.y = 1; SCALE_U needs R = 0 and T.x = C.x, SCALE_V needs R = 0 and T.y = C.y; ROTATE needs
///             S.x = S.y and T = C.
///         </item>
///         <item>
///             Method 2 (O = R((T.x, 1 - T.y) - C) + C, K = (S.x, -S.y), rotation R): TRANSLATE_U needs R = 0; SCALE_U
///             always maps; TRANSLATE_V and SCALE_V never map (the V member enters as 1 - T.y and -S.y); ROTATE needs
///             T.x = C.x and 1 - T.y = C.y.
///         </item>
///     </list>
///     <para>
///         Beside the algebra, the cut-1a decomposition of the rest transform must have taken the branch the algebra
///         assumes: a rotation of exactly 0 for a translate or scale member (a negative rest scale decomposes as a
///         rotation by pi and negated scales, where a sampled scale would land with the wrong sign), and for ROTATE a
///         rotation bit-equal to the stored one with the scale equal to the stored scale. A map that stores no transform
///         is refused: the engine's default Transform Method is not established here. Every comparison is exact.
///     </para>
/// </remarks>
internal static class NifModelTextureTransformMember
{
    /// <summary>The reason a map without a stored transform is refused.</summary>
    public const string NoTransformReason =
        "map stores no texture transform: the engine's default Transform Method is not established";

    private const string RotationNotZero = "the rest rotation is not 0";
    private const string DecompositionRotates = "the rest decomposition carries a rotation";
    private const string TranslationNotCenter = "the rest translation differs from the center";

    /// <summary>Decides one member for one map.</summary>
    /// <param name="map">The cut-1a view of the NiTexturingProperty map the controller drives.</param>
    /// <param name="operation">The nif.xml TransformMember (0 TRANSLATE_U, 1 TRANSLATE_V, 2 ROTATE, 3 SCALE_U, 4 SCALE_V).</param>
    /// <param name="kind">The Shared kind the member maps to.</param>
    /// <param name="reason">Why it does not map; null when it does.</param>
    /// <returns>True when the member maps onto exactly one Shared member.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The operation is outside the table.</exception>
    public static bool TryMap(NifTextureMapView map, uint operation, out ScenePropertyKind kind, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!NifModelPropertyController.TryOperationKind(operation, out kind))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not a nif.xml TransformMember.");
        }

        reason = null;
        if (!map.HasTransform)
        {
            reason = NoTransformReason;
            return false;
        }

        if (!NifModelTextureTransform.TryCompose(map.TransformMethod, map.Translation, map.Scale, map.Rotation,
                map.Center, out var composed, out _))
        {
            reason = Refusal(map, operation, "the rest transform has no Shared equivalent");
            return false;
        }

        var failure = map.TransformMethod switch
        {
            0 => MayaDeprecated(map, composed, operation),
            1 => Max(map, composed, operation),
            2 => Maya(map, composed, operation),
            _ => "the Transform Method is not one nif.xml defines"
        };
        if (failure is null)
        {
            return true;
        }

        reason = Refusal(map, operation, failure);
        return false;
    }

    /// <summary>Method 0: C R B T S.</summary>
    private static string? MayaDeprecated(NifTextureMapView map, SceneTextureTransform composed, uint operation)
    {
        return operation switch
        {
            0 or 1 => IsZero(map.Rotation) && IsZero(composed.Rotation) ? null : RotationNotZero,
            3 or 4 => IsZero(composed.Rotation) ? null : DecompositionRotates,
            _ => Rotate(map, composed, map.Translation.Equals(map.Center) ? null : TranslationNotCenter)
        };
    }

    /// <summary>Method 1: C S R T B.</summary>
    private static string? Max(NifTextureMapView map, SceneTextureTransform composed, uint operation)
    {
        var t = map.Translation;
        var s = map.Scale;
        var c = map.Center;
        if (operation == 2)
        {
            return Rotate(map, composed,
                !Same(s.X, s.Y) ? "the rest scale is not uniform" : t.Equals(c) ? null : TranslationNotCenter);
        }

        if (!IsZero(map.Rotation) || !IsZero(composed.Rotation))
        {
            return RotationNotZero;
        }

        return operation switch
        {
            0 => IsOne(s.X) ? null : "the rest U scale is not 1",
            1 => IsOne(s.Y) ? null : "the rest V scale is not 1",
            3 => Same(t.X, c.X) ? null : "the rest U translation differs from the center",
            _ => Same(t.Y, c.Y) ? null : "the rest V translation differs from the center"
        };
    }

    /// <summary>Method 2: C R B F T S.</summary>
    private static string? Maya(NifTextureMapView map, SceneTextureTransform composed, uint operation)
    {
        var t = map.Translation;
        var c = map.Center;
        return operation switch
        {
            0 => IsZero(map.Rotation) && IsZero(composed.Rotation) ? null : RotationNotZero,
            1 => "the Maya method flips V: the Shared offset is 1 - T.y, not the member",
            3 => IsZero(composed.Rotation) ? null : DecompositionRotates,
            4 => "the Maya method flips V: the Shared scale is -S.y, not the member",
            _ => Rotate(map, composed, Same(t.X, c.X) && Same(1f - t.Y, c.Y) ? null : TranslationNotCenter)
        };
    }

    /// <summary>ROTATE's shared checks: the algebraic condition, then the rest decomposition equal to the stored rotation and scale.</summary>
    private static string? Rotate(NifTextureMapView map, SceneTextureTransform composed, string? algebraic)
    {
        if (algebraic is not null)
        {
            return algebraic;
        }

        if (BitConverter.SingleToUInt32Bits(composed.Rotation) != BitConverter.SingleToUInt32Bits(map.Rotation))
        {
            return "the rest decomposition's rotation is not the stored rotation";
        }

        return composed.Scale.Equals(map.Scale) ? null : "the rest decomposition's scale is not the stored scale";
    }

    private static bool IsZero(float value)
    {
        return value.Equals(0f);
    }

    private static bool IsOne(float value)
    {
        return value.Equals(1f);
    }

    private static bool Same(float first, float second)
    {
        return first.Equals(second);
    }

    private static string Refusal(NifTextureMapView map, uint operation, string failure)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"texture transform member {NifModelPropertyController.OperationName(operation)} does not map to one " +
            $"Shared member under the map's rest transform (method {map.TransformMethod}): {failure}");
    }
}
