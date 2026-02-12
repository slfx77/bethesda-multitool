using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Detects and parses miscellaneous ESM subrecord types: EDID, text (FULL/DESC),
///     paths (MODL/ICON/TX00-TX07), GMST, SCTX, SCRO, NAME/FormID references,
///     and generic subrecords from byte arrays during memory dump scanning.
/// </summary>
internal static class EsmMiscDetector
{
    #region Schema Signatures

    private static readonly Lazy<HashSet<string>> SchemaSignaturesLE =
        new(() => SubrecordSchemaRegistry.GetAllSignatures().ToHashSet());

    private static readonly Lazy<HashSet<string>> SchemaSignaturesBE = new(() => SubrecordSchemaRegistry
        .GetAllSignatures()
        .Select(SubrecordSchemaRegistry.GetReversedSignature)
        .ToHashSet());

    #endregion

    #region EDID

    internal static void TryAddEdidRecord(byte[] data, int i, int dataLength, List<EdidRecord> records,
        HashSet<string> seen)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 256 || i + 6 + len > dataLength)
        {
            return;
        }

        var name = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (IsValidEditorId(name) && seen.Add(name))
        {
            records.Add(new EdidRecord(name, i));
        }
    }

    internal static void TryAddEdidRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<EdidRecord> records, HashSet<string> seen)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 256 || i + 6 + len > dataLength)
        {
            return;
        }

        var name = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (IsValidEditorId(name) && seen.Add(name))
        {
            records.Add(new EdidRecord(name, baseOffset + i));
        }
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

        // Require 100% valid characters (alphanumeric + underscore)
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        // Reject repeated-pattern junk (e.g., "katSkatSkatS...")
        if (name.Length >= 8 && HasRepeatedPattern(name))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Detect repeated substring patterns (e.g., "katSkatSkatS" repeats "katS").
    /// </summary>
    private static bool HasRepeatedPattern(string s)
    {
        // Check for patterns of length 2-6 that repeat 3+ times
        for (var patLen = 2; patLen <= Math.Min(6, s.Length / 3); patLen++)
        {
            var pattern = s[..patLen];
            var repeatCount = 0;
            for (var i = 0; i + patLen <= s.Length; i += patLen)
            {
                if (s.AsSpan(i, patLen).SequenceEqual(pattern))
                {
                    repeatCount++;
                }
                else
                {
                    break;
                }
            }

            if (repeatCount >= 3)
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Text Subrecords (FULL, DESC)

    internal static void TryAddTextSubrecord(byte[] data, int i, int dataLength,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = EsmSubrecordUtils.GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 512 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (IsValidDisplayText(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, i));
        }
    }

    internal static void TryAddTextSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = EsmSubrecordUtils.GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 512 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (IsValidDisplayText(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, baseOffset + i));
        }
    }

    private static bool IsValidDisplayText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 2)
        {
            return false;
        }

        // Should be mostly printable characters
        var printable = text.Count(c => c >= 32 && c < 127);
        return (double)printable / text.Length >= 0.8;
    }

    #endregion

    #region Path Subrecords (MODL, ICON, TX00-TX07)

    internal static void TryAddPathSubrecord(byte[] data, int i, int dataLength,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = EsmSubrecordUtils.GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 260 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (IsValidPath(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, i));
        }
    }

    internal static void TryAddPathSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = EsmSubrecordUtils.GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 260 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (IsValidPath(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, baseOffset + i));
        }
    }

    private static bool IsValidPath(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 4)
        {
            return false;
        }

        // Should look like a file path (contains \ or / and an extension)
        return (text.Contains('\\') || text.Contains('/')) &&
               text.Contains('.') &&
               !text.Contains('\0');
    }

    #endregion

    #region GMST

    internal static void TryAddGmstRecord(byte[] data, int i, int dataLength, List<GmstRecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 256 || i + 6 + len > dataLength)
        {
            return;
        }

        var name = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (IsValidSettingName(name))
        {
            records.Add(new GmstRecord(name, i, len));
        }
    }

    internal static void TryAddGmstRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<GmstRecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 256 || i + 6 + len > dataLength)
        {
            return;
        }

        var name = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (IsValidSettingName(name))
        {
            records.Add(new GmstRecord(name, baseOffset + i, len));
        }
    }

    private static bool IsValidSettingName(string name)
    {
        // Game settings start with a type prefix: f (float), i (int), s (string), b (bool)
        if (string.IsNullOrEmpty(name) || name.Length < 2)
        {
            return false;
        }

        var firstChar = char.ToLowerInvariant(name[0]);
        return firstChar is 'f' or 'i' or 's' or 'b';
    }

    #endregion

    #region SCTX

    internal static void TryAddSctxRecord(byte[] data, int i, int dataLength, List<SctxRecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = EsmSubrecordUtils.GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 65535 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (ContainsScriptKeywords(text))
        {
            records.Add(new SctxRecord(text, i, len));
        }
    }

    internal static void TryAddSctxRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<SctxRecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = EsmSubrecordUtils.GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 65535 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (ContainsScriptKeywords(text))
        {
            records.Add(new SctxRecord(text, baseOffset + i, len));
        }
    }

    private static bool ContainsScriptKeywords(string text)
    {
        return text.Contains("Enable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Disable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("MoveTo", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("SetStage", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("GetStage", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("if ", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("endif", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("REF", StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region SCRO

    internal static void TryAddScroRecord(byte[] data, int i, int dataLength, List<ScroRecord> records,
        HashSet<uint> seen)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 4)
        {
            return;
        }

        var formId = EsmSubrecordUtils.GetFormId(data, i + 6);
        if (EsmSubrecordUtils.IsValidFormId(formId) && seen.Add(formId))
        {
            records.Add(new ScroRecord(formId, i));
        }
    }

    internal static void TryAddScroRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ScroRecord> records, HashSet<uint> seen)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 4)
        {
            return;
        }

        var formId = EsmSubrecordUtils.GetFormId(data, i + 6);
        if (EsmSubrecordUtils.IsValidFormId(formId) && seen.Add(formId))
        {
            records.Add(new ScroRecord(formId, baseOffset + i));
        }
    }

    #endregion

    #region NAME and FormID Subrecords

    internal static void TryAddNameSubrecord(byte[] data, int i, int dataLength, List<NameSubrecord> records)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 4)
        {
            return;
        }

        // Try little-endian first
        var formId = BinaryUtils.ReadUInt32LE(data, i + 6);
        if (EsmSubrecordUtils.IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, i, false));
            return;
        }

        // Try big-endian
        formId = BinaryUtils.ReadUInt32BE(data, i + 6);
        if (EsmSubrecordUtils.IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, i, true));
        }
    }

    internal static void TryAddNameSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<NameSubrecord> records)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 4)
        {
            return;
        }

        var formId = BinaryUtils.ReadUInt32LE(data, i + 6);
        if (EsmSubrecordUtils.IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, baseOffset + i, false));
            return;
        }

        formId = BinaryUtils.ReadUInt32BE(data, i + 6);
        if (EsmSubrecordUtils.IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, baseOffset + i, true));
        }
    }

    internal static void TryAddFormIdSubrecord(byte[] data, int i, int dataLength,
        string subrecordType, List<FormIdSubrecord> records)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = EsmSubrecordUtils.GetSubrecordLength(data, i + 4, 4);
        if (len != 4)
        {
            return;
        }

        var formId = EsmSubrecordUtils.GetFormId(data, i + 6);
        if (EsmSubrecordUtils.IsValidFormId(formId))
        {
            records.Add(new FormIdSubrecord(subrecordType, formId, i));
        }
    }

    internal static void TryAddFormIdSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        string subrecordType, List<FormIdSubrecord> records)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = EsmSubrecordUtils.GetSubrecordLength(data, i + 4, 4);
        if (len != 4)
        {
            return;
        }

        var formId = EsmSubrecordUtils.GetFormId(data, i + 6);
        if (EsmSubrecordUtils.IsValidFormId(formId))
        {
            records.Add(new FormIdSubrecord(subrecordType, formId, baseOffset + i));
        }
    }

    #endregion

    #region Generic Subrecords

    internal static void TryAddGenericSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<DetectedSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        // Check if signature looks valid (4 uppercase ASCII letters or underscore)
        for (var j = 0; j < 4; j++)
        {
            var c = data[i + j];
            if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))
            {
                return;
            }
        }

        var sig = Encoding.ASCII.GetString(data, i, 4);
        var len = EsmSubrecordUtils.GetSubrecordLength(data, i + 4, dataLength - i - 6);

        if (len == 0 || len > 65535 || i + 6 + len > dataLength)
        {
            return;
        }

        // Only add if it's a known subrecord signature from the schema
        if (SchemaSignaturesLE.Value.Contains(sig) ||
            SchemaSignaturesBE.Value.Contains(sig))
        {
            records.Add(new DetectedSubrecord { Signature = sig, DataSize = len, Offset = baseOffset + i });
        }
    }

    #endregion
}
