using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Reads NIF key groups (nif.xml <c>KeyGroup&lt;T&gt;</c> / <c>QuatKey</c> arrays) and ADVANCES a
///     cursor by the exact per-type stride — the property the single-key snapshot reader
///     (<see cref="NifTransformDataKeyframeReader" />) doesn't need and doesn't have. A wrong stride
///     here desyncs every group that follows, so the strides are taken from nif.xml, not from the
///     snapshot reader (whose Quadratic values for quats/vectors disagree with the format spec):
///     <list type="bullet">
///         <item>QuatKey: Linear/Quadratic 20 bytes (quaternion keys never carry tangents), TBC 32.</item>
///         <item>Key&lt;Vector3&gt;: Linear 16, Quadratic 40 (value + forward + backward), TBC 28.</item>
///         <item>Key&lt;float&gt;: Linear 8, Quadratic 16, TBC 20.</item>
///     </list>
///     Tangent/TBC payloads are parsed-and-skipped — only <c>(time, value)</c> is retained; the
///     authored interpolation type is preserved as a label on the owning track.
/// </summary>
internal static class NifKeyGroupReader
{
    private const uint MaxKeys = 1 << 20; // sanity cap: no real track has a million keys

    /// <summary>
    ///     Reads a rotation key block (count + type + keys). XYZ-Euler rotations (type 4) are three
    ///     per-axis float KeyGroups — reported through <paramref name="eulerKeys" /> (the
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
            _ => 20, // Linear / Quadratic / Constant: time + quaternion, no tangents
        };
        if (pos + (long)numKeys * stride > end)
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

        interpolation = (NifKeyInterpolation)BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        var stride = interpolation switch
        {
            NifKeyInterpolation.Quadratic => 40, // time + value + forward + backward
            NifKeyInterpolation.Tbc => 28,       // time + value + tension/bias/continuity
            _ => 16,
        };
        if (pos + (long)numKeys * stride > end)
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
                    BinaryUtils.ReadFloat(data, pos + 12, be)));
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

        interpolation = (NifKeyInterpolation)BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        var stride = interpolation switch
        {
            NifKeyInterpolation.Quadratic => 16, // time + value + forward + backward
            NifKeyInterpolation.Tbc => 20,       // time + value + tension/bias/continuity
            _ => 8,
        };
        if (pos + (long)numKeys * stride > end)
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
}
