using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 2: maps the lossless key-group views of slice 1 (<see cref="NifKeyGroupView" />,
///     <see cref="NifRotationKeysView" />) onto Shared transform curves (plan sections 1.2 and 1.3, with the RE results of
///     docs/formats/nif-animation-engine-behavior-20260925.md). Pure and synchronous; it allocates only the owned output
///     arrays.
/// </summary>
/// <remarks>
///     <para>Component groups (float and Vector3):</para>
///     <list type="bullet">
///         <item>LINEAR (1) maps to <see cref="SceneInterpolation.Linear" />, CONST (5) to <see cref="SceneInterpolation.Step" />.</item>
///         <item>
///             QUADRATIC (2) maps to <see cref="SceneInterpolation.Hermite" /> with each key's [Forward, Value, Backward] as
///             Shared's [incoming, value, outgoing], exactly as stored: no interval scaling, no exchange, signed zeros kept
///             (RE-18).
///         </item>
///         <item>
///             TBC (3) maps to <see cref="SceneInterpolation.Tbc" /> with <c>SceneTbcParameters(tension = float 0,
///             continuity = float 1, bias = float 2)</c> in file order (the TBC order rule; nif.xml's t, b, c labels are
///             wrong for the second and third floats) and the RE-19 endpoints of <see cref="NifModelTbcEndpoints" />.
///         </item>
///         <item>A scale float s becomes (s, s, s): values, tangents and endpoints are all replicated.</item>
///     </list>
///     <para>Quaternion rotation groups:</para>
///     <list type="bullet">
///         <item>
///             LINEAR (1) maps to <see cref="SceneInterpolation.GamebryoCounterWarpedNlerp" /> with the keys of
///             <see cref="NifRotationKeyEngineRule" /> (RE-17: chain sign alignment, W clamp, exact normalization),
///             permuted from the file's W, X, Y, Z to Shared's X, Y, Z, W with no conjugation.
///         </item>
///         <item>CONST (5) maps to <see cref="SceneInterpolation.Step" /> with the same RE-17 keys (RE-17 rule step 4).</item>
///         <item>
///             TBC (3) and QUADRATIC (2) map to <see cref="SceneInterpolation.GamebryoSquad" /> (slice 16b, RE-24):
///             RE-17 steps 1 and 2 in place with no normalization, then the engine's Float32 inner points
///             (<see cref="NifModelSquadInnerPoints" />) as incoming, key, outgoing triples permuted to X, Y, Z, W, under
///             the file's platform policy (<see cref="NifModelSquadPolicy" />: PC, X360, or refused for PS3).
///         </item>
///         <item>
///             XYZ_ROTATION (4) maps through <see cref="MapEulerRotation" /> (slice 13, RE-20) to three scalar axes, not
///             to a quaternion curve; any other key type is blocked (fail closed).
///         </item>
///     </list>
///     <para>
///         A group whose keys Shared could not hold (a non-finite or non-increasing time, a non-finite value, tangent or
///         parameter) is blocked rather than repaired.
///     </para>
/// </remarks>
internal static class NifModelCurveMapping
{
    /// <summary>How many times a NIF scale float is repeated to form Shared's three-component scale.</summary>
    public const int ScaleReplication = 3;

    /// <summary>The Shared width of a rotation value (X, Y, Z, W).</summary>
    public const int RotationComponentCount = 4;

    /// <summary>The values one Squad key holds: the incoming inner point, the key and the outgoing inner point.</summary>
    public const int SquadStride = 3 * RotationComponentCount;

    /// <summary>Maps a translation key group (Vector3) of NiTransformData or NiKeyframeData.</summary>
    /// <param name="group">The translation group.</param>
    /// <returns>The curve, a blocked reason, or <see cref="NifModelCurveResult.NoKeys" /> for an empty group.</returns>
    /// <exception cref="ArgumentException">The group is not a Vector3 group.</exception>
    public static NifModelCurveResult MapTranslation(in NifKeyGroupView group)
    {
        RequireLayout(group, NifKeyValueLayout.Vector3);
        return MapComponents(group, 1);
    }

    /// <summary>Maps a scale key group (float) of NiTransformData or NiKeyframeData, replicating s to (s, s, s).</summary>
    /// <param name="group">The scale group.</param>
    /// <returns>The curve, a blocked reason, or <see cref="NifModelCurveResult.NoKeys" /> for an empty group.</returns>
    /// <exception cref="ArgumentException">The group is not a float group.</exception>
    public static NifModelCurveResult MapScale(in NifKeyGroupView group)
    {
        RequireLayout(group, NifKeyValueLayout.Float);
        return MapComponents(group, ScaleReplication);
    }

    /// <summary>
    ///     Maps a float or Vector3 key group, each stored component repeated <paramref name="replication" /> times in the
    ///     Shared vector (component j of the output reads stored component j / replication).
    /// </summary>
    /// <param name="group">The float or Vector3 group.</param>
    /// <param name="replication">How many times each stored component is repeated; 1 keeps the stored width.</param>
    /// <returns>The curve, a blocked reason, or <see cref="NifModelCurveResult.NoKeys" /> for an empty group.</returns>
    /// <exception cref="ArgumentException">The group is neither a float nor a Vector3 group.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The replication is not positive.</exception>
    public static NifModelCurveResult MapComponents(in NifKeyGroupView group, int replication = 1)
    {
        if (group.Layout is not (NifKeyValueLayout.Float or NifKeyValueLayout.Vector3))
        {
            throw new ArgumentException(
                $"Only float and Vector3 key groups map to component curves, not {group.Layout}.", nameof(group));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replication);
        if (group.Count == 0)
        {
            return NifModelCurveResult.NoKeys;
        }

        SceneInterpolation interpolation;
        switch (group.KeyType)
        {
            case (uint)NifKeyInterpolation.Linear:
                interpolation = SceneInterpolation.Linear;
                break;
            case (uint)NifKeyInterpolation.Constant:
                interpolation = SceneInterpolation.Step;
                break;
            case (uint)NifKeyInterpolation.Quadratic:
                interpolation = SceneInterpolation.Hermite;
                break;
            case (uint)NifKeyInterpolation.Tbc:
                interpolation = SceneInterpolation.Tbc;
                break;
            default:
                return NifModelCurveResult.Blocked(NifModelCurveBlock.UnknownKeyType);
        }

        if (!TryReadTimes(group, out var times))
        {
            return NifModelCurveResult.Blocked(NifModelCurveBlock.InvalidKeyTimes);
        }

        var width = group.ComponentCount * replication;
        return interpolation switch
        {
            SceneInterpolation.Hermite => MapHermite(group, times, width, replication),
            SceneInterpolation.Tbc => MapTbc(group, times, width, replication),
            _ => MapPlain(group, times, width, replication, interpolation)
        };
    }

    /// <summary>
    ///     Maps the quaternion rotation part of NiTransformData or NiKeyframeData: LINEAR and CONST through RE-17, TBC
    ///     and QUADRATIC through the Squad form (RE-24) under the file's platform policy. An XYZ_ROTATION part is not a
    ///     quaternion curve and maps through <see cref="MapEulerRotation" />.
    /// </summary>
    /// <param name="rotation">The rotation view.</param>
    /// <param name="squad">The file's Squad policy (<see cref="NifModelSquadPolicy.Resolve" />), consulted for TBC and QUADRATIC only.</param>
    /// <returns>The curve, a blocked reason, or <see cref="NifModelCurveResult.NoKeys" /> when no rotation key is stored.</returns>
    /// <exception cref="ArgumentException">
    ///     The view is an XYZ_ROTATION rotation, its quaternion keys disagree with its stored count or type, or the Squad
    ///     policy is the unresolved default.
    /// </exception>
    public static NifModelCurveResult MapRotation(in NifRotationKeysView rotation, in NifModelSquadPolicy squad)
    {
        if (rotation.StoredKeyCount == 0)
        {
            return NifModelCurveResult.NoKeys;
        }

        SceneInterpolation interpolation;
        switch (rotation.KeyType)
        {
            case (uint)NifKeyInterpolation.Linear:
                interpolation = SceneInterpolation.GamebryoCounterWarpedNlerp;
                break;
            case (uint)NifKeyInterpolation.Constant:
                interpolation = SceneInterpolation.Step;
                break;
            case (uint)NifKeyInterpolation.Tbc:
            case (uint)NifKeyInterpolation.Quadratic:
                return MapSquad(rotation, squad);
            case (uint)NifKeyInterpolation.XyzEuler:
                throw new ArgumentException(
                    "An XYZ_ROTATION rotation is three scalar axes, not a quaternion curve: use MapEulerRotation.",
                    nameof(rotation));
            default:
                return NifModelCurveResult.Blocked(NifModelCurveBlock.UnknownKeyType);
        }

        var keys = QuaternionKeys(rotation);
        if (!TryReadTimes(keys, out var times))
        {
            return NifModelCurveResult.Blocked(NifModelCurveBlock.InvalidKeyTimes);
        }

        if (!NifRotationKeyEngineRule.TryApply(keys, out var engine))
        {
            throw new InvalidOperationException("RE-17 governs every non-empty LINEAR and CONST quaternion group.");
        }

        var values = new float[times.Length * RotationComponentCount];
        for (var key = 0; key < times.Length; key++)
        {
            var value = engine.Values[key];
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) ||
                !float.IsFinite(value.W))
            {
                return NifModelCurveResult.Blocked(NifModelCurveBlock.NonFiniteValue);
            }

            if (!IsSharedUnitRotation(value))
            {
                return NifModelCurveResult.Blocked(NifModelCurveBlock.NonUnitRotation);
            }

            // RE-17 already returns System.Numerics X, Y, Z, W: the file's W, X, Y, Z permuted, never conjugated.
            var offset = key * RotationComponentCount;
            values[offset] = value.X;
            values[offset + 1] = value.Y;
            values[offset + 2] = value.Z;
            values[offset + 3] = value.W;
        }

        return NifModelCurveResult.Keyed(new NifModelCurve(
            rotation.KeyType, RotationComponentCount, times, values, interpolation, null, null));
    }

    /// <summary>
    ///     RE-20 (slice 13): maps an XYZ_ROTATION (type 4) rotation part to its three axes. Record 0 only (slice 1 walked
    ///     every record so the block stays byte-exact; a stored count other than 1 fails closed, rule 2). Each axis is a
    ///     scalar KeyGroup through <see cref="MapComponents" /> at width 1 with its own key type (RE-18 Hermite and RE-19
    ///     TBC included); an empty axis is angle 0 (rule 3); an axis key type outside LINEAR, QUADRATIC, TBC and CONST
    ///     fails closed (rule 2). Angles are radians as stored (rule 8); the composition order is
    ///     <see cref="NifModelEulerRotation.Order" /> (rule 9). The interpolator's static rotation is the caller's
    ///     (native state only).
    /// </summary>
    /// <param name="rotation">The rotation view.</param>
    /// <returns>The axes, or a blocked reason.</returns>
    /// <exception cref="ArgumentException">The view is not an XYZ_ROTATION rotation.</exception>
    public static NifModelEulerResult MapEulerRotation(in NifRotationKeysView rotation)
    {
        if (!rotation.IsEuler)
        {
            throw new ArgumentException("Only an XYZ_ROTATION (type 4) rotation maps to Euler axes.", nameof(rotation));
        }

        if (rotation.StoredKeyCount != 1)
        {
            return NifModelEulerResult.Blocked(NifModelCurveBlock.EulerRecordCount);
        }

        if (!TryMapEulerAxis(rotation.EulerX, out var x, out var block) ||
            !TryMapEulerAxis(rotation.EulerY, out var y, out block) ||
            !TryMapEulerAxis(rotation.EulerZ, out var z, out block))
        {
            return NifModelEulerResult.Blocked(block);
        }

        return NifModelEulerResult.Mapped(new NifModelEulerRotation(x, y, z, null));
    }

    /// <summary>One Euler axis: empty (angle 0), a scalar curve, or the reason it is refused.</summary>
    /// <param name="axis">The axis's float key group.</param>
    /// <param name="mapped">The axis.</param>
    /// <param name="block">The refusal; <see cref="NifModelCurveBlock.None" /> when the axis mapped.</param>
    /// <returns>True when the axis mapped.</returns>
    /// <exception cref="ArgumentException">The group is not a float group.</exception>
    private static bool TryMapEulerAxis(in NifKeyGroupView axis, out NifModelEulerAxis mapped,
        out NifModelCurveBlock block)
    {
        block = NifModelCurveBlock.None;
        mapped = NifModelEulerAxis.Empty;
        if (axis.Layout != NifKeyValueLayout.Float)
        {
            throw new ArgumentException($"An Euler axis is a float key group, not a {axis.Layout} group.", nameof(axis));
        }

        if (axis.Count == 0)
        {
            return true;
        }

        if (axis.KeyType is not ((uint)NifKeyInterpolation.Linear or (uint)NifKeyInterpolation.Quadratic or
            (uint)NifKeyInterpolation.Tbc or (uint)NifKeyInterpolation.Constant))
        {
            block = NifModelCurveBlock.EulerAxisKeyType;
            return false;
        }

        var result = MapComponents(axis, 1);
        if (result.Curve is not { } curve)
        {
            block = result.Block;
            return false;
        }

        mapped = new NifModelEulerAxis(axis.KeyType, curve);
        return true;
    }

    /// <summary>
    ///     RE-24 (slice 16b): a TBC (3) or QUADRATIC (2) quaternion group onto Shared's Squad form under the file's
    ///     platform policy. Times must be finite; a repeated time is the engine's zero-length span (its inner point is
    ///     NaN) and is refused as such, a decreasing one as an ordinary key-time refusal. Two or more keys: RE-17 steps
    ///     1 and 2 in place and NO normalization (rule step 1), then the engine's inner points
    ///     (<see cref="NifModelSquadInnerPoints" />: TBC gives Out[i] and In[i], QUADRATIC gives s[i] used as both).
    ///     One key: the raw key, which Shared holds, stands as its own inner points, because the engine's fill does not
    ///     run (rule step 3). Each key's triple is written incoming, key, outgoing, each permuted from W, X, Y, Z to
    ///     X, Y, Z, W with no conjugation and no sign change beyond the chain alignment. A non-finite or zero key or
    ///     inner point is refused. The stored T, C and B enter only the inner points: Shared refuses TBC parameters on
    ///     a Squad track, and the raw floats stay in the block's native row.
    /// </summary>
    /// <param name="rotation">A TBC or QUADRATIC rotation view.</param>
    /// <param name="squad">The file's Squad policy.</param>
    /// <returns>The Squad curve or the refusal.</returns>
    /// <exception cref="ArgumentException">The Squad policy is the unresolved default.</exception>
    private static NifModelCurveResult MapSquad(in NifRotationKeysView rotation, in NifModelSquadPolicy squad)
    {
        if (squad.IsBlocked)
        {
            return NifModelCurveResult.Blocked(squad.Block);
        }

        if (squad.Policy is not { } policy)
        {
            throw new ArgumentException("The Squad policy names neither a Shared policy nor a refusal.", nameof(squad));
        }

        var keys = QuaternionKeys(rotation);
        var count = keys.Count;
        if (!TryReadSquadTimes(keys, out var times, out var timeBlock))
        {
            return NifModelCurveResult.Blocked(timeBlock);
        }

        var w = new float[count];
        var x = new float[count];
        var y = new float[count];
        var z = new float[count];
        for (var key = 0; key < count; key++)
        {
            // File order w, x, y, z.
            w[key] = keys.Value(key, 0);
            x[key] = keys.Value(key, 1);
            y[key] = keys.Value(key, 2);
            z[key] = keys.Value(key, 3);
        }

        var quaternions = new NifModelSquadQuaternion[count];
        NifModelSquadQuaternion[] incoming;
        NifModelSquadQuaternion[] outgoing;
        if (count == 1)
        {
            quaternions[0] = new NifModelSquadQuaternion(w[0], x[0], y[0], z[0]);
            incoming = quaternions;
            outgoing = quaternions;
        }
        else
        {
            NifRotationKeyEngineRule.AlignChainAndClampW(w, x, y, z);
            for (var key = 0; key < count; key++)
            {
                quaternions[key] = new NifModelSquadQuaternion(w[key], x[key], y[key], z[key]);
            }

            if (keys.KeyType == (uint)NifKeyInterpolation.Tbc)
            {
                var tension = new float[count];
                var continuity = new float[count];
                var bias = new float[count];
                for (var key = 0; key < count; key++)
                {
                    tension[key] = BitConverter.UInt32BitsToSingle(keys.TensionBits(key));
                    continuity[key] = BitConverter.UInt32BitsToSingle(keys.ContinuityBits(key));
                    bias[key] = BitConverter.UInt32BitsToSingle(keys.BiasBits(key));
                }

                (incoming, outgoing) = NifModelSquadInnerPoints.Tbc(times, quaternions, tension, continuity, bias);
            }
            else
            {
                incoming = NifModelSquadInnerPoints.Quadratic(quaternions);
                outgoing = incoming;
            }
        }

        var values = new float[count * SquadStride];
        for (var key = 0; key < count; key++)
        {
            if (!incoming[key].IsFinite || !quaternions[key].IsFinite || !outgoing[key].IsFinite)
            {
                return NifModelCurveResult.Blocked(NifModelCurveBlock.NonFiniteValue);
            }

            if (incoming[key].IsZero || quaternions[key].IsZero || outgoing[key].IsZero)
            {
                return NifModelCurveResult.Blocked(NifModelCurveBlock.ZeroQuaternion);
            }

            var offset = key * SquadStride;
            incoming[key].WriteXyzw(values, offset);
            quaternions[key].WriteXyzw(values, offset + RotationComponentCount);
            outgoing[key].WriteXyzw(values, offset + 2 * RotationComponentCount);
        }

        return NifModelCurveResult.Keyed(new NifModelCurve(
            keys.KeyType, RotationComponentCount, times, values, SceneInterpolation.GamebryoSquad, null, null, policy));
    }

    /// <summary>
    ///     Reads a Squad group's key times: not finite is a key-time refusal; a time equal to the previous one is the
    ///     engine's zero-length span (RE-24); a time below the previous one is a key-time refusal.
    /// </summary>
    /// <param name="keys">A non-empty quaternion group.</param>
    /// <param name="times">The key times, one per key.</param>
    /// <param name="block">The refusal; <see cref="NifModelCurveBlock.None" /> when the times are usable.</param>
    /// <returns>True when Shared's time rule holds.</returns>
    private static bool TryReadSquadTimes(in NifKeyGroupView keys, out float[] times, out NifModelCurveBlock block)
    {
        block = NifModelCurveBlock.None;
        times = new float[keys.Count];
        for (var key = 0; key < times.Length; key++)
        {
            var time = keys.Time(key);
            if (!float.IsFinite(time))
            {
                block = NifModelCurveBlock.InvalidKeyTimes;
                return false;
            }

            if (key > 0 && time == times[key - 1])
            {
                block = NifModelCurveBlock.SquadZeroLengthSpan;
                return false;
            }

            if (key > 0 && time < times[key - 1])
            {
                block = NifModelCurveBlock.InvalidKeyTimes;
                return false;
            }

            times[key] = time;
        }

        return true;
    }

    /// <summary>The quaternion keys of a rotation view, checked against its stored count and type.</summary>
    /// <param name="rotation">A quaternion rotation view.</param>
    /// <returns>The key group.</returns>
    /// <exception cref="ArgumentException">The view's quaternion keys disagree with its stored count or type.</exception>
    private static NifKeyGroupView QuaternionKeys(in NifRotationKeysView rotation)
    {
        var keys = rotation.Keys;
        if (keys.Layout != NifKeyValueLayout.Quaternion || keys.KeyType != rotation.KeyType ||
            keys.NumKeys != rotation.StoredKeyCount)
        {
            throw new ArgumentException(
                "The rotation view's quaternion keys disagree with its stored key count or key type.", nameof(rotation));
        }

        return keys;
    }

    /// <summary>Shared's own Float32 unit test for a rotation key (squared length within 1e-4 of one).</summary>
    /// <param name="value">The normalized key.</param>
    /// <returns>True when Shared's key validation accepts the key.</returns>
    private static bool IsSharedUnitRotation(Quaternion value)
    {
        return MathF.Abs(value.LengthSquared() - 1f) <= 0.0001f;
    }

    /// <summary>
    ///     Copies a LINEAR, CONST or TBC group's values key-major, each stored component repeated
    ///     <paramref name="replication" /> times.
    /// </summary>
    /// <param name="group">The float or Vector3 group.</param>
    /// <param name="times">The already validated key times.</param>
    /// <param name="width">The Shared width (stored components times replication).</param>
    /// <param name="replication">How many times each stored component is repeated.</param>
    /// <param name="interpolation">The Shared interpolation of the curve.</param>
    /// <returns>The curve, or <see cref="NifModelCurveBlock.NonFiniteValue" />.</returns>
    private static NifModelCurveResult MapPlain(
        in NifKeyGroupView group, float[] times, int width, int replication, SceneInterpolation interpolation)
    {
        var values = new float[times.Length * width];
        for (var key = 0; key < times.Length; key++)
        {
            for (var component = 0; component < width; component++)
            {
                var value = group.Value(key, component / replication);
                if (!float.IsFinite(value))
                {
                    return NifModelCurveResult.Blocked(NifModelCurveBlock.NonFiniteValue);
                }

                values[key * width + component] = value;
            }
        }

        return NifModelCurveResult.Keyed(new NifModelCurve(
            group.KeyType, width, times, values, interpolation, null, null));
    }

    /// <summary>
    ///     RE-18: each key's stored Forward is its incoming tangent and Backward its outgoing one, both normalized-segment
    ///     tangents; Shared's Hermite triple [incoming, value, outgoing] takes them exactly as stored.
    /// </summary>
    /// <param name="group">The QUADRATIC float or Vector3 group.</param>
    /// <param name="times">The already validated key times.</param>
    /// <param name="width">The Shared width (stored components times replication).</param>
    /// <param name="replication">How many times each stored component is repeated.</param>
    /// <returns>The Hermite curve, or <see cref="NifModelCurveBlock.NonFiniteValue" />.</returns>
    private static NifModelCurveResult MapHermite(in NifKeyGroupView group, float[] times, int width, int replication)
    {
        var values = new float[times.Length * 3 * width];
        for (var key = 0; key < times.Length; key++)
        {
            var incomingOffset = key * 3 * width;
            var valueOffset = incomingOffset + width;
            var outgoingOffset = valueOffset + width;
            for (var component = 0; component < width; component++)
            {
                var stored = component / replication;
                var incoming = group.Forward(key, stored);
                var value = group.Value(key, stored);
                var outgoing = group.Backward(key, stored);
                if (!float.IsFinite(incoming) || !float.IsFinite(value) || !float.IsFinite(outgoing))
                {
                    return NifModelCurveResult.Blocked(NifModelCurveBlock.NonFiniteValue);
                }

                values[incomingOffset + component] = incoming;
                values[valueOffset + component] = value;
                values[outgoingOffset + component] = outgoing;
            }
        }

        return NifModelCurveResult.Keyed(new NifModelCurve(
            group.KeyType, width, times, values, SceneInterpolation.Hermite, null, null));
    }

    /// <summary>
    ///     The TBC order rule and RE-19: the three stored floats are, in file order, tension, continuity and bias; the
    ///     boundary tangents are the engine's mirrored-neighbor tangents.
    /// </summary>
    /// <param name="group">The TBC float or Vector3 group.</param>
    /// <param name="times">The already validated key times.</param>
    /// <param name="width">The Shared width (stored components times replication).</param>
    /// <param name="replication">How many times each stored component is repeated.</param>
    /// <returns>The TBC curve, or <see cref="NifModelCurveBlock.NonFiniteValue" />.</returns>
    private static NifModelCurveResult MapTbc(in NifKeyGroupView group, float[] times, int width, int replication)
    {
        var plain = MapPlain(group, times, width, replication, SceneInterpolation.Tbc);
        if (plain.Curve is not { } curve)
        {
            return plain;
        }

        var parameters = new SceneTbcParameters[times.Length];
        for (var key = 0; key < times.Length; key++)
        {
            var tension = BitConverter.UInt32BitsToSingle(group.TensionBits(key));
            var continuity = BitConverter.UInt32BitsToSingle(group.ContinuityBits(key));
            var bias = BitConverter.UInt32BitsToSingle(group.BiasBits(key));
            if (!float.IsFinite(tension) || !float.IsFinite(continuity) || !float.IsFinite(bias))
            {
                return NifModelCurveResult.Blocked(NifModelCurveBlock.NonFiniteValue);
            }

            parameters[key] = new SceneTbcParameters(tension, continuity, bias);
        }

        if (!NifModelTbcEndpoints.TryCompute(group, replication, out var endpoints))
        {
            return NifModelCurveResult.Blocked(NifModelCurveBlock.NonFiniteValue);
        }

        return NifModelCurveResult.Keyed(curve with { TbcParameters = parameters, TbcEndpoints = endpoints });
    }

    /// <summary>Reads every key time; false when one is not finite or the times are not strictly increasing.</summary>
    /// <param name="group">A non-empty group with a known key type.</param>
    /// <param name="times">The key times, one per key.</param>
    /// <returns>True when Shared's time rule holds.</returns>
    private static bool TryReadTimes(in NifKeyGroupView group, out float[] times)
    {
        times = new float[group.Count];
        for (var key = 0; key < times.Length; key++)
        {
            var time = group.Time(key);
            if (!float.IsFinite(time) || (key > 0 && time <= times[key - 1]))
            {
                return false;
            }

            times[key] = time;
        }

        return true;
    }

    /// <summary>Rejects a group of the wrong value type for a transform channel.</summary>
    /// <param name="group">The group.</param>
    /// <param name="layout">The value type the channel stores.</param>
    private static void RequireLayout(in NifKeyGroupView group, NifKeyValueLayout layout)
    {
        if (group.Layout != layout)
        {
            throw new ArgumentException($"Expected a {layout} key group, not {group.Layout}.", nameof(group));
        }
    }
}
