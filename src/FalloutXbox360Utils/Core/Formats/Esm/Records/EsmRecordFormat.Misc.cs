using System.Buffers;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

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
    ///     Scans for model paths, texture paths, and other asset references using
    ///     pooled buffers, deduplication, and categorization.
    /// </summary>
    public static void ScanForAssetStrings(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult scanResult,
        bool verbose = false)
    {
        var sw = Stopwatch.StartNew();

        const int chunkSize = 4 * 1024 * 1024; // 4MB chunks
        const int minStringLength = 8; // Minimum path length (e.g., "a/b.nif")
        const int maxStringLength = 260; // MAX_PATH
        const int maxAssetStrings = 100000; // Limit to prevent runaway

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        var lastProgressMb = 0L;

        var log = Logger.Instance;
        log.Debug("AssetStrings: Starting scan of {0:N0} MB", fileSize / (1024 * 1024));

        try
        {
            long offset = 0;
            while (offset < fileSize && scanResult.AssetStrings.Count < maxAssetStrings)
            {
                var toRead = (int)Math.Min(chunkSize, fileSize - offset);
                accessor.ReadArray(offset, buffer, 0, toRead);

                // Scan for null-terminated strings that look like asset paths
                var i = 0;
                while (i < toRead - minStringLength && scanResult.AssetStrings.Count < maxAssetStrings)
                {
                    // Look for strings that start with printable ASCII
                    if (!IsPathStartChar(buffer[i]))
                    {
                        i++;
                        continue;
                    }

                    // Find the end of this potential string (null terminator)
                    var stringEnd = FindStringEnd(buffer, i, Math.Min(i + maxStringLength, toRead));
                    if (stringEnd < 0)
                    {
                        i++;
                        continue;
                    }

                    var stringLength = stringEnd - i;
                    if (stringLength < minStringLength)
                    {
                        i = stringEnd + 1;
                        continue;
                    }

                    // Extract the string and check if it's a valid path
                    var path = Encoding.ASCII.GetString(buffer, i, stringLength);
                    if (IsAssetPath(path) && seenPaths.Add(path))
                    {
                        var category = CategorizeAssetPath(path);
                        scanResult.AssetStrings.Add(new DetectedAssetString
                        {
                            Path = path,
                            Offset = offset + i,
                            Category = category
                        });
                    }

                    i = stringEnd + 1;
                }

                offset += toRead;

                // Progress every 100MB
                if (offset / (100 * 1024 * 1024) > lastProgressMb)
                {
                    lastProgressMb = offset / (100 * 1024 * 1024);
                    log.Debug("AssetStrings:   {0:N0} MB scanned, {1:N0} unique paths found ({2:N0} ms)",
                        offset / (1024 * 1024), scanResult.AssetStrings.Count, sw.ElapsedMilliseconds);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        sw.Stop();
        log.Debug("AssetStrings: Complete: {0:N0} unique asset paths in {1:N0} ms",
            scanResult.AssetStrings.Count, sw.ElapsedMilliseconds);
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
    ///     Check if a string looks like a valid asset path by verifying it has
    ///     a path separator and a known game asset file extension.
    /// </summary>
    public static bool IsAssetPath(string path)
    {
        // Must contain a path separator
        if (!path.Contains('\\') && !path.Contains('/'))
        {
            return false;
        }

        // Must have a file extension
        var lastDot = path.LastIndexOf('.');
        if (lastDot < 0 || lastDot >= path.Length - 1)
        {
            return false;
        }

        var extension = path[(lastDot + 1)..].ToLowerInvariant();

        // Check for known game asset extensions
        return extension is
            "nif" or "kf" or "hkx" or // Models/animations
            "dds" or "ddx" or "tga" or "bmp" or // Textures
            "wav" or "mp3" or "ogg" or "lip" or // Sound/lipsync
            "psc" or "pex" or // Scripts
            "egm" or "egt" or "tri" or // FaceGen
            "spt" or "txt" or "xml" or // Misc data
            "esm" or "esp"; // Plugin files (references)
    }

    /// <summary>
    ///     Categorize an asset path by its file extension.
    /// </summary>
    private static AssetCategory CategorizeAssetPath(string path)
    {
        var lastDot = path.LastIndexOf('.');
        if (lastDot < 0)
        {
            return AssetCategory.Other;
        }

        var extension = path[(lastDot + 1)..].ToLowerInvariant();

        return extension switch
        {
            "nif" or "egm" or "egt" or "tri" => AssetCategory.Model,
            "dds" or "ddx" or "tga" or "bmp" => AssetCategory.Texture,
            "wav" or "mp3" or "ogg" or "lip" => AssetCategory.Sound,
            "psc" or "pex" => AssetCategory.Script,
            "kf" or "hkx" => AssetCategory.Animation,
            _ => AssetCategory.Other
        };
    }

    /// <summary>
    ///     Check if a byte is a valid path start character.
    /// </summary>
    private static bool IsPathStartChar(byte b)
    {
        // Paths typically start with: a-z, A-Z, ., \, /
        return (b >= 'a' && b <= 'z') ||
               (b >= 'A' && b <= 'Z') ||
               b == '.' || b == '\\' || b == '/';
    }

    /// <summary>
    ///     Find the end of a null-terminated string in a buffer.
    ///     Returns -1 if a non-printable character is encountered before a null terminator.
    /// </summary>
    private static int FindStringEnd(byte[] buffer, int start, int maxEnd)
    {
        for (var i = start; i < maxEnd; i++)
        {
            if (buffer[i] == 0)
            {
                return i;
            }

            // Stop at non-printable characters (except path separators)
            if (buffer[i] < 0x20 || buffer[i] > 0x7E)
            {
                return -1;
            }
        }

        return -1; // No null terminator found
    }

    #endregion
}
