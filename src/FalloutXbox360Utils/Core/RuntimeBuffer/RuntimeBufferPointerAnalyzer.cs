using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Coverage;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

/// <summary>
///     Pointer graph analysis for classifying pointer-dense memory regions.
/// </summary>
internal sealed class RuntimeBufferPointerAnalyzer
{
    private const int PointerScanChunkSize = 1024 * 1024;
    private readonly BufferAnalysisContext _ctx;

    public RuntimeBufferPointerAnalyzer(BufferAnalysisContext ctx)
    {
        _ctx = ctx;
    }

    #region Pointer Graph Analysis

    internal void RunStringOwnershipAnalysis(BufferExplorationResult result)
    {
        var meaningfulHits = result.StringHits
            .Where(hit => hit.IsMeaningfulCategory)
            .OrderBy(hit => hit.FileOffset)
            .ToList();

        var analysis = new RuntimeStringOwnershipAnalysis();
        analysis.AllHits.AddRange(meaningfulHits);

        if (meaningfulHits.Count == 0)
        {
            result.StringOwnership = analysis;
            return;
        }

        var hitsByFileOffset = meaningfulHits.ToDictionary(hit => hit.FileOffset);
        var hitsByVa = meaningfulHits
            .Where(hit => hit.VirtualAddress is >= 0 and <= uint.MaxValue)
            .GroupBy(hit => (uint)hit.VirtualAddress!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var referrersByVa = ScanInboundPointers(hitsByVa);
        var claimsByFileOffset = BuildDirectOwnerClaims(result, hitsByFileOffset);

        foreach (var hit in meaningfulHits)
        {
            PointerRefInfo? referrerInfo = null;
            if (hit.VirtualAddress is >= 0 and <= uint.MaxValue)
            {
                referrersByVa.TryGetValue((uint)hit.VirtualAddress.Value, out referrerInfo);
            }

            claimsByFileOffset.TryGetValue(hit.FileOffset, out var claim);

            hit.InboundPointerCount = referrerInfo?.Count ?? 0;

            if (claim != null)
            {
                hit.OwnershipStatus = RuntimeStringOwnershipStatus.Owned;
                hit.OwnerResolution = new RuntimeStringOwnerResolution
                {
                    OwnerKind = claim.OwnerKind,
                    OwnerName = claim.OwnerName,
                    OwnerFormId = claim.OwnerFormId,
                    OwnerFileOffset = claim.OwnerFileOffset,
                    ReferrerVa = referrerInfo?.ReferrerVa,
                    ReferrerFileOffset = referrerInfo?.ReferrerFileOffset,
                    ReferrerContext = referrerInfo?.ReferrerContext
                };
                analysis.OwnedHits.Add(hit);
            }
            else if (referrerInfo != null)
            {
                hit.OwnershipStatus = RuntimeStringOwnershipStatus.ReferencedOwnerUnknown;
                hit.OwnerResolution = new RuntimeStringOwnerResolution
                {
                    ReferrerVa = referrerInfo.ReferrerVa,
                    ReferrerFileOffset = referrerInfo.ReferrerFileOffset,
                    ReferrerContext = referrerInfo.ReferrerContext
                };
                analysis.ReferencedOwnerUnknownHits.Add(hit);
            }
            else
            {
                hit.OwnershipStatus = RuntimeStringOwnershipStatus.Unreferenced;
                hit.OwnerResolution = null;
                analysis.UnreferencedHits.Add(hit);
            }

            analysis.CategoryCounts.TryGetValue(hit.Category, out var categoryCount);
            analysis.CategoryCounts[hit.Category] = categoryCount + 1;

            analysis.StatusCounts.TryGetValue(hit.OwnershipStatus, out var statusCount);
            analysis.StatusCounts[hit.OwnershipStatus] = statusCount + 1;
        }

        result.StringOwnership = analysis;
    }

    /// <summary>
    ///     Analyze pointer-dense gaps to classify data structures.
    /// </summary>
    internal void RunPointerGraphAnalysis(BufferExplorationResult result)
    {
        var summary = new PointerGraphSummary();
        var vtableCounts = new Dictionary<uint, int>();

        var pointerGaps = _ctx.Coverage.Gaps
            .Where(g => g.Classification == GapClassification.PointerDense)
            .ToList();

        summary.TotalPointerDenseGaps = pointerGaps.Count;
        summary.TotalPointerDenseBytes = pointerGaps.Sum(g => g.Size);

        foreach (var gap in pointerGaps)
        {
            var sampleSize = (int)Math.Min(gap.Size, 256);
            sampleSize = sampleSize / 4 * 4; // Align to 4 bytes
            if (sampleSize < 4)
            {
                continue;
            }

            var buffer = new byte[sampleSize];
            _ctx.Accessor.ReadArray(gap.FileOffset, buffer, 0, sampleSize);

            var vtableCount = 0;
            var heapCount = 0;
            var nullCount = 0;
            var slots = sampleSize / 4;

            for (var i = 0; i < sampleSize; i += 4)
            {
                var val = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));

                if (val == 0)
                {
                    nullCount++;
                    continue;
                }

                if (val >= _ctx.ModuleStart && val < _ctx.ModuleEnd)
                {
                    vtableCount++;
                    vtableCounts.TryGetValue(val, out var c);
                    vtableCounts[val] = c + 1;
                    summary.TotalVtablePointersFound++;
                }
                else if (_ctx.IsValidPointer(val))
                {
                    heapCount++;
                }
            }

            // Classify gap based on pointer distribution
            if (vtableCount > 0 && vtableCount >= slots * 0.15)
            {
                summary.ObjectArrayGaps++;
            }
            else if (heapCount > slots * 0.4 && nullCount > slots * 0.15)
            {
                summary.HashTableGaps++;
            }
            else if (heapCount > slots * 0.5)
            {
                summary.LinkedListGaps++;
            }
            else
            {
                summary.MixedStructureGaps++;
            }
        }

        // Top vtable addresses (most frequently referenced)
        foreach (var (addr, count) in vtableCounts.OrderByDescending(kv => kv.Value).Take(10))
        {
            summary.TopVtableAddresses[addr] = count;
        }

        result.PointerGraph = summary;
    }

    #endregion

    #region Ownership Analysis Helpers

    private Dictionary<uint, PointerRefInfo> ScanInboundPointers(
        IReadOnlyDictionary<uint, RuntimeStringHit> hitsByVa)
    {
        var refs = new Dictionary<uint, PointerRefInfo>();
        if (hitsByVa.Count == 0)
        {
            return refs;
        }

        var pointerTargets = hitsByVa.Keys.ToHashSet();
        var buffer = new byte[PointerScanChunkSize];

        foreach (var region in _ctx.MinidumpInfo.MemoryRegions)
        {
            var alignDelta = (4 - (region.VirtualAddress & 3)) & 3;
            if (region.Size - alignDelta < 4)
            {
                continue;
            }

            var regionOffset = (long)alignDelta;
            while (regionOffset + 4 <= region.Size)
            {
                var remaining = region.Size - regionOffset;
                var readSize = (int)Math.Min(PointerScanChunkSize, remaining);
                readSize -= readSize % 4;
                if (readSize < 4)
                {
                    break;
                }

                _ctx.Accessor.ReadArray(region.FileOffset + regionOffset, buffer, 0, readSize);

                for (var i = 0; i <= readSize - 4; i += 4)
                {
                    var targetVa = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
                    if (!pointerTargets.Contains(targetVa))
                    {
                        continue;
                    }

                    var referrerFileOffset = region.FileOffset + regionOffset + i;
                    var referrerVa = region.VirtualAddress + regionOffset + i;

                    if (!refs.TryGetValue(targetVa, out var info))
                    {
                        info = new PointerRefInfo();
                        refs[targetVa] = info;
                    }

                    info.Count++;
                    if (info.ReferrerFileOffset == null)
                    {
                        info.ReferrerFileOffset = referrerFileOffset;
                        info.ReferrerVa = referrerVa;
                        info.ReferrerContext = DescribeReferrerContext(referrerFileOffset);
                    }
                }

                regionOffset += readSize;
            }
        }

        return refs;
    }

    private Dictionary<long, RuntimeStringOwnershipClaim> BuildDirectOwnerClaims(
        BufferExplorationResult result,
        Dictionary<long, RuntimeStringHit> hitsByFileOffset)
    {
        var claims = new Dictionary<long, RuntimeStringOwnershipClaim>();

        if (_ctx.RuntimeEditorIds != null)
        {
            foreach (var entry in _ctx.RuntimeEditorIds)
            {
                if (!hitsByFileOffset.TryGetValue(entry.StringOffset, out _))
                {
                    continue;
                }

                claims.TryAdd(entry.StringOffset, new RuntimeStringOwnershipClaim(
                    entry.StringOffset,
                    _ctx.MinidumpInfo.FileOffsetToVirtualAddress(entry.StringOffset),
                    "RuntimeEditorId",
                    entry.EditorId,
                    entry.FormId != 0 ? entry.FormId : null,
                    entry.TesFormOffset));
            }
        }

        foreach (var claim in result.ManagerResults.SelectMany(m => m.OwnedStringClaims))
        {
            if (!hitsByFileOffset.ContainsKey(claim.StringFileOffset))
            {
                continue;
            }

            claims.TryAdd(claim.StringFileOffset, claim);
        }

        return claims;
    }

    private string DescribeReferrerContext(long fileOffset)
    {
        var gap = _ctx.Coverage.Gaps.FirstOrDefault(g =>
            fileOffset >= g.FileOffset && fileOffset < g.FileOffset + g.Size);
        if (gap == null)
        {
            return "CapturedMemory";
        }

        return string.IsNullOrWhiteSpace(gap.Context)
            ? gap.Classification.ToString()
            : $"{gap.Classification}:{gap.Context}";
    }

    private sealed class PointerRefInfo
    {
        public int Count { get; set; }
        public long? ReferrerVa { get; set; }
        public long? ReferrerFileOffset { get; set; }
        public string? ReferrerContext { get; set; }
    }

    #endregion
}
