using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class EsmRecordFormat
{
    #region String Reading

    /// <summary>
    ///     Read a null-terminated string from a byte array.
    ///     Uses the shared utility from EsmStringUtils.
    /// </summary>
    private static string ReadNullTermString(byte[] data, int offset, int maxLen)
    {
        return EsmStringUtils.ReadNullTermString(data, offset, maxLen);
    }

    /// <summary>
    ///     Read a BSStringT&lt;char&gt; string from a TESForm object in the dump.
    ///     BSStringT layout (8 bytes, big-endian on Xbox 360):
    ///     Offset 0: pString (char* pointer, 4 bytes BE)
    ///     Offset 4: sLen (uint16 BE)
    /// </summary>
    private static string? ReadBSStringT(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        long tesFormFileOffset,
        int fieldOffset)
    {
        var bstOffset = tesFormFileOffset + fieldOffset;
        if (bstOffset + 8 > fileSize)
        {
            return null;
        }

        var bstBuffer = new byte[8];
        accessor.ReadArray(bstOffset, bstBuffer, 0, 8);

        var pString = BinaryUtils.ReadUInt32BE(bstBuffer);
        var sLen = BinaryUtils.ReadUInt16BE(bstBuffer, 4);

        if (pString == 0 || sLen == 0 || sLen > 4096)
        {
            return null;
        }

        if (!IsValidPointerInDump(pString, minidumpInfo))
        {
            return null;
        }

        var strFileOffset = minidumpInfo.VirtualAddressToFileOffset(Xbox360VaToLong(pString));
        if (!strFileOffset.HasValue || strFileOffset.Value + sLen > fileSize)
        {
            return null;
        }

        var strBuffer = new byte[sLen];
        accessor.ReadArray(strFileOffset.Value, strBuffer, 0, sLen);

        // Validate: should be mostly printable ASCII
        var printable = 0;
        for (var i = 0; i < sLen; i++)
        {
            var c = strBuffer[i];
            if ((c >= 32 && c <= 126) || c == '\n' || c == '\r' || c == '\t')
            {
                printable++;
            }
        }

        if (printable < sLen * 0.8)
        {
            return null;
        }

        return Encoding.ASCII.GetString(strBuffer, 0, sLen);
    }

    #endregion

    #region Text Subrecords (FULL, DESC)

    /// <summary>
    ///     Add a text subrecord (FULL, DESC, etc.) - legacy version.
    /// </summary>
    private static void TryAddTextSubrecord(byte[] data, int i, int dataLength,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 512 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = ReadNullTermString(data, i + 6, len);
        if (IsValidDisplayText(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, i));
        }
    }

    /// <summary>
    ///     Add a text subrecord with offset.
    /// </summary>
    private static void TryAddTextSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 512 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = ReadNullTermString(data, i + 6, len);
        if (IsValidDisplayText(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, baseOffset + i));
        }
    }

    /// <summary>
    ///     Validate display text (FULL, DESC).
    /// </summary>
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

    #region EditorID Subrecords (EDID)

    private static void TryAddEdidRecord(byte[] data, int i, int dataLength, List<EdidRecord> records,
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

        var name = ReadNullTermString(data, i + 6, len);
        if (IsValidEditorId(name) && seen.Add(name))
        {
            records.Add(new EdidRecord(name, i));
        }
    }

    private static void TryAddEdidRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
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

        var name = ReadNullTermString(data, i + 6, len);
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

    #region Path Subrecords (MODL, ICON, TX00-TX07)

    /// <summary>
    ///     Add a path subrecord (MODL, ICON, TX00, etc.) - legacy version.
    /// </summary>
    private static void TryAddPathSubrecord(byte[] data, int i, int dataLength,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 260 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = ReadNullTermString(data, i + 6, len);
        if (IsValidPath(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, i));
        }
    }

    private static void TryAddPathSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 260 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = ReadNullTermString(data, i + 6, len);
        if (IsValidPath(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, baseOffset + i));
        }
    }

    /// <summary>
    ///     Validate file path (MODL, ICON, TX00).
    /// </summary>
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
}
