using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A copy of the three renderer methods of <see cref="NifKeyGroupReader" /> as they stood before the
///     lossless views (blob 63c00aa4 at commit 2a0162e8), kept only as the reference the projection-equivalence tests
///     compare against. The production methods are now projections of <see cref="NifKeyGroupView" />; this copy is what
///     proves they still return the renderer the same bits, on synthetic groups
///     (<see cref="NifKeyGroupReaderProjectionEquivalenceTests" />) and on every cut-1b manifest file
///     (NifAnimationViewOracleTests). The method bodies are verbatim (only this summary, the remarks and one comment dash
///     differ); do not edit them, since a change here voids the proof.
/// </summary>
/// <remarks>
///     Its XYZ-Euler walk follows nif.xml (one record whatever Num Rotation Keys says, an Order float up to 10.1.0.0), which
///     the projection deliberately no longer matches: it follows the engine (RE-20). On an Euler block with more than one
///     record, or at a stream version from 10.1.0.1 to 10.1.0.103, the comparison therefore uses the engine's walk composed
///     from this class's float-group reader instead (<see cref="NifKeyGroupProjectionSignatures" />). Neither kind of
///     block is known in retail data (see <see cref="NifKeyGroupReader" />'s remarks).
/// </remarks>
internal static class NifKeyGroupReaderLegacyReference
{
    private const uint MaxKeys = 1 << 20; // sanity cap: no real track has a million keys

    /// <summary>
    ///     Reads a rotation key block (count + type + keys). XYZ-Euler rotations (type 4) are three
    ///     per-axis float KeyGroups, reported through <paramref name="eulerKeys" /> (the
    ///     Goodsprings saloon sign's swing is authored this way) with no quaternion keys.
    /// </summary>
    internal static bool TryReadQuatKeys(
        byte[] data, ref int pos, int end, bool be, uint binaryVersion,
        out NifKeyInterpolation interpolation, out NifQuatKey[] keys,
        out (NifFloatKey[] X, NifFloatKey[] Y, NifFloatKey[] Z)? eulerKeys)
    {
        interpolation = NifKeyInterpolation.Linear;
        keys = [];
        eulerKeys = null;
        if (pos + 4 > end)
        {
            return false;
        }

        var numKeys = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numKeys == 0)
        {
            return true;
        }

        if (numKeys > MaxKeys || pos + 4 > end)
        {
            return false;
        }

        var rawType = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (rawType < (uint)NifKeyInterpolation.Linear ||
            rawType > (uint)NifKeyInterpolation.Constant)
        {
            return false;
        }

        interpolation = (NifKeyInterpolation)rawType;

        if (interpolation == NifKeyInterpolation.XyzEuler)
        {
            // nif.xml: an Order float precedes the axis groups until 10.1.0.0.
            if (binaryVersion <= NifVersions.Gamebryo10100)
            {
                if (pos + 4 > end)
                {
                    return false;
                }

                pos += 4;
            }

            var axes = new NifFloatKey[3][];
            for (var axis = 0; axis < 3; axis++)
            {
                if (!TryReadFloatKeys(data, ref pos, end, be, out _, out var axisKeys))
                {
                    return false;
                }

                axes[axis] = axisKeys;
            }

            eulerKeys = (axes[0], axes[1], axes[2]);
            return true;
        }

        var stride = interpolation switch
        {
            NifKeyInterpolation.Tbc => 32,
            _ => 20 // Linear / Quadratic / Constant: time + quaternion, no tangents
        };
        if (pos + numKeys * stride > end)
        {
            return false;
        }

        keys = new NifQuatKey[numKeys];
        for (var i = 0; i < numKeys; i++)
        {
            var time = BinaryUtils.ReadFloat(data, pos, be);
            var w = BinaryUtils.ReadFloat(data, pos + 4, be);
            var x = BinaryUtils.ReadFloat(data, pos + 8, be);
            var y = BinaryUtils.ReadFloat(data, pos + 12, be);
            var z = BinaryUtils.ReadFloat(data, pos + 16, be);
            keys[i] = new NifQuatKey(time, new Quaternion(x, y, z, w));
            pos += stride;
        }

        return true;
    }

    /// <summary>Reads a KeyGroup&lt;Vector3&gt; (count + type-if-any + keys).</summary>
    internal static bool TryReadVector3Keys(
        byte[] data, ref int pos, int end, bool be,
        out NifKeyInterpolation interpolation, out NifVec3Key[] keys)
    {
        interpolation = NifKeyInterpolation.Linear;
        keys = [];
        if (pos + 4 > end)
        {
            return false;
        }

        var numKeys = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numKeys == 0)
        {
            return true;
        }

        if (numKeys > MaxKeys || pos + 4 > end)
        {
            return false;
        }

        var rawType = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (!IsScalarInterpolation(rawType))
        {
            return false;
        }

        interpolation = (NifKeyInterpolation)rawType;

        var stride = interpolation switch
        {
            NifKeyInterpolation.Quadratic => 40, // time + value + forward + backward
            NifKeyInterpolation.Tbc => 28, // time + value + tension/bias/continuity
            _ => 16
        };
        if (pos + numKeys * stride > end)
        {
            return false;
        }

        keys = new NifVec3Key[numKeys];
        for (var i = 0; i < numKeys; i++)
        {
            keys[i] = new NifVec3Key(
                BinaryUtils.ReadFloat(data, pos, be),
                new Vector3(
                    BinaryUtils.ReadFloat(data, pos + 4, be),
                    BinaryUtils.ReadFloat(data, pos + 8, be),
                    BinaryUtils.ReadFloat(data, pos + 12, be)),
                interpolation == NifKeyInterpolation.Quadratic ? ReadVector(data, pos + 16, be) : default,
                interpolation == NifKeyInterpolation.Quadratic ? ReadVector(data, pos + 28, be) : default,
                interpolation == NifKeyInterpolation.Quadratic);
            if (keys[i].HasQuadraticTangents &&
                (!NifQuadraticVectorCurve.IsFiniteAuthored(keys[i].Value) ||
                 !NifQuadraticVectorCurve.IsFiniteAuthored(keys[i].Forward) ||
                 !NifQuadraticVectorCurve.IsFiniteAuthored(keys[i].Backward)))
            {
                keys = [];
                return false;
            }

            pos += stride;
        }

        return true;
    }

    /// <summary>Reads a KeyGroup&lt;float&gt; (count + type-if-any + keys).</summary>
    internal static bool TryReadFloatKeys(
        byte[] data, ref int pos, int end, bool be,
        out NifKeyInterpolation interpolation, out NifFloatKey[] keys)
    {
        interpolation = NifKeyInterpolation.Linear;
        keys = [];
        if (pos + 4 > end)
        {
            return false;
        }

        var numKeys = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numKeys == 0)
        {
            return true;
        }

        if (numKeys > MaxKeys || pos + 4 > end)
        {
            return false;
        }

        var rawType = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (!IsScalarInterpolation(rawType))
        {
            return false;
        }

        interpolation = (NifKeyInterpolation)rawType;

        var stride = interpolation switch
        {
            NifKeyInterpolation.Quadratic => 16, // time + value + forward + backward
            NifKeyInterpolation.Tbc => 20, // time + value + tension/bias/continuity
            _ => 8
        };
        if (pos + numKeys * stride > end)
        {
            return false;
        }

        keys = new NifFloatKey[numKeys];
        for (var i = 0; i < numKeys; i++)
        {
            keys[i] = new NifFloatKey(
                BinaryUtils.ReadFloat(data, pos, be),
                BinaryUtils.ReadFloat(data, pos + 4, be));
            pos += stride;
        }

        return true;
    }

    private static Vector3 ReadVector(byte[] data, int pos, bool be)
    {
        return new Vector3(
            BinaryUtils.ReadFloat(data, pos, be),
            BinaryUtils.ReadFloat(data, pos + 4, be),
            BinaryUtils.ReadFloat(data, pos + 8, be));
    }

    private static bool IsScalarInterpolation(uint rawType)
    {
        return rawType == (uint)NifKeyInterpolation.Linear ||
               rawType == (uint)NifKeyInterpolation.Quadratic ||
               rawType == (uint)NifKeyInterpolation.Tbc ||
               rawType == (uint)NifKeyInterpolation.Constant;
    }
}
