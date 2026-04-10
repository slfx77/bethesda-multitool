using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Utils;
using Spectre.Console;

namespace FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;

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
        if (offset + 4 <= a.Length && offset + 4 <= b.Length &&
            a[offset] == b[offset + 3] && a[offset + 1] == b[offset + 2] &&
            a[offset + 2] == b[offset + 1] && a[offset + 3] == b[offset])
        {
            return 4;
        }

        // 2-byte swap match
        if (offset + 2 <= a.Length && offset + 2 <= b.Length &&
            a[offset] == b[offset + 1] && a[offset + 1] == b[offset])
        {
            return 2;
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
            if (isSwapByte) sb.Append("~~");
            else if (isDiff) sb.Append("^^");
            else sb.Append("  ");
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
    ///     Attempts to interpret the difference between Xbox and PC data for known subrecord types.
    /// </summary>
    public static void TryInterpretDifference(string sig, byte[] xboxData, byte[] pcData, bool xboxBE, bool pcBE)
    {
        switch (sig)
        {
            case "EDID":
                var xboxStr = Encoding.ASCII.GetString(xboxData).TrimEnd('\0');
                var pcStr = Encoding.ASCII.GetString(pcData).TrimEnd('\0');
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
    ///     Finds a record by FormID in the file data.
    /// </summary>
    internal static AnalyzerRecordInfo? FindRecordByFormId(byte[] data, bool bigEndian, uint formId)
    {
        var offset = 0;
        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            var sig = bigEndian
                ? new string([
                    (char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]
                ])
                : Encoding.ASCII.GetString(data, offset, 4);

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
}
