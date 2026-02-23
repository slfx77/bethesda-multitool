namespace FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;

/// <summary>
///     Detects and analyzes byte-swap and endian patterns in ESM diff data.
/// </summary>
public static class DiffPatternAnalyzer
{
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
                while (offset + swapCount * 4 + 4 <= xbox.Length)
                {
                    var o = offset + swapCount * 4;
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

                segments.Add(swapCount == 1 ? $"4B swap @0x{offset:X}" : $"{swapCount}x4B swap @0x{offset:X}");
                offset += swapCount * 4;
                continue;
            }

            // Try 2-byte swap
            if (offset + 2 <= xbox.Length && xbox[offset] == pc[offset + 1] && xbox[offset + 1] == pc[offset])
            {
                // Check if multiple consecutive 2-byte swaps
                var swapCount = 1;
                while (offset + swapCount * 2 + 2 <= xbox.Length)
                {
                    var o = offset + swapCount * 2;
                    if (xbox[o] == pc[o + 1] && xbox[o + 1] == pc[o])
                    {
                        swapCount++;
                    }
                    else
                    {
                        break;
                    }
                }

                segments.Add(swapCount == 1 ? $"2B swap @0x{offset:X}" : $"{swapCount}x2B swap @0x{offset:X}");
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
#pragma warning disable S127 // Loop counters are intentionally advanced within windowed pattern matching
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
                var head = fourByte.Count == 1 ? "4B swap" : $"{fourByte.Count}x4B swap";
                parts.Add($"{head} @0x{fourByte[0]:X}");
            }

            if (nearFourByte.Count > 0)
            {
                var head = nearFourByte.Count == 1 ? "4B swap+1B diff" : $"{nearFourByte.Count}x4B swap+1B diff";
                parts.Add($"{head} @0x{nearFourByte[0]:X}");
            }

            if (twoByte.Count > 0)
            {
                var head = twoByte.Count == 1 ? "2B swap" : $"{twoByte.Count}x2B swap";
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
#pragma warning restore S127

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
