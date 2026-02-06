using System.Buffers;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

public sealed partial class EsmRecordFormat
{
    #region FormID Correlation

    public static Dictionary<uint, string> CorrelateFormIdsToNames(byte[] data,
        EsmRecordScanResult? existingScan = null)
    {
        var scan = existingScan ?? ScanForRecords(data);
        var correlations = new Dictionary<uint, string>();

        foreach (var edid in scan.EditorIds)
        {
            var formId = FindRecordFormId(data, (int)edid.Offset);
            if (formId != 0 && !correlations.ContainsKey(formId))
            {
                correlations[formId] = edid.Name;
            }
        }

        return correlations;
    }

    /// <summary>
    ///     Correlate FormIDs to names using memory-mapped access.
    /// </summary>
    public static Dictionary<uint, string> CorrelateFormIdsToNamesMemoryMapped(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult existingScan)
    {
        // fileSize parameter kept for API consistency, not needed for correlation
        _ = fileSize;

        var correlations = new Dictionary<uint, string>();
        var buffer = ArrayPool<byte>.Shared.Rent(256); // Small buffer for searching backward

        try
        {
            foreach (var edid in existingScan.EditorIds)
            {
                var edidOffset = edid.Offset;
                var searchStart = Math.Max(0, edidOffset - 200);
                var toRead = (int)Math.Min(256, edidOffset - searchStart + 50);

                if (toRead <= 0)
                {
                    continue;
                }

                accessor.ReadArray(searchStart, buffer, 0, toRead);

                var localEdidOffset = (int)(edidOffset - searchStart);
                var formId = FindRecordFormIdInBuffer(buffer, localEdidOffset, toRead);

                if (formId != 0 && !correlations.ContainsKey(formId))
                {
                    correlations[formId] = edid.Name;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Merge runtime EditorID hash table entries (48K+ entries with validated FormIDs)
        foreach (var entry in existingScan.RuntimeEditorIds)
        {
            if (entry.FormId != 0 && !correlations.ContainsKey(entry.FormId))
            {
                correlations[entry.FormId] = entry.EditorId;
            }
        }

        return correlations;
    }

    #endregion

    #region FormID Finding

    private static uint FindRecordFormId(byte[] data, int edidOffset)
    {
        var searchStart = Math.Max(0, edidOffset - 200);
        for (var checkOffset = edidOffset - 4; checkOffset >= searchStart; checkOffset--)
        {
            var formId = TryExtractFormIdFromRecordHeader(data, checkOffset, edidOffset, data.Length);
            if (formId != 0)
            {
                return formId;
            }
        }

        return 0;
    }

    private static uint FindRecordFormIdInBuffer(byte[] data, int edidLocalOffset, int dataLength)
    {
        var searchStart = Math.Max(0, edidLocalOffset - 200);
        for (var checkOffset = edidLocalOffset - 4; checkOffset >= searchStart; checkOffset--)
        {
            var formId = TryExtractFormIdFromRecordHeader(data, checkOffset, edidLocalOffset, dataLength);
            if (formId != 0)
            {
                return formId;
            }
        }

        return 0;
    }

    private static uint TryExtractFormIdFromRecordHeader(byte[] data, int checkOffset, int edidOffset, int dataLength)
    {
        if (checkOffset + 24 >= dataLength)
        {
            return 0;
        }

        if (!IsRecordTypeMarker(data, checkOffset))
        {
            return 0;
        }

        var formId = BinaryUtils.ReadUInt32LE(data, checkOffset + 12);
        if (formId == 0 || formId == 0xFFFFFFFF || formId >> 24 > 0x0F)
        {
            return 0;
        }

        var size = BinaryUtils.ReadUInt32LE(data, checkOffset + 4);
        if (size is > 0 and < 10_000_000 && edidOffset < checkOffset + 24 + size)
        {
            return formId;
        }

        return 0;
    }

    #endregion

    #region FormID Extraction Helpers

    /// <summary>
    ///     Get FormID, trying both endianness.
    /// </summary>
    private static uint GetFormId(byte[] data, int offset)
    {
        var formIdLe = BinaryUtils.ReadUInt32LE(data, offset);
        var formIdBe = BinaryUtils.ReadUInt32BE(data, offset);

        // Valid FormIDs have plugin index 0x00-0x0F
        if (formIdLe >> 24 <= 0x0F && formIdLe != 0)
        {
            return formIdLe;
        }

        if (formIdBe >> 24 <= 0x0F && formIdBe != 0)
        {
            return formIdBe;
        }

        return 0;
    }

    private static bool IsValidFormId(uint formId)
    {
        // FormID should not be 0 or 0xFFFFFFFF
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return false;
        }

        // Plugin index should be reasonable (0x00-0xFF, typically 0x00-0x0F)
        var pluginIndex = formId >> 24;
        return pluginIndex <= 0xFF;
    }

    #endregion
}
