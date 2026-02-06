using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class EsmRecordFormat
{
    #region Exclusion Range Check

    /// <summary>
    ///     Checks if the given offset falls within any excluded range (e.g., module memory).
    ///     Used to skip ESM detection inside executable module regions.
    /// </summary>
    private static bool IsInExcludedRange(long offset, List<(long start, long end)>? ranges)
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

    #endregion

    #region Signature Validation

    /// <summary>
    ///     Validates a record signature using the comprehensive MainRecordTypes dictionary.
    ///     This provides stricter validation than just checking if it's uppercase ASCII.
    /// </summary>
    private static bool IsValidRecordSignature(string signature)
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
    private static bool MatchesTextureSignature(byte[] data, int i)
    {
        if (i + 4 > data.Length)
        {
            return false;
        }

        return data[i] == 'T' && data[i + 1] == 'X' && data[i + 2] == '0' &&
               data[i + 3] >= '0' && data[i + 3] <= '7';
    }

    private static bool MatchesSignature(byte[] data, int i, ReadOnlySpan<byte> sig)
    {
        return data[i] == sig[0] && data[i + 1] == sig[1] && data[i + 2] == sig[2] && data[i + 3] == sig[3];
    }

    private static bool IsRecordTypeMarker(byte[] data, int offset)
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
    private static bool IsKnownFalsePositive(byte[] data, int offset)
    {
        if (offset + 4 > data.Length)
        {
            return false;
        }

        // Check against known false positive patterns (both LE and BE byte orders)
        foreach (var pattern in KnownFalsePositivePatterns)
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

    private static bool IsValidMainRecordHeader(string recordType, uint dataSize, uint flags, uint formId)
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

        // Plugin index validation (relaxed - allow any valid index)
        var pluginIndex = formId >> 24;
        if (pluginIndex > 0xFF)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Check if a FormID value consists entirely of printable ASCII characters.
    ///     This indicates we're likely inside string data, not a real record header.
    /// </summary>
    private static bool IsFormIdAllPrintableAscii(uint formId)
    {
        var b0 = (byte)(formId & 0xFF);
        var b1 = (byte)((formId >> 8) & 0xFF);
        var b2 = (byte)((formId >> 16) & 0xFF);
        var b3 = (byte)((formId >> 24) & 0xFF);

        return IsPrintableAscii(b0) && IsPrintableAscii(b1) &&
               IsPrintableAscii(b2) && IsPrintableAscii(b3);
    }

    private static bool IsPrintableAscii(byte b)
    {
        return b >= 0x20 && b < 0x7F;
    }

    #endregion

    #region FormType Detection

    /// <summary>
    ///     Detect the runtime FormType value for INFO records by matching EditorID naming
    ///     conventions. The FormType enum shifts between game builds, so we calibrate from
    ///     actual data rather than using hardcoded values.
    /// </summary>
    private static byte? DetectInfoFormType(List<RuntimeEditorIdEntry> entries, int startIndex)
    {
        // INFO EditorIDs in Fallout: New Vegas reliably contain "Topic"
        // (e.g., aBHTopicAgree, VDialogueDocMitchellTopic001)
        var formTypeCounts = new Dictionary<byte, int>();
        for (var i = startIndex; i < entries.Count; i++)
        {
            if (entries[i].EditorId.Contains("Topic", StringComparison.OrdinalIgnoreCase))
            {
                formTypeCounts.TryGetValue(entries[i].FormType, out var count);
                formTypeCounts[entries[i].FormType] = count + 1;
            }
        }

        if (formTypeCounts.Count == 0)
        {
            return null;
        }

        // Return the FormType with the most Topic matches (require at least 5)
        var best = formTypeCounts.MaxBy(kv => kv.Value);
        return best.Value >= 5 ? best.Key : null;
    }

    #endregion
}
