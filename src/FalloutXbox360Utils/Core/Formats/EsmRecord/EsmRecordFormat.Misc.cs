using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

public sealed partial class EsmRecordFormat
{
    #region Generic Subrecords

    private static void TryAddGenericSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
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
        var len = GetSubrecordLength(data, i + 4, dataLength - i - 6);

        if (len == 0 || len > 65535 || i + 6 + len > dataLength)
        {
            return;
        }

        // Only add if it's a known subrecord signature from the schema
        if (SchemaSignaturesLE.Value.Contains(sig) || SchemaSignaturesBE.Value.Contains(sig))
        {
            records.Add(new DetectedSubrecord { Signature = sig, DataSize = len, Offset = baseOffset + i });
        }
    }

    #endregion

    #region Asset String Scanning

    /// <summary>
    ///     Scan for asset strings in the memory dump.
    ///     This method scans for model paths, texture paths, and other asset references.
    /// </summary>
    public static void ScanForAssetStrings(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult result,
        bool verbose)
    {
        // Implementation stub - the original implementation was lost during consolidation.
        // This scans for strings that look like asset paths (e.g., "meshes/", "textures/").
#pragma warning disable S1135 // Valid TODO for future improvement
        // TODO: Restore full implementation from version control if needed.
#pragma warning restore S1135

        const int bufferSize = 64 * 1024;
        var buffer = new byte[bufferSize];
        var assetStrings = result.AssetStrings;

        for (long offset = 0; offset < fileSize; offset += bufferSize - 256)
        {
            var bytesToRead = (int)Math.Min(bufferSize, fileSize - offset);
            if (bytesToRead < 16)
            {
                break;
            }

            accessor.ReadArray(offset, buffer, 0, bytesToRead);

#pragma warning disable S127 // Loop counter modified in body - intentional skip-ahead in binary parsing
            for (var i = 0; i < bytesToRead - 16; i++)
            {
                // Look for common asset path prefixes
                if (!IsPathStartChar(buffer[i]))
                {
                    continue;
                }

                var endPos = FindStringEnd(buffer, i, Math.Min(i + 256, bytesToRead));
                if (endPos <= i || endPos - i < 8)
                {
                    continue;
                }

                var text = Encoding.ASCII.GetString(buffer, i, endPos - i);
                if (IsValidPath(text))
                {
                    assetStrings.Add(new DetectedAssetString
                    {
                        Path = CleanAssetPath(text),
                        Offset = offset + i
                    });
                    i = endPos;
                }
            }
#pragma warning restore S127
        }
    }

    #endregion

    #region Game Setting Subrecords (GMST)

    private static void TryAddGmstRecord(byte[] data, int i, int dataLength, List<GmstRecord> records)
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
        if (IsValidSettingName(name))
        {
            records.Add(new GmstRecord(name, i, len));
        }
    }

    private static void TryAddGmstRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
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

        var name = ReadNullTermString(data, i + 6, len);
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

    #region Script Subrecords (SCTX, SCRO)

    private static void TryAddSctxRecord(byte[] data, int i, int dataLength, List<SctxRecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 65535 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = ReadNullTermString(data, i + 6, len);
        if (ContainsScriptKeywords(text))
        {
            records.Add(new SctxRecord(text, i, len));
        }
    }

    private static void TryAddSctxRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<SctxRecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 65535 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = ReadNullTermString(data, i + 6, len);
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

    private static void TryAddScroRecord(byte[] data, int i, int dataLength, List<ScroRecord> records,
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

        var formId = GetFormId(data, i + 6);
        if (IsValidFormId(formId) && seen.Add(formId))
        {
            records.Add(new ScroRecord(formId, i));
        }
    }

    private static void TryAddScroRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
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

        var formId = GetFormId(data, i + 6);
        if (IsValidFormId(formId) && seen.Add(formId))
        {
            records.Add(new ScroRecord(formId, baseOffset + i));
        }
    }

    #endregion

    #region NAME and FormID Subrecords

    private static void TryAddNameSubrecord(byte[] data, int i, int dataLength, List<NameSubrecord> records)
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
        if (IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, i, false));
            return;
        }

        // Try big-endian
        formId = BinaryUtils.ReadUInt32BE(data, i + 6);
        if (IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, i, true));
        }
    }

    private static void TryAddNameSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
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
        if (IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, baseOffset + i, false));
            return;
        }

        formId = BinaryUtils.ReadUInt32BE(data, i + 6);
        if (IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, baseOffset + i, true));
        }
    }

    /// <summary>
    ///     Add a FormID reference subrecord - legacy version.
    /// </summary>
    private static void TryAddFormIdSubrecord(byte[] data, int i, int dataLength,
        string subrecordType, List<FormIdSubrecord> records)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, 4);
        if (len != 4)
        {
            return;
        }

        var formId = GetFormId(data, i + 6);
        if (IsValidFormId(formId))
        {
            records.Add(new FormIdSubrecord(subrecordType, formId, i));
        }
    }

    private static void TryAddFormIdSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        string subrecordType, List<FormIdSubrecord> records)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, 4);
        if (len != 4)
        {
            return;
        }

        var formId = GetFormId(data, i + 6);
        if (IsValidFormId(formId))
        {
            records.Add(new FormIdSubrecord(subrecordType, formId, baseOffset + i));
        }
    }

    #endregion

    #region Asset Path Helpers

    /// <summary>
    ///     Clean an asset path by normalizing slashes and removing leading slashes.
    /// </summary>
    public static string CleanAssetPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // Normalize to forward slashes
        var cleaned = path.Replace('\\', '/');

        // Remove leading slashes
        while (cleaned.StartsWith('/'))
        {
            cleaned = cleaned[1..];
        }

        return cleaned.ToLowerInvariant();
    }

    /// <summary>
    ///     Check if a string looks like an asset path.
    /// </summary>
    public static bool IsAssetPath(string text)
    {
        return IsValidPath(text);
    }

    /// <summary>
    ///     Check if a byte is a valid path start character.
    /// </summary>
    private static bool IsPathStartChar(byte b)
    {
        return char.IsAsciiLetterOrDigit((char)b) || b == '\\' || b == '/';
    }

    /// <summary>
    ///     Find the end of a string in a buffer.
    /// </summary>
    private static int FindStringEnd(byte[] buffer, int start, int maxEnd)
    {
        for (var i = start; i < maxEnd; i++)
        {
            if (buffer[i] == 0)
            {
                return i;
            }
        }

        return -1;
    }

    #endregion
}
