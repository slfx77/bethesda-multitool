using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Records;

/// <summary>
///     Validation and detection helpers for ESM record scanning.
///     Provides signature matching, false-positive filtering, and header validation
///     used by both the main scanner and correlator.
/// </summary>
internal static class RecordValidator
{
    /// <summary>
    ///     Every subrecord signature the schema registry knows about, used to reject heap garbage that
    ///     merely looks like a record header (see <see cref="HasPlausibleFirstSubrecord" />). Shared by the
    ///     main dump scanner and the gap-recovery scanner so both apply exactly one plausibility gate.
    /// </summary>
    private static readonly IReadOnlySet<string> KnownSubrecordSignatures =
        SubrecordSchemaRegistry.GetAllSignatures();

    #region Detection Helpers

    /// <summary>
    ///     Checks if the given offset falls within any excluded range (e.g., module memory).
    ///     Used to skip ESM detection inside executable module regions.
    /// </summary>
    internal static bool IsInExcludedRange(long offset, List<(long start, long end)>? ranges)
    {
        if (ranges == null || ranges.Count == 0)
        {
            return false;
        }

        foreach (var (start, end) in ranges)
        {
            if (offset >= start && offset < end)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Validates a record signature using the comprehensive MainRecordTypes dictionary.
    ///     This provides stricter validation than just checking if it's uppercase ASCII.
    /// </summary>
    internal static bool IsValidRecordSignature(string signature)
    {
        // Primary check: known record types from comprehensive EsmRecordTypes dictionary
        if (EsmRecordTypes.MainRecordTypes.ContainsKey(signature))
        {
            return true;
        }

        // Secondary: allow uppercase-only 4-char for potential unknown types
        // (memory dumps may have record types not in the PC version dictionary)
        return signature.Length == 4 && signature.All(c => c is >= 'A' and <= 'Z' or '_');
    }

    /// <summary>
    ///     Check if bytes match a texture signature (TX00-TX07).
    /// </summary>
    internal static bool MatchesTextureSignature(byte[] data, int i)
    {
        if (i + 4 > data.Length)
        {
            return false;
        }

        return data[i] == 'T' && data[i + 1] == 'X' && data[i + 2] == '0' &&
               data[i + 3] >= '0' && data[i + 3] <= '7';
    }

    internal static bool MatchesSignature(byte[] data, int i, ReadOnlySpan<byte> sig)
    {
        return data[i] == sig[0] && data[i + 1] == sig[1] && data[i + 2] == sig[2] && data[i + 3] == sig[3];
    }

    internal static bool IsRecordTypeMarker(byte[] data, int offset)
    {
        for (var b = 0; b < 4; b++)
        {
            if (!char.IsAsciiLetterOrDigit((char)data[offset + b]) && data[offset + b] != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Checks if the signature at the given offset matches a known false positive pattern.
    ///     GPU debug register dumps contain patterns like "VGT_DEBUG" that look like valid signatures.
    /// </summary>
    internal static bool IsKnownFalsePositive(byte[] data, int offset)
    {
        if (offset + 4 > data.Length)
        {
            return false;
        }

        // Check against known false positive patterns (both LE and BE byte orders)
        foreach (var pattern in RecordScannerDispatch.KnownFalsePositivePatterns)
        {
            // Check little-endian order (as stored in memory)
            if (data[offset] == pattern[0] && data[offset + 1] == pattern[1] &&
                data[offset + 2] == pattern[2] && data[offset + 3] == pattern[3])
            {
                return true;
            }

            // Check big-endian (reversed) order for Xbox 360
            if (data[offset + 3] == pattern[0] && data[offset + 2] == pattern[1] &&
                data[offset + 1] == pattern[2] && data[offset] == pattern[3])
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Main Record Header Validation

    internal static bool IsValidMainRecordHeader(string recordType, uint dataSize, uint flags, uint formId)
    {
        // Validate record type using comprehensive MainRecordTypes dictionary
        // This provides stricter validation than just checking if it's uppercase ASCII
        if (!IsValidRecordSignature(recordType))
        {
            return false;
        }

        // Validate data size (reasonable range for game records)
        // Most records are under 100KB, very few exceed 1MB
        if (dataSize == 0 || dataSize > 10_000_000)
        {
            return false;
        }

        // Validate flags (common valid flags, reject obviously bad values)
        // Upper bits should not be set for most valid records
        if ((flags & 0xFFF00000) != 0 && (flags & 0x00040000) == 0) // Allow compressed flag
        {
            return false;
        }

        // Validate FormID
        // Plugin index should be 0x00-0xFF (usually 0x00-0x0F for base game)
        // FormID should not be 0 or 0xFFFFFFFF
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return false;
        }

        // False positive prevention: check if FormID bytes are all printable ASCII
        // This indicates we're inside string data (e.g., "PrisonerSandBoxPACKAGE" triggering PACK detection)
        // Real FormIDs have structured values like 0x00XXXXXX with plugin index as first byte
        if (IsFormIdAllPrintableAscii(formId))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Check if a FormID value consists entirely of printable ASCII characters.
    ///     This indicates we're likely inside string data, not a real record header.
    /// </summary>
    internal static bool IsFormIdAllPrintableAscii(uint formId)
    {
        var b0 = (byte)(formId & 0xFF);
        var b1 = (byte)((formId >> 8) & 0xFF);
        var b2 = (byte)((formId >> 16) & 0xFF);
        var b3 = (byte)((formId >> 24) & 0xFF);

        return IsPrintableAscii(b0) && IsPrintableAscii(b1) &&
               IsPrintableAscii(b2) && IsPrintableAscii(b3);
    }

    internal static bool IsPrintableAscii(byte b)
    {
        return b >= 0x20 && b < 0x7F;
    }

    /// <summary>
    ///     True when the bytes immediately after a candidate record header begin with a recognized
    ///     subrecord signature (or the XXXX extended-size marker). Compressed records carry a zlib
    ///     payload rather than a plain signature, so they are accepted without the probe; a truncated
    ///     tail with no room to read is likewise accepted (it cannot be disproven). Rejecting a header
    ///     whose data does NOT start with a known subrecord filters heap garbage that satisfies the
    ///     scalar header gates (signature/size/flags/FormID) but is not a real record.
    /// </summary>
    /// <param name="data">Buffer containing the candidate header.</param>
    /// <param name="i">Offset of the record header within <paramref name="data" />.</param>
    /// <param name="headerSize">Record header size (20 for Oblivion TES4, 24 for FO3/FNV/Skyrim+).</param>
    /// <param name="dataLength">Length of valid bytes in <paramref name="data" /> (bounds guard).</param>
    /// <param name="isBigEndian">True for Xbox 360 big-endian records (signature byte-reversed).</param>
    /// <param name="flags">Record flags (the compressed flag exempts the record from the probe).</param>
    internal static bool HasPlausibleFirstSubrecord(
        byte[] data, int i, int headerSize, int dataLength, bool isBigEndian, uint flags)
    {
        const uint compressedFlag = 0x00040000;
        if ((flags & compressedFlag) != 0)
        {
            return true;
        }

        var subOffset = i + headerSize;
        if (subOffset + 4 > dataLength)
        {
            return true;
        }

        var sig = isBigEndian
            ? new string([
                (char)data[subOffset + 3], (char)data[subOffset + 2],
                (char)data[subOffset + 1], (char)data[subOffset]
            ])
            : Encoding.ASCII.GetString(data, subOffset, 4);

        // XXXX carries the true 4-byte size of an oversized following subrecord and can legitimately
        // lead a record's data, so accept it alongside the registered signatures.
        return sig == "XXXX" || KnownSubrecordSignatures.Contains(sig);
    }

    /// <summary>
    ///     DataSize above which a header must prove its extent before the scanner may trust it.
    ///     Retail FNV's largest genuine records are NAVI (622 KB) and WRLD (177 KB, XXXX-escaped
    ///     OFST); everything else is ≤ 41 KB. A torn header with a scribbled DataSize under the
    ///     10 MB scalar cap otherwise blinds the skip-ahead scanner to megabytes of real records
    ///     (xex44: a stale REFR at 0x5BD090F claiming 4.5 MB swallowed the 4.4 MB chunk tail —
    ///     an entire cell-children GRUP, undetected, silently).
    /// </summary>
    private const int LargeRecordVerifyThreshold = 64 * 1024;

    /// <summary>
    ///     True when a candidate header's DataSize can be trusted for skip-ahead. Small records
    ///     pass unchecked (a bogus small skip costs nothing). Large records must prove their
    ///     extent: compressed via the zlib stream header, uncompressed via the subrecord chain
    ///     tiling the payload exactly (XXXX extended-size escapes honored). Verification stops at
    ///     the buffer end — a genuine large record straddling a chunk boundary validates on what
    ///     is visible instead of being rejected for what is not.
    /// </summary>
    internal static bool HasTrustworthyExtent(
        byte[] data, int i, int headerSize, int dataLength, bool isBigEndian, uint flags, uint dataSize)
    {
        if (dataSize <= LargeRecordVerifyThreshold)
        {
            return true;
        }

        const uint compressedFlag = 0x00040000;
        var payloadStart = i + headerSize;
        if ((flags & compressedFlag) != 0)
        {
            // Compressed payload = 4-byte decompressed size, then a zlib stream. RFC 1950:
            // CMF low nibble 8 (deflate) and (CMF<<8 | FLG) divisible by 31. A large
            // compressed claim whose payload lacks the stream header is a torn fragment.
            if (payloadStart + 6 > dataLength)
            {
                return false;
            }

            var cmf = data[payloadStart + 4];
            var flg = data[payloadStart + 5];
            return (cmf & 0x0F) == 8 && ((cmf << 8) | flg) % 31 == 0;
        }

        // Uncompressed: the subrecord chain must tile the payload. Garbage after a torn
        // header misaligns within a few steps; a genuine NAVI/WRLD walks clean.
        var end = payloadStart + dataSize;
        var verifiableEnd = Math.Min(end, dataLength);
        long pos = payloadStart;
        var xxxxOverride = 0u;
        while (pos < verifiableEnd)
        {
            if (pos + 6 > verifiableEnd)
            {
                // A header fragment at the buffer edge is unprovable either way; accept
                // only when the record genuinely extends past the buffer.
                return end > dataLength;
            }

            for (var b = 0; b < 4; b++)
            {
                var c = data[pos + b];
                if (c is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_'))
                {
                    return false;
                }
            }

            // "XXXX" is a palindrome — no endian branch needed for the signature itself.
            var isXxxx = data[pos] == 'X' && data[pos + 1] == 'X'
                                          && data[pos + 2] == 'X' && data[pos + 3] == 'X';
            var subLen = isBigEndian
                ? (uint)((data[pos + 4] << 8) | data[pos + 5])
                : (uint)(data[pos + 4] | (data[pos + 5] << 8));

            if (xxxxOverride != 0)
            {
                // The subrecord after an XXXX escape declares length 0; its true size is
                // the escape's payload.
                subLen = xxxxOverride;
                xxxxOverride = 0;
            }
            else if (isXxxx && subLen == 4 && pos + 10 <= verifiableEnd)
            {
                xxxxOverride = isBigEndian
                    ? BinaryUtils.ReadUInt32BE(data, (int)(pos + 6))
                    : BinaryUtils.ReadUInt32LE(data, (int)(pos + 6));
            }

            pos += 6 + subLen;
        }

        // In-buffer records must land exactly on the declared end; a straddling record is
        // accepted once every visible subrecord tiled cleanly.
        return end > dataLength || pos == end;
    }

    #endregion
}
