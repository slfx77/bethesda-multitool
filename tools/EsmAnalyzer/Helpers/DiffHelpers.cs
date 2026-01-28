using EsmAnalyzer.Conversion.Schema;
using EsmAnalyzer.Core;
using Spectre.Console;
using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Utils;

namespace EsmAnalyzer.Helpers;

/// <summary>
///     Utilities for comparing and analyzing byte-level differences in ESM data.
/// </summary>
public static class DiffHelpers
{
    /// <summary>
    ///     Adds a flag comparison row to a Spectre.Console table.
    /// </summary>
    public static void AddFlagRow(Table table, int bit, uint mask, string name, uint xboxFlags, uint pcFlags)
    {
        var xboxSet = (xboxFlags & mask) != 0;
        var pcSet = (pcFlags & mask) != 0;
        _ = table.AddRow(
            bit.ToString(),
            $"0x{mask:X8}",
            name,
            xboxSet ? "[green]SET[/]" : "[grey]not set[/]",
            pcSet ? "[green]SET[/]" : "[grey]not set[/]"
        );
    }

    /// <summary>
    ///     Formats a range of bytes as a hex string.
    /// </summary>
    public static string FormatBytes(byte[] data, int offset, int length)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < length && offset + i < data.Length; i++)
        {
            if (i > 0)
            {
                _ = sb.Append(' ');
            }

            _ = sb.Append(data[offset + i].ToString("X2"));
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Finds the offset of the first differing byte between two arrays.
    ///     Returns -1 if arrays are identical.
    /// </summary>
    public static int FindFirstDifferenceOffset(byte[] a, byte[] b)
    {
        var min = Math.Min(a.Length, b.Length);
        for (var i = 0; i < min; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return a.Length == b.Length ? -1 : min;
    }

    /// <summary>
    ///     Checks if bytes at the given offset match when swapped (2 or 4 byte).
    ///     Returns the swap length (2, 4) or 0 if no match.
    /// </summary>
    public static int GetSwapMatchLengthAtOffset(byte[] a, byte[] b, int offset)
    {
        if (offset < 0)
        {
            return 0;
        }

        // 4-byte swap match
        if (offset + 4 <= a.Length && offset + 4 <= b.Length)
        {
            if (a[offset] == b[offset + 3] && a[offset + 1] == b[offset + 2] &&
                a[offset + 2] == b[offset + 1] && a[offset + 3] == b[offset])
            {
                return 4;
            }
        }

        // 2-byte swap match
        if (offset + 2 <= a.Length && offset + 2 <= b.Length)
        {
            if (a[offset] == b[offset + 1] && a[offset + 1] == b[offset])
            {
                return 2;
            }
        }

        return 0;
    }

    /// <summary>
    ///     Formats bytes with diff highlighting (Spectre markup).
    /// </summary>
    public static string FormatBytesDiffHighlighted(byte[] a, byte[] b, int offset, int length,
        int primaryDiffOffset = -1)
    {
        return FormatBytesDiffHighlighted(a, b, offset, length, primaryDiffOffset, null);
    }

    /// <summary>
    ///     Formats bytes with diff highlighting and swap region support.
    ///     Returns a Spectre markup string - do NOT Markup.Escape this.
    ///     - differing bytes outside swap regions are [red]
    ///     - bytes explained by swap regions are [yellow]
    /// </summary>
    public static string FormatBytesDiffHighlighted(
        byte[] a,
        byte[] b,
        int offset,
        int length,
        int primaryDiffOffset,
        IReadOnlySet<int>? swapByteOffsets)
    {
        var localSwapLen = primaryDiffOffset >= 0 ? GetSwapMatchLengthAtOffset(a, b, primaryDiffOffset) : 0;
        var localSwapStart = primaryDiffOffset;
        var localSwapEnd = localSwapLen > 0 ? primaryDiffOffset + localSwapLen : -1;

        var sb = new StringBuilder();
        for (var i = 0; i < length; i++)
        {
            var idx = offset + i;
            if (idx >= a.Length && idx >= b.Length)
            {
                break;
            }

            if (i > 0)
            {
                _ = sb.Append(' ');
            }

            var aByte = idx < a.Length ? a[idx] : (byte?)null;
            var bByte = idx < b.Length ? b[idx] : (byte?)null;

            var isDiff = aByte != bByte;

            var isSwapByte = swapByteOffsets != null
                ? swapByteOffsets.Contains(idx)
                : localSwapLen > 0 && idx >= localSwapStart && idx < localSwapEnd;
            var hex = (aByte ?? 0).ToString("X2");

            if (isSwapByte)
            {
                _ = sb.Append("[yellow]");
                _ = sb.Append(hex);
                _ = sb.Append("[/]");
            }
            else if (!isDiff)
            {
                _ = sb.Append(hex);
            }
            else
            {
                _ = sb.Append("[red]");
                _ = sb.Append(hex);
                _ = sb.Append("[/]");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Returns plain ASCII markers aligned to a byte dump:
    ///     - ^^ : true diff (not swap-explained)
    ///     - ~~ : swap-explained
    ///     - "  " : identical and not swap-explained
    /// </summary>
    public static string FormatBytesDiffMarkers(
        byte[] a,
        byte[] b,
        int offset,
        int length,
        int primaryDiffOffset,
        IReadOnlySet<int>? swapByteOffsets)
    {
        var localSwapLen = primaryDiffOffset >= 0 ? GetSwapMatchLengthAtOffset(a, b, primaryDiffOffset) : 0;
        var localSwapStart = primaryDiffOffset;
        var localSwapEnd = localSwapLen > 0 ? primaryDiffOffset + localSwapLen : -1;

        var sb = new StringBuilder();
        for (var i = 0; i < length; i++)
        {
            var idx = offset + i;
            if (idx >= a.Length && idx >= b.Length)
            {
                break;
            }

            if (i > 0)
            {
                _ = sb.Append(' ');
            }

            var aByte = idx < a.Length ? a[idx] : (byte?)null;
            var bByte = idx < b.Length ? b[idx] : (byte?)null;

            var isDiff = aByte != bByte;

            var isSwapByte = swapByteOffsets != null
                ? swapByteOffsets.Contains(idx)
                : localSwapLen > 0 && idx >= localSwapStart && idx < localSwapEnd;
            _ = isSwapByte ? sb.Append("~~") : isDiff ? sb.Append("^^") : sb.Append("  ");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Calculates a context window around a diff offset.
    /// </summary>
    public static (int Start, int Length) GetContextWindow(int diffOffset, int dataLength, int before = 16,
        int after = 16)
    {
        if (diffOffset < 0)
        {
            return (0, Math.Min(dataLength, before + after));
        }

        var start = Math.Max(0, diffOffset - before);
        var endExclusive = Math.Min(dataLength, diffOffset + after);
        return (start, Math.Max(0, endExclusive - start));
    }

    /// <summary>
    ///     Describes which schema field is at the given offset within a subrecord.
    /// </summary>
    public static string? DescribeSchemaAtOffset(string subrecordSignature, string recordType, int dataLength,
        int offset)
    {
        var schema = SubrecordSchemaRegistry.GetSchema(subrecordSignature, recordType, dataLength);
        if (schema == null)
        {
            return null;
        }

        if (schema.Fields.Length == 0)
        {
            return schema.Description;
        }

        // Repeating array schemas (ExpectedSize = -1) use the element fields as a template.
        if (schema.ExpectedSize == -1)
        {
            var elementSize = schema.Fields.Sum(f => f.EffectiveSize);
            if (elementSize <= 0)
            {
                return schema.Description;
            }

            var elementIndex = offset / elementSize;
            var elementOffset = offset % elementSize;

            var fieldOffset = 0;
            foreach (var field in schema.Fields)
            {
                var size = field.EffectiveSize;
                if (size <= 0)
                {
                    break;
                }

                if (elementOffset >= fieldOffset && elementOffset < fieldOffset + size)
                {
                    var inner = elementOffset - fieldOffset;
                    var innerSuffix = inner == 0 ? string.Empty : $" (+0x{inner:X})";
                    return $"{field.Name}[{elementIndex}] : {field.Type}{innerSuffix}";
                }

                fieldOffset += size;
            }

            return schema.Description;
        }

        var running = 0;
        foreach (var field in schema.Fields)
        {
            var size = field.EffectiveSize;
            if (size <= 0)
            {
                break;
            }

            if (offset >= running && offset < running + size)
            {
                var inner = offset - running;
                var innerSuffix = inner == 0 ? string.Empty : $" (+0x{inner:X})";
                return $"{field.Name} : {field.Type}{innerSuffix}";
            }

            running += size;
        }

        return schema.Description;
    }

    /// <summary>
    ///     Analyzes byte arrays to detect structured conversion patterns.
    ///     Returns a description like "4B same + 4B swap" or empty string if no clear pattern.
    /// </summary>
    public static string AnalyzeStructuredDifference(byte[] xbox, byte[] pc)
    {
        if (xbox.Length != pc.Length || xbox.Length < 4)
        {
            return string.Empty;
        }

        var segments = new List<string>();
        var offset = 0;

        while (offset < xbox.Length)
        {
            // Try to detect identical bytes
            var identicalCount = 0;
            while (offset + identicalCount < xbox.Length &&
                   xbox[offset + identicalCount] == pc[offset + identicalCount])
            {
                identicalCount++;
            }

            if (identicalCount > 0)
            {
                segments.Add($"{identicalCount}B same");
                offset += identicalCount;
                continue;
            }

            // Try 4-byte swap
            if (offset + 4 <= xbox.Length &&
                xbox[offset] == pc[offset + 3] && xbox[offset + 1] == pc[offset + 2] &&
                xbox[offset + 2] == pc[offset + 1] && xbox[offset + 3] == pc[offset])
            {
                // Check if multiple consecutive 4-byte swaps
                var swapCount = 1;
                while (offset + (swapCount * 4) + 4 <= xbox.Length)
                {
                    var o = offset + (swapCount * 4);
                    if (xbox[o] == pc[o + 3] && xbox[o + 1] == pc[o + 2] &&
                        xbox[o + 2] == pc[o + 1] && xbox[o + 3] == pc[o])
                    {
                        swapCount++;
                    }
                    else
                    {
                        break;
                    }
                }

                segments.Add(swapCount == 1 ? $"4B swap @0x{offset:X}" : $"{swapCount}×4B swap @0x{offset:X}");
                offset += swapCount * 4;
                continue;
            }

            // Try 2-byte swap
            if (offset + 2 <= xbox.Length && xbox[offset] == pc[offset + 1] && xbox[offset + 1] == pc[offset])
            {
                // Check if multiple consecutive 2-byte swaps
                var swapCount = 1;
                while (offset + (swapCount * 2) + 2 <= xbox.Length)
                {
                    var o = offset + (swapCount * 2);
                    if (xbox[o] == pc[o + 1] && xbox[o + 1] == pc[o])
                    {
                        swapCount++;
                    }
                    else
                    {
                        break;
                    }
                }

                segments.Add(swapCount == 1 ? $"2B swap @0x{offset:X}" : $"{swapCount}×2B swap @0x{offset:X}");
                offset += swapCount * 2;
                continue;
            }

            // Unknown difference - can't determine pattern
            return string.Empty;
        }

        return segments.Count > 0 ? string.Join(" + ", segments) : string.Empty;
    }

    /// <summary>
    ///     Analyzes byte arrays to detect all swap patterns and diff statistics.
    /// </summary>
    public static DiffPatternInfo AnalyzeDiffPatterns(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        if (len <= 0)
        {
            return new DiffPatternInfo
            {
                SwapRanges = [],
                NearSwapRanges = [],
                SwapByteOffsetsA = new HashSet<int>(),
                SwapByteOffsetsB = new HashSet<int>(),
                TotalDiffBytes = a.Length == b.Length ? 0 : Math.Abs(a.Length - b.Length),
                DiffBytesOutsideSwap = a.Length == b.Length ? 0 : Math.Abs(a.Length - b.Length),
                Summary = string.Empty
            };
        }

        var windowClaimed = new bool[len];
        var swapExplainedA = new bool[len];
        var swapExplainedB = new bool[len];
        var ranges = new List<SwapRange>();
        var nearRanges = new List<SwapRange>();

        // 4-byte reverse matches (only when the region is actually different in-place)
        for (var i = 0; i + 4 <= len; i++)
        {
            if (windowClaimed[i] || windowClaimed[i + 1] || windowClaimed[i + 2] || windowClaimed[i + 3])
            {
                continue;
            }

            if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2] && a[i + 3] == b[i + 3])
            {
                continue;
            }

            if (a[i] == b[i + 3] && a[i + 1] == b[i + 2] && a[i + 2] == b[i + 1] && a[i + 3] == b[i])
            {
                ranges.Add(new SwapRange(i, 4));
                windowClaimed[i] = windowClaimed[i + 1] = windowClaimed[i + 2] = windowClaimed[i + 3] = true;
                swapExplainedA[i] = swapExplainedA[i + 1] = swapExplainedA[i + 2] = swapExplainedA[i + 3] = true;
                swapExplainedB[i] = swapExplainedB[i + 1] = swapExplainedB[i + 2] = swapExplainedB[i + 3] = true;
                i += 3;
            }
        }

        // 4-byte near reverse matches: 3/4 bytes match when reversed
        for (var i = 0; i + 4 <= len; i++)
        {
            if (windowClaimed[i] || windowClaimed[i + 1] || windowClaimed[i + 2] || windowClaimed[i + 3])
            {
                continue;
            }

            if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2] && a[i + 3] == b[i + 3])
            {
                continue;
            }

            var inPlaceDiff = 0;
            if (a[i] != b[i])
            {
                inPlaceDiff++;
            }

            if (a[i + 1] != b[i + 1])
            {
                inPlaceDiff++;
            }

            if (a[i + 2] != b[i + 2])
            {
                inPlaceDiff++;
            }

            if (a[i + 3] != b[i + 3])
            {
                inPlaceDiff++;
            }

            if (inPlaceDiff < 2)
            {
                continue;
            }

            var match0 = a[i] == b[i + 3];
            var match1 = a[i + 1] == b[i + 2];
            var match2 = a[i + 2] == b[i + 1];
            var match3 = a[i + 3] == b[i];
            var reverseMatches = (match0 ? 1 : 0) + (match1 ? 1 : 0) + (match2 ? 1 : 0) + (match3 ? 1 : 0);
            if (reverseMatches < 3)
            {
                continue;
            }

            nearRanges.Add(new SwapRange(i, 4));
            windowClaimed[i] = windowClaimed[i + 1] = windowClaimed[i + 2] = windowClaimed[i + 3] = true;

            if (match0)
            {
                swapExplainedA[i] = true;
                swapExplainedB[i + 3] = true;
            }

            if (match1)
            {
                swapExplainedA[i + 1] = true;
                swapExplainedB[i + 2] = true;
            }

            if (match2)
            {
                swapExplainedA[i + 2] = true;
                swapExplainedB[i + 1] = true;
            }

            if (match3)
            {
                swapExplainedA[i + 3] = true;
                swapExplainedB[i] = true;
            }

            i += 3;
        }

        // 2-byte reverse matches
        for (var i = 0; i + 2 <= len; i++)
        {
            if (windowClaimed[i] || windowClaimed[i + 1])
            {
                continue;
            }

            if (a[i] == b[i] && a[i + 1] == b[i + 1])
            {
                continue;
            }

            if (a[i] == b[i + 1] && a[i + 1] == b[i])
            {
                ranges.Add(new SwapRange(i, 2));
                windowClaimed[i] = windowClaimed[i + 1] = true;
                swapExplainedA[i] = swapExplainedA[i + 1] = true;
                swapExplainedB[i] = swapExplainedB[i + 1] = true;
                i += 1;
            }
        }

        // Count differences
        var totalDiff = 0;
        var outsideSwapDiff = 0;
        for (var i = 0; i < len; i++)
        {
            if (a[i] == b[i])
            {
                continue;
            }

            totalDiff++;
            if (!swapExplainedA[i])
            {
                outsideSwapDiff++;
            }
        }

        if (a.Length != b.Length)
        {
            outsideSwapDiff += Math.Abs(a.Length - b.Length);
        }

        var summary = string.Empty;
        if (ranges.Count > 0 || nearRanges.Count > 0)
        {
            var fourByte = ranges.Where(r => r.Length == 4).Select(r => r.Start).OrderBy(x => x).ToList();
            var twoByte = ranges.Where(r => r.Length == 2).Select(r => r.Start).OrderBy(x => x).ToList();
            var nearFourByte = nearRanges.Where(r => r.Length == 4).Select(r => r.Start).OrderBy(x => x).ToList();
            var parts = new List<string>();
            if (fourByte.Count > 0)
            {
                var head = fourByte.Count == 1 ? "4B swap" : $"{fourByte.Count}×4B swap";
                parts.Add($"{head} @0x{fourByte[0]:X}");
            }

            if (nearFourByte.Count > 0)
            {
                var head = nearFourByte.Count == 1 ? "4B swap+1B diff" : $"{nearFourByte.Count}×4B swap+1B diff";
                parts.Add($"{head} @0x{nearFourByte[0]:X}");
            }

            if (twoByte.Count > 0)
            {
                var head = twoByte.Count == 1 ? "2B swap" : $"{twoByte.Count}×2B swap";
                parts.Add($"{head} @0x{twoByte[0]:X}");
            }

            summary = string.Join(" + ", parts);

            if (outsideSwapDiff > 0)
            {
                summary = $"MIXED: {outsideSwapDiff}B diff + {summary}";
            }
        }

        var swapBytesA = new HashSet<int>();
        var swapBytesB = new HashSet<int>();
        for (var i = 0; i < len; i++)
        {
            if (swapExplainedA[i])
            {
                _ = swapBytesA.Add(i);
            }

            if (swapExplainedB[i])
            {
                _ = swapBytesB.Add(i);
            }
        }

        return new DiffPatternInfo
        {
            SwapRanges = ranges,
            NearSwapRanges = nearRanges,
            SwapByteOffsetsA = swapBytesA,
            SwapByteOffsetsB = swapBytesB,
            TotalDiffBytes = totalDiff,
            DiffBytesOutsideSwap = outsideSwapDiff,
            Summary = summary
        };
    }

    /// <summary>
    ///     Attempts to interpret the difference between Xbox and PC data for known subrecord types.
    /// </summary>
    public static void TryInterpretDifference(string sig, byte[] xboxData, byte[] pcData, bool xboxBE, bool pcBE)
    {
        switch (sig)
        {
            case "EDID":
                var xboxStr = System.Text.Encoding.ASCII.GetString(xboxData).TrimEnd('\0');
                var pcStr = System.Text.Encoding.ASCII.GetString(pcData).TrimEnd('\0');
                if (xboxStr != pcStr)
                {
                    AnsiConsole.MarkupLine($"    [grey]String: Xbox='{xboxStr}' PC='{pcStr}'[/]");
                }

                break;

            case "DATA" when xboxData.Length == 4:
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

            case "VHGT" when xboxData.Length >= 4:
                var xboxOffset =
                    BitConverter.ToSingle(BitConverter.GetBytes(BinaryUtils.ReadUInt32BE(xboxData.AsSpan())), 0);
                var pcOffset = BitConverter.ToSingle(pcData, 0);
                AnsiConsole.MarkupLine($"    [grey]Height offset: Xbox={xboxOffset:F2} PC={pcOffset:F2}[/]");
                break;
        }
    }

    /// <summary>
    ///     Checks if the data appears to be a simple endian swap.
    /// </summary>
    public static bool CheckEndianSwapped(byte[] xbox, byte[] pc)
    {
        if (xbox.Length != pc.Length)
        {
            return false;
        }

        if (xbox.Length == 2)
        {
            return xbox[0] == pc[1] && xbox[1] == pc[0];
        }

        if (xbox.Length == 4)
        {
            return xbox[0] == pc[3] && xbox[1] == pc[2] && xbox[2] == pc[1] && xbox[3] == pc[0];
        }

        if (xbox.Length % 4 == 0)
        {
            for (var i = 0; i < xbox.Length; i += 4)
            {
                if (xbox[i] != pc[i + 3] || xbox[i + 1] != pc[i + 2] ||
                    xbox[i + 2] != pc[i + 1] || xbox[i + 3] != pc[i])
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    ///     Finds a record by FormID in the file data.
    /// </summary>
    public static AnalyzerRecordInfo? FindRecordByFormId(byte[] data, bool bigEndian, uint formId)
    {
        var offset = 0;
        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            var sig = bigEndian
                ? new string([
                    (char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]
                ])
                : System.Text.Encoding.ASCII.GetString(data, offset, 4);

            if (sig == "GRUP")
            {
                offset += 24;
                continue;
            }

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
            {
                return new AnalyzerRecordInfo
                {
                    Signature = sig,
                    Offset = (uint)offset,
                    DataSize = dataSize,
                    Flags = flags,
                    FormId = recFormId,
                    TotalSize = EsmParser.MainRecordHeaderSize + dataSize
                };
            }

            offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
        }

        return null;
    }

    /// <summary>
    ///     Represents a swap range detected in byte comparison.
    /// </summary>
    public readonly record struct SwapRange(int Start, int Length)
    {
        public int EndExclusive => Start + Length;

        public bool Contains(int index)
        {
            return index >= Start && index < EndExclusive;
        }
    }

    /// <summary>
    ///     Contains analysis results from diff pattern detection.
    /// </summary>
    public sealed class DiffPatternInfo
    {
        public required List<SwapRange> SwapRanges { get; init; }
        public required List<SwapRange> NearSwapRanges { get; init; }
        public required IReadOnlySet<int> SwapByteOffsetsA { get; init; }
        public required IReadOnlySet<int> SwapByteOffsetsB { get; init; }
        public required int TotalDiffBytes { get; init; }
        public required int DiffBytesOutsideSwap { get; init; }
        public required string Summary { get; init; }
    }
}
