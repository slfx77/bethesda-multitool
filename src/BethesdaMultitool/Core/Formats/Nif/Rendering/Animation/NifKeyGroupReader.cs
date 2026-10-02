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
///         <item>Key&lt;Color4&gt;: Linear 20, Quadratic 52, TBC 32; Key&lt;byte&gt;: Linear 5, Quadratic 7, TBC 17.</item>
///     </list>
/// </summary>
/// <remarks>
///     <para>
///         ONE decode path serves the renderer and the lossless model reader (cut-1b owner ruling D5). The structural walk
///         (<see cref="TryReadGroupView" />, <see cref="TryReadRotationView" />) validates only what bounds the read: the
///         key count cap, the allowed key types, and the keys fitting the span. It exposes every stored field as raw bits
///         (<see cref="NifKeyGroupView" />, <see cref="NifRotationKeysView" />): Quadratic tangents, TBC triples, each
///         Euler axis's own key type, the stored Euler record count and each record's legacy word.
///     </para>
///     <para>
///         The renderer's <see cref="TryReadQuatKeys" />, <see cref="TryReadVector3Keys" /> and
///         <see cref="TryReadFloatKeys" /> are projections of that walk. They re-apply today's value gate unchanged (a
///         Quadratic Vector3 key with a non-finite or |x| &gt;= 1e30 value or tangent rejects the group) and build every
///         float with <see cref="BitConverter.UInt32BitsToSingle" /> of the endian-swapped word, which is exactly what
///         <see cref="BinaryUtils.ReadFloat(byte[], int, bool)" /> did, so every renderer value is bit-identical to the
///         pre-view reader, except on the two kinds of Euler block described next. Scalar tangents and TBC payloads stay
///         out of the renderer's key records; the authored interpolation type is preserved as a label on the owning
///         track.
///     </para>
///     <para>
///         An XYZ-Euler rotation (type 4) is walked as the engine reads it (RE-20): Num Rotation Keys counts
///         <c>NiEulerRotKey</c> records, each a legacy word below stream version 10.1.0.104
///         (<see cref="EulerLegacyWordEndVersion" />) followed by the X, Y and Z angle groups, and every record is walked;
///         the renderer's projection reports the first record, the only one the engine evaluates. The pre-view reader
///         followed nif.xml instead (one record whatever the count, an Order float up to 10.1.0.0), so the projection
///         deliberately differs from it on exactly two kinds of Euler block: one storing more than one record, where the
///         old cursor stopped after the first record and the translation group was read from the second record's bytes,
///         and one at a stream version from 10.1.0.1 to 10.1.0.103, where the old reader skipped no legacy word. Neither is
///         known in retail data: the stored record count is 1 on all 70,104 Euler blocks RE-20 measured (20.2.0.7),
///         Morrowind, Tribunal and Bloodmoon store no Euler block (none of their 10,057 NiKeyframeData blocks is type 4), a
///         byte scan of Oblivion's meshes finds only single-record Euler blocks, and the only retail NIFs in that version
///         range (eight Oblivion files at 10.1.0.101) carry no keyframe data.
///     </para>
///     <para>
///         On a false return the out values match the pre-view reader (the interpolation is the stored key type once it
///         was read and accepted, Linear before that; the keys are empty), but the cursor position does not; no caller
///         reads it after a failure.
///     </para>
/// </remarks>
internal static class NifKeyGroupReader
{
    private const uint MaxKeys = 1 << 20; // sanity cap: no real track has a million keys
    private const int TbcByteLength = 12; // tension, continuity, bias

    /// <summary>
    ///     The first stream version whose <c>NiEulerRotKey</c> records carry no leading legacy word: 10.1.0.104. FNV's
    ///     NiEulerRotKey::LoadBinary (0xA29F00, the GECK's 0x7E1ED0 is byte-identical) reads and discards one u32 before
    ///     the three axis groups while the stream version is below it (<c>cmp [stream+0xD8], 0x0A010068; jae</c>, RE-20).
    ///     nif.xml ends its Order float at 10.1.0.0 instead; the two disagree from 10.1.0.1 to 10.1.0.103, and the engine
    ///     is followed.
    /// </summary>
    internal const uint EulerLegacyWordEndVersion = 0x0A010068;

    /// <summary>
    ///     Reads a rotation key block (count + type + keys). XYZ-Euler rotations (type 4) are <c>NiEulerRotKey</c> records
    ///     of three per-axis float KeyGroups; every record is walked and the first one's axes are reported through
    ///     <paramref name="eulerKeys" /> (the Goodsprings saloon sign's swing is authored this way) with no quaternion keys.
    /// </summary>
    internal static bool TryReadQuatKeys(
        byte[] data, ref int pos, int end, bool be, uint binaryVersion,
        out NifKeyInterpolation interpolation, out NifQuatKey[] keys,
        out (NifFloatKey[] X, NifFloatKey[] Y, NifFloatKey[] Z)? eulerKeys)
    {
        keys = [];
        eulerKeys = null;
        var read = TryReadRotationCore(data, ref pos, end, be, binaryVersion, out var acceptedKeyType, out var view);
        interpolation = ToInterpolation(acceptedKeyType);
        if (!read || view.StoredKeyCount == 0)
        {
            return read;
        }

        if (view.IsEuler)
        {
            eulerKeys = (ToFloatKeys(view.EulerX), ToFloatKeys(view.EulerY), ToFloatKeys(view.EulerZ));
            return true;
        }

        keys = ToQuatKeys(view.Keys);
        return true;
    }

    /// <summary>Reads a KeyGroup&lt;Vector3&gt; (count + type-if-any + keys).</summary>
    internal static bool TryReadVector3Keys(
        byte[] data, ref int pos, int end, bool be,
        out NifKeyInterpolation interpolation, out NifVec3Key[] keys)
    {
        keys = [];
        var read = TryReadGroupCore(data, ref pos, end, be, NifKeyValueLayout.Vector3, out var acceptedKeyType,
            out var view);
        interpolation = ToInterpolation(acceptedKeyType);
        return read && TryProjectVector3Keys(view, out keys);
    }

    /// <summary>Reads a KeyGroup&lt;float&gt; (count + type-if-any + keys).</summary>
    internal static bool TryReadFloatKeys(
        byte[] data, ref int pos, int end, bool be,
        out NifKeyInterpolation interpolation, out NifFloatKey[] keys)
    {
        keys = [];
        var read = TryReadGroupCore(data, ref pos, end, be, NifKeyValueLayout.Float, out var acceptedKeyType,
            out var view);
        interpolation = ToInterpolation(acceptedKeyType);
        if (!read)
        {
            return false;
        }

        keys = ToFloatKeys(view);
        return true;
    }

    /// <summary>
    ///     Reads one KeyGroup&lt;T&gt; of the given value type losslessly and advances the cursor past it. Key types
    ///     1, 2, 3 and 5 are accepted (4 is a rotation-only form, read through <see cref="TryReadRotationView" />).
    /// </summary>
    /// <returns>False when the group is truncated, its count exceeds the sanity cap, or its key type is not allowed.</returns>
    internal static bool TryReadGroupView(
        byte[] data, ref int pos, int end, bool be, NifKeyValueLayout layout, out NifKeyGroupView view)
    {
        return TryReadGroupCore(data, ref pos, end, be, layout, out _, out view);
    }

    /// <summary>
    ///     Reads the rotation part of an NiKeyframeData/NiTransformData block losslessly and advances the cursor past it:
    ///     quaternion keys (types 1, 2, 3, 5), or for type 4 every <c>NiEulerRotKey</c> record (RE-20), each a legacy word
    ///     below 10.1.0.104 and three float axis groups keeping their own key types; the view exposes the first record.
    /// </summary>
    internal static bool TryReadRotationView(
        byte[] data, ref int pos, int end, bool be, uint binaryVersion, out NifRotationKeysView view)
    {
        return TryReadRotationCore(data, ref pos, end, be, binaryVersion, out _, out view);
    }

    /// <summary>
    ///     Reads one <c>NiEulerRotKey</c> record losslessly and advances the cursor past it: the legacy word when the stream
    ///     version is below <see cref="EulerLegacyWordEndVersion" />, then the X, Y and Z float key groups (key types 1, 2,
    ///     3 and 5, each its own).
    /// </summary>
    /// <returns>False when the record is truncated or an axis group is refused (see <see cref="TryReadGroupView" />).</returns>
    internal static bool TryReadEulerRecordView(
        byte[] data, ref int pos, int end, bool be, uint binaryVersion, out NifEulerRotationRecordView record)
    {
        record = default;
        var offset = pos;
        var hasLegacyWord = HasEulerLegacyWord(binaryVersion);
        uint legacyWordBits = 0;
        if (hasLegacyWord)
        {
            if (pos + 4 > end)
            {
                return false;
            }

            legacyWordBits = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
        }

        if (!TryReadGroupCore(data, ref pos, end, be, NifKeyValueLayout.Float, out _, out var x) ||
            !TryReadGroupCore(data, ref pos, end, be, NifKeyValueLayout.Float, out _, out var y) ||
            !TryReadGroupCore(data, ref pos, end, be, NifKeyValueLayout.Float, out _, out var z))
        {
            return false;
        }

        record = new NifEulerRotationRecordView(offset, hasLegacyWord, legacyWordBits, x, y, z);
        return true;
    }

    /// <summary>True when an <c>NiEulerRotKey</c> record of this stream version stores the leading legacy word.</summary>
    internal static bool HasEulerLegacyWord(uint binaryVersion)
    {
        return binaryVersion < EulerLegacyWordEndVersion;
    }

    /// <summary>The value type of a single-group key-data block (NiFloatData, NiPosData, NiBoolData, NiColorData).</summary>
    internal static bool TryGetDataBlockLayout(string typeName, out NifKeyValueLayout layout)
    {
        switch (typeName)
        {
            case "NiFloatData":
                layout = NifKeyValueLayout.Float;
                return true;
            case "NiPosData":
                layout = NifKeyValueLayout.Vector3;
                return true;
            case "NiBoolData":
                layout = NifKeyValueLayout.Byte;
                return true;
            case "NiColorData":
                layout = NifKeyValueLayout.Color4;
                return true;
            default:
                layout = default;
                return false;
        }
    }

    /// <summary>
    ///     Reads the one key group of an NiFloatData, NiPosData, NiBoolData or NiColorData block losslessly. The block
    ///     span must lie inside <paramref name="data" />; whether the group consumes it exactly is left to the caller
    ///     (<see cref="NifKeyGroupView.EndOffset" /> against the block end).
    /// </summary>
    internal static bool TryReadDataBlockView(byte[] data, NifInfo nif, BlockInfo block, out NifKeyGroupView view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = default;
        if (!TryGetDataBlockLayout(block.TypeName, out var layout) ||
            block.DataOffset < 0 || block.Size < 0 ||
            (long)block.DataOffset + block.Size > data.LongLength)
        {
            return false;
        }

        var pos = block.DataOffset;
        return TryReadGroupView(data, ref pos, block.DataOffset + block.Size, nif.IsBigEndian, layout, out view);
    }

    /// <summary>The byte width of one stored value of the given type.</summary>
    internal static int GetValueWidth(NifKeyValueLayout layout)
    {
        return layout switch
        {
            NifKeyValueLayout.Byte => 1,
            NifKeyValueLayout.Float => 4,
            NifKeyValueLayout.Vector3 => 12,
            NifKeyValueLayout.Color4 => 16,
            NifKeyValueLayout.Quaternion => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown key value layout.")
        };
    }

    /// <summary>The number of 32-bit components in one stored value (1 for a byte).</summary>
    internal static int GetComponentCount(NifKeyValueLayout layout)
    {
        return layout switch
        {
            NifKeyValueLayout.Byte => 1,
            NifKeyValueLayout.Float => 1,
            NifKeyValueLayout.Vector3 => 3,
            NifKeyValueLayout.Color4 => 4,
            NifKeyValueLayout.Quaternion => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown key value layout.")
        };
    }

    /// <summary>
    ///     The byte length of one key (nif.xml <c>Key&lt;T&gt;</c>, <c>QuatKey</c>): Time plus Value for LINEAR and CONST,
    ///     plus Forward and Backward for QUADRATIC (never for a quaternion), plus the three TBC floats for TBC.
    /// </summary>
    internal static int GetStride(NifKeyValueLayout layout, uint keyType)
    {
        var width = GetValueWidth(layout);
        switch (keyType)
        {
            case (uint)NifKeyInterpolation.Linear:
            case (uint)NifKeyInterpolation.Constant:
                return 4 + width;
            case (uint)NifKeyInterpolation.Quadratic:
                return layout == NifKeyValueLayout.Quaternion ? 4 + width : 4 + 3 * width;
            case (uint)NifKeyInterpolation.Tbc:
                return 4 + width + TbcByteLength;
            default:
                throw new ArgumentOutOfRangeException(nameof(keyType), keyType,
                    "Only key types 1, 2, 3 and 5 have a key stride.");
        }
    }

    /// <summary>
    ///     The structural walk of one non-rotation key group. <paramref name="acceptedKeyType" /> is the stored key type
    ///     once it was read and accepted (0 before that), also on a false return, so the projections can report the same
    ///     interpolation as the pre-view reader on every path.
    /// </summary>
    private static bool TryReadGroupCore(
        byte[] data, ref int pos, int end, bool be, NifKeyValueLayout layout,
        out uint acceptedKeyType, out NifKeyGroupView view)
    {
        acceptedKeyType = 0;
        view = default;
        if (pos + 4 > end)
        {
            return false;
        }

        var offset = pos;
        var numKeys = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numKeys == 0)
        {
            view = new NifKeyGroupView(data, be, layout, offset, 0, 0, pos, 0);
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

        acceptedKeyType = rawType;
        return TryReadKeys(data, ref pos, end, be, layout, offset, numKeys, rawType, out view);
    }

    private static bool TryReadRotationCore(
        byte[] data, ref int pos, int end, bool be, uint binaryVersion,
        out uint acceptedKeyType, out NifRotationKeysView view)
    {
        acceptedKeyType = 0;
        view = default;
        if (pos + 4 > end)
        {
            return false;
        }

        var offset = pos;
        var numKeys = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numKeys == 0)
        {
            var none = new NifKeyGroupView(data, be, NifKeyValueLayout.Quaternion, offset, 0, 0, pos, 0);
            view = new NifRotationKeysView(offset, 0, 0, none, default, pos);
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

        acceptedKeyType = rawType;
        if (rawType == (uint)NifKeyInterpolation.XyzEuler)
        {
            return TryReadEulerRecords(data, ref pos, end, be, binaryVersion, offset, numKeys, out view);
        }

        if (!TryReadKeys(data, ref pos, end, be, NifKeyValueLayout.Quaternion, offset, numKeys, rawType,
                out var keys))
        {
            return false;
        }

        view = new NifRotationKeysView(offset, numKeys, rawType, keys, default, pos);
        return true;
    }

    /// <summary>
    ///     The engine's Euler walk (RE-20): <paramref name="recordCount" /> <c>NiEulerRotKey</c> records back to back. The
    ///     engine evaluates only the first; the others are walked so the block stays byte-exact.
    /// </summary>
    private static bool TryReadEulerRecords(
        byte[] data, ref int pos, int end, bool be, uint binaryVersion, int offset, uint recordCount,
        out NifRotationKeysView view)
    {
        view = default;
        var noQuaternionKeys = new NifKeyGroupView(data, be, NifKeyValueLayout.Quaternion, offset, 0, 0, pos, 0);
        if (!TryReadEulerRecordView(data, ref pos, end, be, binaryVersion, out var first))
        {
            return false;
        }

        for (var record = 1u; record < recordCount; record++)
        {
            if (!TryReadEulerRecordView(data, ref pos, end, be, binaryVersion, out _))
            {
                return false;
            }
        }

        view = new NifRotationKeysView(offset, recordCount, (uint)NifKeyInterpolation.XyzEuler, noQuaternionKeys,
            first, pos);
        return true;
    }

    private static bool TryReadKeys(
        byte[] data, ref int pos, int end, bool be, NifKeyValueLayout layout, int offset, uint numKeys,
        uint keyType, out NifKeyGroupView view)
    {
        view = default;
        var stride = GetStride(layout, keyType);
        if (pos + numKeys * stride > end)
        {
            return false;
        }

        view = new NifKeyGroupView(data, be, layout, offset, numKeys, keyType, pos, stride);
        pos += (int)(numKeys * stride);
        return true;
    }

    private static NifKeyInterpolation ToInterpolation(uint acceptedKeyType)
    {
        return acceptedKeyType == 0 ? NifKeyInterpolation.Linear : (NifKeyInterpolation)acceptedKeyType;
    }

    private static NifQuatKey[] ToQuatKeys(in NifKeyGroupView view)
    {
        if (view.Count == 0)
        {
            return [];
        }

        var keys = new NifQuatKey[view.Count];
        for (var i = 0; i < keys.Length; i++)
        {
            // File order w, x, y, z; System.Numerics takes x, y, z, w.
            keys[i] = new NifQuatKey(
                view.Time(i),
                new Quaternion(view.Value(i, 1), view.Value(i, 2), view.Value(i, 3), view.Value(i, 0)));
        }

        return keys;
    }

    private static bool TryProjectVector3Keys(in NifKeyGroupView view, out NifVec3Key[] keys)
    {
        keys = [];
        if (view.Count == 0)
        {
            return true;
        }

        var quadratic = view.KeyType == (uint)NifKeyInterpolation.Quadratic;
        var projected = new NifVec3Key[view.Count];
        for (var i = 0; i < projected.Length; i++)
        {
            projected[i] = new NifVec3Key(
                view.Time(i),
                new Vector3(view.Value(i, 0), view.Value(i, 1), view.Value(i, 2)),
                quadratic ? new Vector3(view.Forward(i, 0), view.Forward(i, 1), view.Forward(i, 2)) : default,
                quadratic ? new Vector3(view.Backward(i, 0), view.Backward(i, 1), view.Backward(i, 2)) : default,
                quadratic);
            if (projected[i].HasQuadraticTangents &&
                (!NifQuadraticVectorCurve.IsFiniteAuthored(projected[i].Value) ||
                 !NifQuadraticVectorCurve.IsFiniteAuthored(projected[i].Forward) ||
                 !NifQuadraticVectorCurve.IsFiniteAuthored(projected[i].Backward)))
            {
                return false;
            }
        }

        keys = projected;
        return true;
    }

    private static NifFloatKey[] ToFloatKeys(in NifKeyGroupView view)
    {
        if (view.Count == 0)
        {
            return [];
        }

        var keys = new NifFloatKey[view.Count];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = new NifFloatKey(view.Time(i), view.Value(i, 0));
        }

        return keys;
    }

    private static bool IsScalarInterpolation(uint rawType)
    {
        return rawType == (uint)NifKeyInterpolation.Linear ||
               rawType == (uint)NifKeyInterpolation.Quadratic ||
               rawType == (uint)NifKeyInterpolation.Tbc ||
               rawType == (uint)NifKeyInterpolation.Constant;
    }
}
