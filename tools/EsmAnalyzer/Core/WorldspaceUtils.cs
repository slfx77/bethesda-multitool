using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Schema;

namespace EsmAnalyzer.Core;

/// <summary>
///     Utilities for working with WRLD (worldspace) records.
/// </summary>
public static class WorldspaceUtils
{
    /// <summary>
    ///     Extracts worldspace bounds from the WRLD record's MNAM subrecord.
    /// </summary>
    /// <param name="data">Raw ESM file data.</param>
    /// <param name="bigEndian">True if data is big-endian (Xbox 360).</param>
    /// <param name="worldspaceFormId">FormID of the target worldspace.</param>
    /// <returns>The worldspace bounds, or null if not found.</returns>
    public static WorldspaceBounds? ExtractWorldspaceBounds(byte[] data, bool bigEndian, uint worldspaceFormId)
    {
        var header = EsmParser.ParseFileHeader(data);
        if (header == null)
        {
            return null;
        }

        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null)
        {
            return null;
        }

        var offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;

        while (offset + EsmConstants.GrupHeaderSize <= data.Length)
        {
            var sig = ReadSignature(data, offset, bigEndian);

            if (sig == "GRUP")
            {
                offset += EsmParser.MainRecordHeaderSize;
                continue;
            }

            var recSize = BinaryUtils.ReadUInt32(data, offset + 4, bigEndian);
            var formId = BinaryUtils.ReadUInt32(data, offset + 12, bigEndian);

            if (sig == "WRLD" && formId == worldspaceFormId)
            {
                // Found the target worldspace, parse its subrecords
                var recordDataSpan = data.AsSpan(offset + EsmParser.MainRecordHeaderSize, (int)recSize);

                return ParseMnamBounds(recordDataSpan, bigEndian);
            }

            offset += EsmParser.MainRecordHeaderSize + (int)recSize;
        }

        return null;
    }

    /// <summary>
    ///     Finds a WRLD record by FormID.
    /// </summary>
    internal static (AnalyzerRecordInfo? record, byte[]? recordData) FindWorldspaceRecord(
        byte[] data, bool bigEndian, uint worldspaceFormId)
    {
        var records = EsmRecordParser.ScanForRecordType(data, bigEndian, "WRLD");
        var wrldRecord = records.FirstOrDefault(r => r.FormId == worldspaceFormId);

        if (wrldRecord == null)
        {
            return (null, null);
        }

        var recordData = HeightmapUtils.GetRecordData(data, wrldRecord, bigEndian);
        return (wrldRecord, recordData);
    }

    /// <summary>
    ///     Gets the editor ID from a worldspace record's subrecords.
    /// </summary>
    public static string? GetWorldspaceEditorId(byte[] recordData, bool bigEndian)
    {
        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
        var edid = EsmRecordParser.FindSubrecord(subrecords, "EDID");
        return edid != null ? EsmRecordParser.GetSubrecordString(edid) : null;
    }

    /// <summary>
    ///     Gets the display name from a worldspace record's subrecords.
    /// </summary>
    public static string? GetWorldspaceName(byte[] recordData, bool bigEndian)
    {
        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
        var full = EsmRecordParser.FindSubrecord(subrecords, "FULL");
        return full != null ? EsmRecordParser.GetSubrecordString(full) : null;
    }

    /// <summary>
    ///     Parses the MNAM subrecord to extract worldspace bounds.
    /// </summary>
    private static WorldspaceBounds? ParseMnamBounds(ReadOnlySpan<byte> recordData, bool bigEndian)
    {
        var subOffset = 0;

        while (subOffset + EsmConstants.SubrecordHeaderSize <= recordData.Length)
        {
            var subSig = bigEndian
                ? new string([
                    (char)recordData[subOffset + 3], (char)recordData[subOffset + 2],
                    (char)recordData[subOffset + 1], (char)recordData[subOffset]
                ])
                : Encoding.ASCII.GetString(recordData.Slice(subOffset, 4));

            var subSize = BinaryUtils.ReadUInt16(recordData.ToArray(), subOffset + 4, bigEndian);

            if (subSig == "MNAM" && subSize >= 16)
            {
                var mnamData = recordData.Slice(subOffset + EsmConstants.SubrecordHeaderSize, subSize);

                // MNAM structure (16 bytes):
                // int32 usableWidth, int32 usableHeight
                // int16 nwCellX, int16 nwCellY, int16 seCellX, int16 seCellY
                var nwCellX = BinaryUtils.ReadInt16(mnamData.ToArray(), 8, bigEndian);
                var nwCellY = BinaryUtils.ReadInt16(mnamData.ToArray(), 10, bigEndian);
                var seCellX = BinaryUtils.ReadInt16(mnamData.ToArray(), 12, bigEndian);
                var seCellY = BinaryUtils.ReadInt16(mnamData.ToArray(), 14, bigEndian);

                // NW is top-left (higher Y), SE is bottom-right (lower Y)
                return new WorldspaceBounds
                {
                    MinCellX = Math.Min(nwCellX, seCellX),
                    MaxCellX = Math.Max(nwCellX, seCellX),
                    MinCellY = Math.Min(nwCellY, seCellY),
                    MaxCellY = Math.Max(nwCellY, seCellY)
                };
            }

            subOffset += EsmConstants.SubrecordHeaderSize + subSize;
        }

        return null;
    }

    /// <summary>
    ///     Reads a 4-character signature at the specified offset.
    /// </summary>
    private static string ReadSignature(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? new string([(char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]])
            : Encoding.ASCII.GetString(data, offset, 4);
    }
}