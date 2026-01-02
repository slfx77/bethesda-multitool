using System.Globalization;
using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for Bethesda ESM/ESP record data fragments in memory dumps.
///     Extracts GMST (game settings), EDID (editor IDs), SCTX (script source), and SCRO (FormID references).
/// </summary>
public static class EsmRecordParser
{
    /// <summary>
    ///     Extracted ESM records from a memory dump.
    /// </summary>
    public record EsmRecordScanResult
    {
        public List<GmstRecord> GameSettings { get; init; } = [];
        public List<EdidRecord> EditorIds { get; init; } = [];
        public List<SctxRecord> ScriptSources { get; init; } = [];
        public List<ScroRecord> FormIdReferences { get; init; } = [];
    }

    public record GmstRecord(string Name, long Offset, int Length);
    public record EdidRecord(string Name, long Offset);
    public record SctxRecord(string Text, long Offset, int Length);
    public record ScroRecord(uint FormId, long Offset);

    /// <summary>
    ///     Scan a memory dump for all ESM record fragments.
    /// </summary>
    public static EsmRecordScanResult ScanForRecords(byte[] data)
    {
        var result = new EsmRecordScanResult();
        var seenEdids = new HashSet<string>();
        var seenFormIds = new HashSet<uint>();

        for (int i = 0; i <= data.Length - 8; i++)
        {
            // Check for EDID records
            if (MatchesEdidSignature(data, i))
            {
                TryAddEdidRecord(data, i, result.EditorIds, seenEdids);
            }
            // Check for GMST records
            else if (MatchesGmstSignature(data, i))
            {
                TryAddGmstRecord(data, i, result.GameSettings);
            }
            // Check for SCTX records
            else if (MatchesSctxSignature(data, i))
            {
                TryAddSctxRecord(data, i, result.ScriptSources);
            }
            // Check for SCRO records
            else if (MatchesScroSignature(data, i))
            {
                TryAddScroRecord(data, i, result.FormIdReferences, seenFormIds);
            }
        }

        return result;
    }

    private static bool MatchesEdidSignature(byte[] data, int i) =>
        data[i] == 'E' && data[i + 1] == 'D' && data[i + 2] == 'I' && data[i + 3] == 'D';

    private static bool MatchesGmstSignature(byte[] data, int i) =>
        data[i] == 'G' && data[i + 1] == 'M' && data[i + 2] == 'S' && data[i + 3] == 'T';

    private static bool MatchesSctxSignature(byte[] data, int i) =>
        data[i] == 'S' && data[i + 1] == 'C' && data[i + 2] == 'T' && data[i + 3] == 'X';

    private static bool MatchesScroSignature(byte[] data, int i) =>
        data[i] == 'S' && data[i + 1] == 'C' && data[i + 2] == 'R' && data[i + 3] == 'O';

    private static void TryAddEdidRecord(byte[] data, int i, List<EdidRecord> records, HashSet<string> seen)
    {
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 256 || i + 6 + len > data.Length)
        {
            return;
        }

        var name = ReadNullTermString(data, i + 6, len);
        if (IsValidEditorId(name) && seen.Add(name))
        {
            records.Add(new EdidRecord(name, i));
        }
    }

    private static void TryAddGmstRecord(byte[] data, int i, List<GmstRecord> records)
    {
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 512 || i + 6 + len > data.Length)
        {
            return;
        }

        var name = ReadNullTermString(data, i + 6, len);
        if (IsValidSettingName(name))
        {
            records.Add(new GmstRecord(name, i, len));
        }
    }

    private static void TryAddSctxRecord(byte[] data, int i, List<SctxRecord> records)
    {
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len <= 10 || len >= 65535 || i + 6 + len > data.Length)
        {
            return;
        }

        var text = Encoding.ASCII.GetString(data, i + 6, len).TrimEnd('\0');
        if (text.Length > 5 && ContainsScriptKeywords(text))
        {
            records.Add(new SctxRecord(text, i, len));
        }
    }

    private static void TryAddScroRecord(byte[] data, int i, List<ScroRecord> records, HashSet<uint> seen)
    {
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 4 || i + 10 > data.Length)
        {
            return;
        }

        var formId = BinaryUtils.ReadUInt32LE(data, i + 6);

        // Valid FormIDs have mod index in high byte (0x00-0x0F for base game)
        if (formId == 0 || formId == 0xFFFFFFFF || (formId >> 24) > 0x0F)
        {
            return;
        }

        if (seen.Add(formId))
        {
            records.Add(new ScroRecord(formId, i));
        }
    }

    /// <summary>
    ///     Correlate FormIDs with their editor ID names.
    /// </summary>
    public static Dictionary<uint, string> CorrelateFormIdsToNames(byte[] data, EsmRecordScanResult? existingScan = null)
    {
        var scan = existingScan ?? ScanForRecords(data);
        var correlations = new Dictionary<uint, string>();

        // Look for ESM record headers that contain both EDID and FormID
        foreach (var edid in scan.EditorIds)
        {
            // Look backwards from EDID for a record header containing FormID
            var formId = FindRecordFormId(data, (int)edid.Offset);
            if (formId != 0 && !correlations.ContainsKey(formId))
            {
                correlations[formId] = edid.Name;
            }
        }

        return correlations;
    }

    private static uint FindRecordFormId(byte[] data, int edidOffset)
    {
        // ESM record structure: Type(4) + Size(4) + Flags(4) + FormID(4) + ... subrecords
        // The FormID is at offset +12 from the record start
        // Search backwards for a record header

        var searchStart = Math.Max(0, edidOffset - 200);
        for (int checkOffset = edidOffset - 4; checkOffset >= searchStart; checkOffset--)
        {
            var formId = TryExtractFormIdFromRecordHeader(data, checkOffset, edidOffset);
            if (formId != 0)
            {
                return formId;
            }
        }

        return 0;
    }

    private static uint TryExtractFormIdFromRecordHeader(byte[] data, int checkOffset, int edidOffset)
    {
        if (checkOffset + 24 >= data.Length)
        {
            return 0;
        }

        // Check if this looks like a record type (4 ASCII letters)
        if (!IsRecordTypeMarker(data, checkOffset))
        {
            return 0;
        }

        var formId = BinaryUtils.ReadUInt32LE(data, checkOffset + 12);

        // Validate FormID
        if (formId == 0 || formId == 0xFFFFFFFF || (formId >> 24) > 0x0F)
        {
            return 0;
        }

        var size = BinaryUtils.ReadUInt32LE(data, checkOffset + 4);

        // Check if EDID is within this record's data
        if (size > 0 && size < 10_000_000 && edidOffset < checkOffset + 24 + size)
        {
            return formId;
        }

        return 0;
    }

    private static bool IsRecordTypeMarker(byte[] data, int offset)
    {
        for (int b = 0; b < 4; b++)
        {
            if (!char.IsAsciiLetterOrDigit((char)data[offset + b]) && data[offset + b] != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadNullTermString(byte[] data, int offset, int maxLen)
    {
        int end = offset;
        while (end < offset + maxLen && end < data.Length && data[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    private static bool IsValidEditorId(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2 || name.Length > 200)
        {
            return false;
        }

        if (!char.IsLetter(name[0]))
        {
            return false;
        }

        int validChars = name.Count(c => char.IsLetterOrDigit(c) || c == '_');
        return validChars >= name.Length * 0.9;
    }

    private static bool IsValidSettingName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2)
        {
            return false;
        }

        // GMST names typically start with f, i, s, b (float, int, string, bool)
        var firstChar = char.ToLower(name[0], CultureInfo.InvariantCulture);
        return firstChar is 'f' or 'i' or 's' or 'b' && name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static bool ContainsScriptKeywords(string text)
    {
        // Check for common script keywords
        return text.Contains("Enable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Disable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("MoveTo", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("SetStage", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("GetStage", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("if ", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("endif", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("REF", StringComparison.OrdinalIgnoreCase);
    }
}
