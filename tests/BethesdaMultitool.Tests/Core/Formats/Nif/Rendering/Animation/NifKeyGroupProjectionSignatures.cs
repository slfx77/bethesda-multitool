using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Runs one renderer key-group read through the verbatim pre-view reader
///     (<see cref="NifKeyGroupReaderLegacyReference" />) and through the view projection (<see cref="NifKeyGroupReader" />)
///     from the same start, and renders both results as signatures: the return value, the interpolation label, every key
///     float as its bits (time, value, both tangents and the tangent flag, so a NaN payload or a signed zero counts), the
///     Euler axis keys, and on success the cursor (no caller reads it after a failure). Equal signatures mean the renderer
///     sees the same values.
/// </summary>
/// <remarks>
///     One kind of block has a different reference, by design: an XYZ-Euler block that stores more than one
///     <c>NiEulerRotKey</c> record, or sits at a stream version from 10.1.0.1 to 10.1.0.103. There the projection follows
///     the engine (RE-20) and the pre-view reader followed nif.xml, so the reference is the engine's walk composed from the
///     pre-view reader's own float-group reader: every record as the legacy word (below 10.1.0.104) and three float
///     groups, the first record's axes reported, the cursor past the last record.
/// </remarks>
internal static class NifKeyGroupProjectionSignatures
{
    /// <summary>NifKeyGroupReader's key-count sanity cap (1 &lt;&lt; 20).</summary>
    private const uint KeyCap = 1u << 20;

    /// <summary>RE-20: an NiEulerRotKey record stores the leading legacy word below stream version 10.1.0.104.</summary>
    private const uint EngineLegacyWordEnd = 0x0A010068;

    /// <summary>
    ///     The rotation reader (<c>TryReadQuatKeys</c>) from <paramref name="start" />. An XYZ-Euler block on which the
    ///     engine's walk and the pre-view reader's differ is compared with the composed engine reference (see the remarks).
    /// </summary>
    public static NifKeyGroupProjectionComparison Quat(
        byte[] data, int start, int end, bool bigEndian, uint binaryVersion)
    {
        if (TryGetDivergentEulerRecordCount(data, start, end, bigEndian, binaryVersion, out var records))
        {
            return EngineEuler(data, start, end, bigEndian, binaryVersion, records);
        }

        var legacyPos = start;
        var legacyRead = NifKeyGroupReaderLegacyReference.TryReadQuatKeys(
            data, ref legacyPos, end, bigEndian, binaryVersion, out var legacyInterpolation, out var legacyKeys,
            out var legacyEuler);
        var viewPos = start;
        var viewRead = NifKeyGroupReader.TryReadQuatKeys(
            data, ref viewPos, end, bigEndian, binaryVersion, out var viewInterpolation, out var viewKeys,
            out var viewEuler);
        return new NifKeyGroupProjectionComparison(
            legacyRead,
            legacyPos,
            legacyInterpolation,
            legacyKeys.Length,
            legacyEuler is not null,
            QuatSignature(legacyRead, legacyInterpolation, legacyKeys, legacyEuler, legacyPos),
            QuatSignature(viewRead, viewInterpolation, viewKeys, viewEuler, viewPos));
    }

    /// <summary>The translation reader (<c>TryReadVector3Keys</c>) from <paramref name="start" />.</summary>
    public static NifKeyGroupProjectionComparison Vector3(byte[] data, int start, int end, bool bigEndian)
    {
        var legacyPos = start;
        var legacyRead = NifKeyGroupReaderLegacyReference.TryReadVector3Keys(
            data, ref legacyPos, end, bigEndian, out var legacyInterpolation, out var legacyKeys);
        var viewPos = start;
        var viewRead = NifKeyGroupReader.TryReadVector3Keys(
            data, ref viewPos, end, bigEndian, out var viewInterpolation, out var viewKeys);
        return new NifKeyGroupProjectionComparison(
            legacyRead,
            legacyPos,
            legacyInterpolation,
            legacyKeys.Length,
            false,
            Vector3Signature(legacyRead, legacyInterpolation, legacyKeys, legacyPos),
            Vector3Signature(viewRead, viewInterpolation, viewKeys, viewPos));
    }

    /// <summary>The scalar reader (<c>TryReadFloatKeys</c>) from <paramref name="start" />.</summary>
    public static NifKeyGroupProjectionComparison Float(byte[] data, int start, int end, bool bigEndian)
    {
        var legacyPos = start;
        var legacyRead = NifKeyGroupReaderLegacyReference.TryReadFloatKeys(
            data, ref legacyPos, end, bigEndian, out var legacyInterpolation, out var legacyKeys);
        var viewPos = start;
        var viewRead = NifKeyGroupReader.TryReadFloatKeys(
            data, ref viewPos, end, bigEndian, out var viewInterpolation, out var viewKeys);
        return new NifKeyGroupProjectionComparison(
            legacyRead,
            legacyPos,
            legacyInterpolation,
            legacyKeys.Length,
            false,
            FloatSignature(legacyRead, legacyInterpolation, legacyKeys, legacyPos),
            FloatSignature(viewRead, viewInterpolation, viewKeys, viewPos));
    }

    /// <summary>
    ///     True for an XYZ-Euler block on which the engine's walk and the pre-view reader's nif.xml walk differ by design:
    ///     it stores more than one record, or the two legacy-word gates (nif.xml's Order float up to 10.1.0.0, the engine's
    ///     word below 10.1.0.104) disagree at <paramref name="binaryVersion" />. Blocks the cap or a short span refuses
    ///     before the walk are left to the legacy comparison, where both readers refuse them identically.
    /// </summary>
    private static bool TryGetDivergentEulerRecordCount(
        byte[] data, int start, int end, bool bigEndian, uint binaryVersion, out uint records)
    {
        records = 0;
        if (start < 0 || end - start < 8 || start + 8 > data.Length)
        {
            return false;
        }

        var count = ReadWord(data, start, bigEndian);
        if (ReadWord(data, start + 4, bigEndian) != 4 || count == 0 || count > KeyCap)
        {
            return false;
        }

        var nifXmlOrder = binaryVersion <= NifVersions.Gamebryo10100;
        var engineWord = binaryVersion < EngineLegacyWordEnd;
        records = count;
        return count > 1 || nifXmlOrder != engineWord;
    }

    /// <summary>
    ///     The rotation reader on a divergent XYZ-Euler block against the engine's walk composed from the pre-view
    ///     reader's float-group reader: the count and type words, then every record; the renderer must see the first
    ///     record's axis keys, no quaternion keys, and the cursor past the last record.
    /// </summary>
    private static NifKeyGroupProjectionComparison EngineEuler(
        byte[] data, int start, int end, bool bigEndian, uint binaryVersion, uint records)
    {
        var referencePos = start + 8;
        NifFloatKey[][]? first = null;
        var referenceRead = true;
        for (var record = 0u; referenceRead && record < records; record++)
        {
            referenceRead = TryReadComposedEulerRecord(data, ref referencePos, end, bigEndian, binaryVersion,
                out var axes);
            first ??= axes;
        }

        (NifFloatKey[] X, NifFloatKey[] Y, NifFloatKey[] Z)? referenceEuler =
            referenceRead && first is not null ? (first[0], first[1], first[2]) : null;

        var viewPos = start;
        var viewRead = NifKeyGroupReader.TryReadQuatKeys(
            data, ref viewPos, end, bigEndian, binaryVersion, out var viewInterpolation, out var viewKeys,
            out var viewEuler);
        return new NifKeyGroupProjectionComparison(
            referenceRead,
            referencePos,
            NifKeyInterpolation.XyzEuler,
            0,
            referenceEuler is not null,
            QuatSignature(referenceRead, NifKeyInterpolation.XyzEuler, [], referenceEuler, referencePos),
            QuatSignature(viewRead, viewInterpolation, viewKeys, viewEuler, viewPos),
            true);
    }

    /// <summary>One NiEulerRotKey record as the engine reads it, through the pre-view reader's float-group reader.</summary>
    private static bool TryReadComposedEulerRecord(
        byte[] data, ref int pos, int end, bool bigEndian, uint binaryVersion, out NifFloatKey[][]? axes)
    {
        axes = null;
        if (binaryVersion < EngineLegacyWordEnd)
        {
            if (pos + 4 > end)
            {
                return false;
            }

            pos += 4;
        }

        var read = new NifFloatKey[3][];
        for (var axis = 0; axis < 3; axis++)
        {
            if (!NifKeyGroupReaderLegacyReference.TryReadFloatKeys(data, ref pos, end, bigEndian, out _,
                    out var keys))
            {
                return false;
            }

            read[axis] = keys;
        }

        axes = read;
        return true;
    }

    private static uint ReadWord(byte[] data, int pos, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
    }

    private static string QuatSignature(
        bool read,
        NifKeyInterpolation interpolation,
        NifQuatKey[] keys,
        (NifFloatKey[] X, NifFloatKey[] Y, NifFloatKey[] Z)? euler,
        int pos)
    {
        var text = new StringBuilder();
        text.Append(read).Append(' ').Append(interpolation).Append(" q").Append(keys.Length).Append(':');
        foreach (var key in keys)
        {
            text.Append(' ').Append(Hex(key.Time)).Append(',').Append(Hex(key.Value.X)).Append(',')
                .Append(Hex(key.Value.Y)).Append(',').Append(Hex(key.Value.Z)).Append(',').Append(Hex(key.Value.W));
        }

        if (euler is { } axes)
        {
            AppendFloatKeys(text.Append(" ex"), axes.X);
            AppendFloatKeys(text.Append(" ey"), axes.Y);
            AppendFloatKeys(text.Append(" ez"), axes.Z);
        }
        else
        {
            text.Append(" no-euler");
        }

        return AppendPosition(text, read, pos);
    }

    private static string Vector3Signature(bool read, NifKeyInterpolation interpolation, NifVec3Key[] keys, int pos)
    {
        var text = new StringBuilder();
        text.Append(read).Append(' ').Append(interpolation).Append(" v").Append(keys.Length).Append(':');
        foreach (var key in keys)
        {
            text.Append(' ').Append(Hex(key.Time))
                .Append(',').Append(Hex(key.Value.X)).Append(',').Append(Hex(key.Value.Y)).Append(',')
                .Append(Hex(key.Value.Z))
                .Append(",f").Append(Hex(key.Forward.X)).Append(',').Append(Hex(key.Forward.Y)).Append(',')
                .Append(Hex(key.Forward.Z))
                .Append(",b").Append(Hex(key.Backward.X)).Append(',').Append(Hex(key.Backward.Y)).Append(',')
                .Append(Hex(key.Backward.Z))
                .Append(key.HasQuadraticTangents ? ",t" : ",n");
        }

        return AppendPosition(text, read, pos);
    }

    private static string FloatSignature(bool read, NifKeyInterpolation interpolation, NifFloatKey[] keys, int pos)
    {
        var text = new StringBuilder();
        text.Append(read).Append(' ').Append(interpolation);
        AppendFloatKeys(text.Append(" f"), keys);
        return AppendPosition(text, read, pos);
    }

    private static void AppendFloatKeys(StringBuilder text, NifFloatKey[] keys)
    {
        text.Append(keys.Length).Append(':');
        foreach (var key in keys)
        {
            text.Append(' ').Append(Hex(key.Time)).Append(',').Append(Hex(key.Value));
        }
    }

    private static string AppendPosition(StringBuilder text, bool read, int pos)
    {
        if (read)
        {
            text.Append(" pos ").Append(pos.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private static string Hex(float value)
    {
        return BitConverter.SingleToUInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture);
    }
}
