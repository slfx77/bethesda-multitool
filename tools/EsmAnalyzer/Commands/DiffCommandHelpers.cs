using System.Text;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Utils;

namespace EsmAnalyzer.Commands;

internal static class DiffCommandHelpers
{
    internal static void AddFlagRow(Table table, int bit, uint mask, string name, uint xboxFlags, uint pcFlags)
    {
        var xboxSet = (xboxFlags & mask) != 0;
        var pcSet = (pcFlags & mask) != 0;
        table.AddRow(
            bit.ToString(),
            $"0x{mask:X8}",
            name,
            xboxSet ? "[green]SET[/]" : "[grey]not set[/]",
            pcSet ? "[green]SET[/]" : "[grey]not set[/]"
        );
    }

    internal static string FormatBytes(byte[] data, int offset, int length)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < length && offset + i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');

            sb.Append(data[offset + i].ToString("X2"));
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Analyzes byte arrays to detect structured conversion patterns.
    ///     Returns a description like "4B same + 4B swap" or empty string if no clear pattern.
    /// </summary>
    internal static string AnalyzeStructuredDifference(byte[] xbox, byte[] pc)
    {
        if (xbox.Length != pc.Length || xbox.Length < 4) return string.Empty;

        var segments = new List<string>();
        var offset = 0;

        while (offset < xbox.Length)
        {
            var remaining = xbox.Length - offset;

            // Try to detect identical bytes
            var identicalCount = 0;
            while (offset + identicalCount < xbox.Length &&
                   xbox[offset + identicalCount] == pc[offset + identicalCount]) identicalCount++;

            if (identicalCount > 0)
            {
                segments.Add($"{identicalCount}B same");
                offset += identicalCount;
                continue;
            }

            // Try 4-byte swap
            if (remaining >= 4 &&
                xbox[offset] == pc[offset + 3] && xbox[offset + 1] == pc[offset + 2] &&
                xbox[offset + 2] == pc[offset + 1] && xbox[offset + 3] == pc[offset])
            {
                // Check if multiple consecutive 4-byte swaps
                var swapCount = 1;
                while (offset + swapCount * 4 + 4 <= xbox.Length)
                {
                    var o = offset + swapCount * 4;
                    if (xbox[o] == pc[o + 3] && xbox[o + 1] == pc[o + 2] &&
                        xbox[o + 2] == pc[o + 1] && xbox[o + 3] == pc[o])
                        swapCount++;
                    else
                        break;
                }

                segments.Add(swapCount == 1 ? "4B swap" : $"{swapCount}×4B swap");
                offset += swapCount * 4;
                continue;
            }

            // Try 2-byte swap
            if (remaining >= 2 && xbox[offset] == pc[offset + 1] && xbox[offset + 1] == pc[offset])
            {
                // Check if multiple consecutive 2-byte swaps
                var swapCount = 1;
                while (offset + swapCount * 2 + 2 <= xbox.Length)
                {
                    var o = offset + swapCount * 2;
                    if (xbox[o] == pc[o + 1] && xbox[o + 1] == pc[o])
                        swapCount++;
                    else
                        break;
                }

                segments.Add(swapCount == 1 ? "2B swap" : $"{swapCount}×2B swap");
                offset += swapCount * 2;
                continue;
            }

            // Unknown difference - can't determine pattern
            return string.Empty;
        }

        // Only return pattern if we detected something meaningful
        return segments.Count > 0 ? string.Join(" + ", segments) : string.Empty;
    }

    internal static void TryInterpretDifference(string sig, byte[] xboxData, byte[] pcData, bool xboxBE, bool pcBE)
    {
        // Known subrecord interpretations
        switch (sig)
        {
            case "EDID": // Editor ID - string
                var xboxStr = Encoding.ASCII.GetString(xboxData).TrimEnd('\0');
                var pcStr = Encoding.ASCII.GetString(pcData).TrimEnd('\0');
                if (xboxStr != pcStr) AnsiConsole.MarkupLine($"    [grey]String: Xbox='{xboxStr}' PC='{pcStr}'[/]");
                break;

            case "DATA" when xboxData.Length == 4: // Often a uint32 or float
                var xboxU32 = xboxBE
                    ? BinaryUtils.ReadUInt32BE(xboxData.AsSpan())
                    : BinaryUtils.ReadUInt32LE(xboxData.AsSpan());
                var pcU32 = pcBE
                    ? BinaryUtils.ReadUInt32BE(pcData.AsSpan())
                    : BinaryUtils.ReadUInt32LE(pcData.AsSpan());
                var xboxF = BitConverter.ToSingle(BitConverter.GetBytes(xboxU32), 0);
                var pcF = BitConverter.ToSingle(pcData, 0);
                AnsiConsole.MarkupLine($"    [grey]As uint32: Xbox={xboxU32} PC={pcU32}[/]");
                AnsiConsole.MarkupLine($"    [grey]As float:  Xbox={xboxF:F4} PC={pcF:F4}[/]");
                break;

            case "VHGT" when xboxData.Length >= 4: // Height data - first float is offset
                var xboxOffset =
                    BitConverter.ToSingle(BitConverter.GetBytes(BinaryUtils.ReadUInt32BE(xboxData.AsSpan())), 0);
                var pcOffset = BitConverter.ToSingle(pcData, 0);
                AnsiConsole.MarkupLine($"    [grey]Height offset: Xbox={xboxOffset:F2} PC={pcOffset:F2}[/]");
                break;
        }
    }

    internal static bool CheckEndianSwapped(byte[] xbox, byte[] pc)
    {
        if (xbox.Length != pc.Length) return false;

        // Check if it's a simple 2-byte swap
        if (xbox.Length == 2) return xbox[0] == pc[1] && xbox[1] == pc[0];

        // Check if it's a simple 4-byte swap
        if (xbox.Length == 4) return xbox[0] == pc[3] && xbox[1] == pc[2] && xbox[2] == pc[1] && xbox[3] == pc[0];

        // For longer data, check if it's a series of 4-byte swaps
        if (xbox.Length % 4 == 0)
        {
            for (var i = 0; i < xbox.Length; i += 4)
                if (xbox[i] != pc[i + 3] || xbox[i + 1] != pc[i + 2] ||
                    xbox[i + 2] != pc[i + 1] || xbox[i + 3] != pc[i])
                    return false;

            return true;
        }

        return false;
    }

    internal static AnalyzerRecordInfo? FindRecordByFormId(byte[] data, bool bigEndian, uint formId)
    {
        // Scan file for records with matching FormID
        var offset = 0;
        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            // Check if this looks like a record (not a GRUP)
            var sig = bigEndian
                ? new string([
                    (char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]
                ])
                : Encoding.ASCII.GetString(data, offset, 4);

            if (sig == "GRUP")
            {
                // Skip GRUP header
                var grupSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(data.AsSpan(offset + 4))
                    : BinaryUtils.ReadUInt32LE(data.AsSpan(offset + 4));
                offset += 24; // GRUP header size, then continue inside
                continue;
            }

            // Read record header
            var dataSize = bigEndian
                ? BinaryUtils.ReadUInt32BE(data.AsSpan(offset + 4))
                : BinaryUtils.ReadUInt32LE(data.AsSpan(offset + 4));

            var flags = bigEndian
                ? BinaryUtils.ReadUInt32BE(data.AsSpan(offset + 8))
                : BinaryUtils.ReadUInt32LE(data.AsSpan(offset + 8));

            var recFormId = bigEndian
                ? BinaryUtils.ReadUInt32BE(data.AsSpan(offset + 12))
                : BinaryUtils.ReadUInt32LE(data.AsSpan(offset + 12));

            if (recFormId == formId)
                return new AnalyzerRecordInfo
                {
                    Signature = sig,
                    Offset = (uint)offset,
                    DataSize = dataSize,
                    Flags = flags,
                    FormId = recFormId,
                    TotalSize = EsmParser.MainRecordHeaderSize + dataSize
                };

            // Move to next record
            offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
        }

        return null;
    }
}