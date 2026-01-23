using System.Buffers.Binary;
using System.Text;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

public static partial class DumpCommands
{
    internal static void TraceRecursive(byte[] data, bool bigEndian, int startOffset, int endOffset,
        int? filterDepth, ref int recordCount, int limit, int depth, Table table)
    {
        var offset = startOffset;

        // limit <= 0 means unlimited
        while (offset + EsmParser.MainRecordHeaderSize <= endOffset && (limit <= 0 || recordCount < limit))
        {
            var recHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (recHeader == null)
            {
                var hexBytes = string.Join(" ", data.Skip(offset).Take(24).Select(b => b.ToString("X2")));
                table.AddRow($"[red]0x{offset:X8}[/]", "[red]FAIL[/]", "-", "-", $"Raw: {hexBytes}", "-");
                break;
            }

            // Allow GRUP and any 4-letter ASCII signature (valid record types)
            var isValidSig = recHeader.Signature == "GRUP" ||
                             recHeader.Signature == "TOFT" ||
                             EsmRecordTypes.MainRecordTypes.ContainsKey(recHeader.Signature) ||
                             (recHeader.Signature.Length == 4 && recHeader.Signature.All(c => c >= 'A' && c <= 'Z'));

            if (!isValidSig)
            {
                var hexBytes = string.Join(" ", data.Skip(offset).Take(24).Select(b => b.ToString("X2")));
                table.AddRow($"[red]0x{offset:X8}[/]", $"[red]{recHeader.Signature}[/]", "-", "-",
                    $"Unknown: {hexBytes}", "-");
                break;
            }

            var groupEnd = offset + (recHeader.Signature == "GRUP"
                ? (int)recHeader.DataSize
                : EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize);

            var showThis = !filterDepth.HasValue || filterDepth.Value == depth;

            if (showThis)
            {
                recordCount++;
                var label = recHeader.Signature == "GRUP"
                    ? EsmHelpers.GetGroupLabel(data, offset, bigEndian)
                    : $"FormID 0x{recHeader.FormId:X8}";
                var sigColor = recHeader.Signature == "GRUP" ? "yellow" : "cyan";

                table.AddRow(
                    $"0x{offset:X8}",
                    $"[{sigColor}]{recHeader.Signature}[/]",
                    $"{recHeader.DataSize:N0}",
                    $"0x{groupEnd:X8}",
                    label,
                    depth.ToString());
            }

            if (recHeader.Signature == "GRUP")
            {
                var innerOffset = offset + EsmParser.MainRecordHeaderSize;
                TraceRecursive(data, bigEndian, innerOffset, groupEnd, filterDepth, ref recordCount, limit, depth + 1,
                    table);
            }

            offset = groupEnd;
        }
    }

    internal static bool ValidateRecursive(byte[] data, bool bigEndian, int startOffset, int endOffset, int limit,
        ref int recordCount, ref int subrecordCount, ref int compressedSkipped, int depth, out string? error)
    {
        error = null;
        var offset = startOffset;

        while (offset + EsmParser.MainRecordHeaderSize <= endOffset && (limit <= 0 || recordCount < limit))
        {
            var recHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (recHeader == null)
            {
                var hexBytes = string.Join(" ", data.Skip(offset).Take(24).Select(b => b.ToString("X2")));
                error = $"FAIL at 0x{offset:X8}: {hexBytes}";
                return false;
            }

            var isValidSig = recHeader.Signature == "GRUP" ||
                             recHeader.Signature == "TOFT" ||
                             EsmRecordTypes.MainRecordTypes.ContainsKey(recHeader.Signature) ||
                             (recHeader.Signature.Length == 4 && recHeader.Signature.All(c => c >= 'A' && c <= 'Z'));

            if (!isValidSig)
            {
                var hexBytes = string.Join(" ", data.Skip(offset).Take(24).Select(b => b.ToString("X2")));
                error = $"FAIL at 0x{offset:X8}: unknown signature '{recHeader.Signature}' ({hexBytes})";
                return false;
            }

            var recordEnd = offset + (recHeader.Signature == "GRUP"
                ? (int)recHeader.DataSize
                : EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize);

            if (recordEnd <= offset || recordEnd > data.Length)
            {
                error =
                    $"FAIL at 0x{offset:X8}: invalid size {recHeader.DataSize} for {recHeader.Signature} (end=0x{recordEnd:X8})";
                return false;
            }

            if (recHeader.Signature == "GRUP")
            {
                var innerOffset = offset + EsmParser.MainRecordHeaderSize;
                if (!ValidateRecursive(data, bigEndian, innerOffset, recordEnd, limit, ref recordCount,
                        ref subrecordCount, ref compressedSkipped, depth + 1, out error))
                    return false;
            }
            else
            {
                var isCompressed = (recHeader.Flags & 0x00040000) != 0;
                if (isCompressed)
                {
                    if (recHeader.DataSize < 4)
                    {
                        error =
                            $"FAIL at 0x{offset:X8}: compressed {recHeader.Signature} has size < 4";
                        return false;
                    }

                    compressedSkipped++;
                }
                else
                {
                    var recordInfo = new AnalyzerRecordInfo
                    {
                        Signature = recHeader.Signature,
                        FormId = recHeader.FormId,
                        Flags = recHeader.Flags,
                        DataSize = recHeader.DataSize,
                        Offset = (uint)offset,
                        TotalSize = (uint)(recordEnd - offset)
                    };

                    try
                    {
                        var recordData = EsmHelpers.GetRecordData(data, recordInfo, bigEndian);
                        var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);
                        subrecordCount += subrecords.Count;

                        foreach (var sub in subrecords)
                        {
                            var subEnd = sub.Offset + 6 + sub.Data.Length;
                            if (subEnd > recordData.Length)
                            {
                                error =
                                    $"FAIL at 0x{offset:X8}: subrecord {sub.Signature} overruns record data";
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        error =
                            $"FAIL at 0x{offset:X8}: subrecord parse error in {recHeader.Signature} (0x{recHeader.FormId:X8}) - {ex.GetType().Name}: {ex.Message}";
                        return false;
                    }
                }
            }

            recordCount++;
            offset = recordEnd;
        }

        return true;
    }

    internal static bool MatchesAt(byte[] data, long offset, byte[] pattern)
    {
        for (var j = 0; j < pattern.Length; j++)
            if (data[offset + j] != pattern[j])
                return false;
        return true;
    }

    internal static void DisplaySearchMatch(byte[] data, long offset, int patternLength, int contextBytes)
    {
        var rule = new Rule($"[cyan]Offset: 0x{offset:X8}[/]");
        rule.LeftJustified();
        AnsiConsole.Write(rule);

        var contextStart = Math.Max(0, offset - contextBytes);
        var contextEnd = Math.Min(data.Length, offset + patternLength + contextBytes);
        var contextData = data.Skip((int)contextStart).Take((int)(contextEnd - contextStart)).ToArray();

        var highlightStart = (int)(offset - contextStart);
        EsmDisplayHelpers.RenderHexDump(contextData, contextStart, highlightStart, patternLength);
        AnsiConsole.WriteLine();
    }

    internal static bool TryLocateInRange(byte[] data, bool bigEndian, int startOffset, int endOffset, int targetOffset,
        List<string> path, out LocatedRecord record, out LocatedSubrecord? subrecord)
    {
        record = null!;
        subrecord = null;

        var offset = startOffset;
        while (offset + EsmParser.MainRecordHeaderSize <= endOffset)
        {
            var recHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (recHeader == null) return false;

            var recordEnd = offset + (recHeader.Signature == "GRUP"
                ? (int)recHeader.DataSize
                : EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize);

            if (targetOffset < offset) return false;

            if (targetOffset >= offset && targetOffset < recordEnd)
            {
                if (recHeader.Signature == "GRUP")
                {
                    var label = EsmHelpers.GetGroupLabel(data, offset, bigEndian);
                    path.Add($"{label} @0x{offset:X8}");

                    var innerStart = offset + EsmParser.MainRecordHeaderSize;
                    if (TryLocateInRange(data, bigEndian, innerStart, recordEnd, targetOffset, path, out record,
                            out subrecord)) return true;

                    record = new LocatedRecord("GRUP", 0, 0, offset, recordEnd, recHeader.DataSize, true, label, false);
                    return true;
                }

                var isCompressed = (recHeader.Flags & 0x00040000) != 0;
                record = new LocatedRecord(recHeader.Signature, recHeader.FormId, recHeader.Flags, offset, recordEnd,
                    recHeader.DataSize, false, string.Empty, isCompressed);

                if (!isCompressed)
                    subrecord = LocateSubrecordInRecord(data, bigEndian, offset, recHeader.DataSize, targetOffset);

                return true;
            }

            offset = recordEnd;
        }

        return false;
    }

    private static LocatedSubrecord? LocateSubrecordInRecord(byte[] data, bool bigEndian, int recordOffset,
        uint dataSize, int targetOffset)
    {
        var dataStart = recordOffset + EsmParser.MainRecordHeaderSize;
        var recordEnd = dataStart + (int)dataSize;
        var offset = dataStart;
        var pendingExtendedSize = 0;

        while (offset + 6 <= recordEnd)
        {
            var sigBytes = data.AsSpan(offset, 4);
            var signature = bigEndian
                ? $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}"
                : Encoding.ASCII.GetString(sigBytes);

            var dataSizeHeader = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));

            var dataOffset = offset + 6;
            if (signature == "XXXX" && dataSizeHeader == 4 && dataOffset + 4 <= recordEnd)
                pendingExtendedSize = bigEndian
                    ? (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(dataOffset, 4))
                    : (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(dataOffset, 4));

            var actualSize = dataSizeHeader == 0 && pendingExtendedSize > 0 ? pendingExtendedSize : dataSizeHeader;
            if (dataSizeHeader == 0 && pendingExtendedSize > 0) pendingExtendedSize = 0;

            var dataEnd = dataOffset + actualSize;
            if (dataEnd > recordEnd) break;

            if (targetOffset >= offset && targetOffset < dataEnd)
                return new LocatedSubrecord(signature, offset, dataOffset, dataEnd, actualSize);

            offset = dataEnd;
        }

        return null;
    }

    internal sealed record LocatedRecord(
        string Signature,
        uint FormId,
        uint Flags,
        int Start,
        int End,
        uint DataSize,
        bool IsGroup,
        string Label,
        bool IsCompressed);

    internal sealed record LocatedSubrecord(
        string Signature,
        int HeaderStart,
        int DataStart,
        int DataEnd,
        int DataSize);
}
