using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using System.Text;
using FalloutXbox360Utils.Core.Utils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Scans WRLD GRUP structures in ESM files to find worldspaces and their child CELL/LAND records.
/// </summary>
internal static class WrldGrupScanner
{
    /// <summary>
    ///     Finds all WRLD records in the ESM file and returns their EditorID (if found) and FormID.
    /// </summary>
    internal static List<(string name, uint formId)> FindAllWorldspaces(byte[] data, bool bigEndian)
    {
        var worldspaces = new List<(string name, uint formId)>();

        // Skip TES4 header
        if (data.Length < 24)
        {
            return worldspaces;
        }

        var headerSig = Encoding.ASCII.GetString(data, 0, 4);
        if (headerSig is not "TES4" and not "4SET")
        {
            return worldspaces;
        }

        var headerSize = bigEndian
            ? BinaryUtils.ReadUInt32BE(data.AsSpan(), 4)
            : BinaryUtils.ReadUInt32LE(data.AsSpan(), 4);
        int offset = EsmParser.MainRecordHeaderSize + (int)headerSize;

        while (offset + 24 <= data.Length)
        {
            var headerData = data.AsSpan(offset, 24);
            var sig = bigEndian
                ? new string(new[]
                    { (char)headerData[3], (char)headerData[2], (char)headerData[1], (char)headerData[0] })
                : Encoding.ASCII.GetString(data, offset, 4);

            if (sig == "GRUP")
            {
                var grupSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                var grupType = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 12)
                    : BinaryUtils.ReadUInt32LE(headerData, 12);

                // Top-level GRUP type 0 with label "WRLD"
                if (grupType == 0)
                {
                    var label = Encoding.ASCII.GetString(data, offset + 8, 4);
                    var labelBE = new string(new[]
                    {
                        (char)data[offset + 11], (char)data[offset + 10], (char)data[offset + 9], (char)data[offset + 8]
                    });

                    if (label == "WRLD" || labelBE == "WRLD")
                    {
                        // Scan inside this GRUP for WRLD records
                        var grupEnd = offset + (int)grupSize;
                        var innerOffset =
                            offset + EsmParser
                                .MainRecordHeaderSize; // GRUP header is same size as main record header (24 bytes)

                        while (innerOffset + 24 <= grupEnd)
                        {
                            var innerSig = bigEndian
                                ? new string(new[]
                                {
                                    (char)data[innerOffset + 3], (char)data[innerOffset + 2],
                                    (char)data[innerOffset + 1], (char)data[innerOffset]
                                })
                                : Encoding.ASCII.GetString(data, innerOffset, 4);

                            if (innerSig == "WRLD")
                            {
                                var wrldSize = bigEndian
                                    ? BinaryUtils.ReadUInt32BE(data.AsSpan(), innerOffset + 4)
                                    : BinaryUtils.ReadUInt32LE(data.AsSpan(), innerOffset + 4);
                                var wrldFormId = bigEndian
                                    ? BinaryUtils.ReadUInt32BE(data.AsSpan(), innerOffset + 12)
                                    : BinaryUtils.ReadUInt32LE(data.AsSpan(), innerOffset + 12);

                                // Try to extract EDID from the WRLD record
                                var wrldDataStart = innerOffset + EsmParser.MainRecordHeaderSize;
                                var wrldDataEnd = wrldDataStart + (int)wrldSize;
                                var editorId = ExtractEditorId(data, wrldDataStart, wrldDataEnd, bigEndian);

                                // Use EditorID if found, otherwise fallback to FormID string
                                var name = !string.IsNullOrEmpty(editorId) ? editorId : $"WRLD_0x{wrldFormId:X8}";
                                worldspaces.Add((name, wrldFormId));

                                innerOffset = wrldDataEnd;
                            }
                            else if (innerSig == "GRUP")
                            {
                                var innerGrupSize = bigEndian
                                    ? BinaryUtils.ReadUInt32BE(data.AsSpan(), innerOffset + 4)
                                    : BinaryUtils.ReadUInt32LE(data.AsSpan(), innerOffset + 4);
                                innerOffset += (int)innerGrupSize;
                            }
                            else
                            {
                                // Skip other records
                                var recSize = bigEndian
                                    ? BinaryUtils.ReadUInt32BE(data.AsSpan(), innerOffset + 4)
                                    : BinaryUtils.ReadUInt32LE(data.AsSpan(), innerOffset + 4);
                                innerOffset += EsmParser.MainRecordHeaderSize + (int)recSize;
                            }
                        }
                    }
                }

                offset += (int)grupSize;
            }
            else
            {
                // Non-GRUP record, skip
                var recSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)recSize;
            }
        }

        return worldspaces;
    }

    /// <summary>
    ///     Extracts the EDID (EditorID) from a record's data section.
    /// </summary>
    internal static string? ExtractEditorId(byte[] data, int dataStart, int dataEnd, bool bigEndian)
    {
        var offset = dataStart;
        while (offset + 6 <= dataEnd)
        {
            var subSig = bigEndian
                ? new string(new[]
                    { (char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset] })
                : Encoding.ASCII.GetString(data, offset, 4);
            var subSize = bigEndian
                ? BinaryUtils.ReadUInt16BE(data.AsSpan(), offset + 4)
                : BinaryUtils.ReadUInt16LE(data.AsSpan(), offset + 4);

            if (subSig == "EDID" && subSize > 0 && offset + 6 + subSize <= dataEnd)
            {
                // EDID is null-terminated string
                var edidLength = subSize - 1; // exclude null terminator
                if (edidLength > 0)
                {
                    return Encoding.ASCII.GetString(data, offset + 6, edidLength);
                }
            }

            offset += 6 + subSize;
        }

        return null;
    }

    /// <summary>
    ///     Scans the ESM file to find the WRLD record with the given FormID, then scans its child GRUPs
    ///     to collect all CELL and LAND records that belong to that worldspace.
    /// </summary>
    internal static (List<AnalyzerRecordInfo> cells, List<AnalyzerRecordInfo> lands) ScanWorldspaceCellsAndLands(
        byte[] data, bool bigEndian, uint worldFormId)
    {
        var cells = new List<AnalyzerRecordInfo>();
        var lands = new List<AnalyzerRecordInfo>();

        var header = EsmParser.ParseFileHeader(data);
        if (header == null)
        {
            return (cells, lands);
        }

        // Skip TES4 header
        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null)
        {
            return (cells, lands);
        }

        var offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;

        // Scan for top-level WRLD GRUP (type 0 with label = 'WRLD')
        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            var headerData = data.AsSpan(offset);
            var sig = bigEndian
                ? new string([(char)headerData[3], (char)headerData[2], (char)headerData[1], (char)headerData[0]])
                : Encoding.ASCII.GetString(headerData[..4]);

            if (sig != "GRUP")
            {
                // Skip non-GRUP record
                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
                continue;
            }

            var grupHeader = EsmParser.ParseGroupHeader(headerData, bigEndian);
            if (grupHeader == null)
            {
                break;
            }

            var grupEnd = offset + (int)grupHeader.GroupSize;

            // Check if this is the top-level WRLD GRUP (type 0, label = 'WRLD')
            if (grupHeader.GroupType == 0)
            {
                var labelSig = bigEndian
                    ? new string([
                        (char)grupHeader.Label[3], (char)grupHeader.Label[2], (char)grupHeader.Label[1],
                        (char)grupHeader.Label[0]
                    ])
                    : Encoding.ASCII.GetString(grupHeader.Label);

                if (labelSig == "WRLD")
                {
                    // Scan inside WRLD GRUP for our target worldspace
                    ScanWrldGrup(data, bigEndian, offset + EsmParser.MainRecordHeaderSize, grupEnd, worldFormId, cells,
                        lands);
                }
            }

            offset = grupEnd;
        }

        return (cells, lands);
    }

    /// <summary>
    ///     Scan inside a WRLD GRUP looking for the target worldspace and its children.
    /// </summary>
    private static void ScanWrldGrup(byte[] data, bool bigEndian, int startOffset, int endOffset,
        uint targetWorldFormId, List<AnalyzerRecordInfo> cells, List<AnalyzerRecordInfo> lands)
    {
        var offset = startOffset;

        while (offset + EsmParser.MainRecordHeaderSize <= endOffset)
        {
            var headerData = data.AsSpan(offset);
            var sig = bigEndian
                ? new string([(char)headerData[3], (char)headerData[2], (char)headerData[1], (char)headerData[0]])
                : Encoding.ASCII.GetString(headerData[..4]);

            if (sig == "GRUP")
            {
                var grupHeader = EsmParser.ParseGroupHeader(headerData, bigEndian);
                if (grupHeader == null)
                {
                    break;
                }

                var grupEnd = offset + (int)grupHeader.GroupSize;

                // GRUP type 1 = World Children (contains cells for a specific world)
                // Label = parent WRLD FormID
                if (grupHeader.GroupType == 1)
                {
                    var parentWorldId = bigEndian
                        ? BinaryUtils.ReadUInt32BE(grupHeader.Label)
                        : BinaryUtils.ReadUInt32LE(grupHeader.Label);

                    if (parentWorldId == targetWorldFormId)
                    {
                        // This is our target worldspace's children - scan for cells and lands
                        ScanWorldChildren(data, bigEndian, offset + EsmParser.MainRecordHeaderSize, grupEnd, cells,
                            lands);
                    }
                }

                offset = grupEnd;
            }
            else if (sig == "WRLD")
            {
                // WRLD record - check if it's our target
                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);

                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
            }
            else
            {
                // Skip other records
                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
            }
        }
    }

    /// <summary>
    ///     Scan World Children GRUP (type 1) for CELL and LAND records.
    /// </summary>
    private static void ScanWorldChildren(byte[] data, bool bigEndian, int startOffset, int endOffset,
        List<AnalyzerRecordInfo> cells, List<AnalyzerRecordInfo> lands)
    {
        var offset = startOffset;

        while (offset + EsmParser.MainRecordHeaderSize <= endOffset)
        {
            var headerData = data.AsSpan(offset);
            var sig = bigEndian
                ? new string([(char)headerData[3], (char)headerData[2], (char)headerData[1], (char)headerData[0]])
                : Encoding.ASCII.GetString(headerData[..4]);

            if (sig == "GRUP")
            {
                var grupHeader = EsmParser.ParseGroupHeader(headerData, bigEndian);
                if (grupHeader == null)
                {
                    break;
                }

                var grupEnd = offset + (int)grupHeader.GroupSize;

                // Recursively scan child GRUPs (types 4-10 are cell-related)
                ScanWorldChildren(data, bigEndian, offset + EsmParser.MainRecordHeaderSize, grupEnd, cells, lands);

                offset = grupEnd;
            }
            else if (sig == "CELL")
            {
                var recordHeader = EsmParser.ParseRecordHeader(headerData, bigEndian);
                if (recordHeader != null)
                {
                    cells.Add(new AnalyzerRecordInfo
                    {
                        Signature = "CELL",
                        Offset = (uint)offset,
                        DataSize = recordHeader.DataSize,
                        Flags = recordHeader.Flags,
                        FormId = recordHeader.FormId,
                        TotalSize = EsmParser.MainRecordHeaderSize + recordHeader.DataSize
                    });
                }

                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
            }
            else if (sig == "LAND")
            {
                var recordHeader = EsmParser.ParseRecordHeader(headerData, bigEndian);
                if (recordHeader != null)
                {
                    lands.Add(new AnalyzerRecordInfo
                    {
                        Signature = "LAND",
                        Offset = (uint)offset,
                        DataSize = recordHeader.DataSize,
                        Flags = recordHeader.Flags,
                        FormId = recordHeader.FormId,
                        TotalSize = EsmParser.MainRecordHeaderSize + recordHeader.DataSize
                    });
                }

                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
            }
            else
            {
                // Skip other records
                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
            }
        }
    }

    /// <summary>
    ///     Recursively scan GRUPs looking for LAND records and tracking parent CELL FormIDs.
    /// </summary>
    internal static void ScanForLandRecords(byte[] data, bool bigEndian, int startOffset, int endOffset,
        uint currentCellFormId, Dictionary<uint, uint> landToCellMap)
    {
        var offset = startOffset;
        var maxIterations = 1_000_000;
        var iterations = 0;

        while (offset + EsmParser.MainRecordHeaderSize <= endOffset && iterations++ < maxIterations)
        {
            var headerData = data.AsSpan(offset);
            var sig = bigEndian
                ? new string([(char)headerData[3], (char)headerData[2], (char)headerData[1], (char)headerData[0]])
                : Encoding.ASCII.GetString(headerData[..4]);

            if (sig == "GRUP")
            {
                var grupHeader = EsmParser.ParseGroupHeader(headerData, bigEndian);
                if (grupHeader == null)
                {
                    break;
                }

                var grupEnd = offset + (int)grupHeader.GroupSize;
                var innerStart = offset + EsmParser.MainRecordHeaderSize;

                // GRUP type 9 = Cell Temporary Children (contains LAND)
                // The label is the parent CELL FormID
                var parentCellFormId = currentCellFormId;
                if (grupHeader.GroupType == 9)
                {
                    // Label contains parent CELL FormID
                    parentCellFormId = bigEndian
                        ? BinaryUtils.ReadUInt32BE(grupHeader.Label)
                        : BinaryUtils.ReadUInt32LE(grupHeader.Label);
                }

                if (grupEnd > innerStart && grupEnd <= data.Length)
                {
                    ScanForLandRecords(data, bigEndian, innerStart, grupEnd, parentCellFormId, landToCellMap);
                }

                offset = grupEnd;
            }
            else
            {
                // Regular record
                var dataSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 4)
                    : BinaryUtils.ReadUInt32LE(headerData, 4);
                var formId = bigEndian
                    ? BinaryUtils.ReadUInt32BE(headerData, 12)
                    : BinaryUtils.ReadUInt32LE(headerData, 12);

                var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)dataSize;

                if (sig == "LAND" && currentCellFormId != 0)
                {
                    landToCellMap[formId] = currentCellFormId;
                }

                offset = recordEnd;
            }
        }
    }
}
