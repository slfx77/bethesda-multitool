using System.Buffers.Binary;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     Pointer graph analysis for classifying pointer-dense memory regions.
/// </summary>
internal sealed class RuntimeBufferPointerAnalyzer
{
    private const int PointerScanChunkSize = 1024 * 1024;
    private readonly BufferAnalysisContext _ctx;

    /// <summary>
    ///     Coverage gaps sorted by file offset, with their start offsets split out for
    ///     <see cref="Array.BinarySearch{T}(T[], T)" />. Built on first use; see
    ///     <see cref="DescribeReferrerContext" />.
    /// </summary>
    private CoverageGap[]? _gapsByStart;

    private long[]? _gapStarts;

    public RuntimeBufferPointerAnalyzer(BufferAnalysisContext ctx)
    {
        _ctx = ctx;
    }

    #region Pointer Graph Analysis

    /// <summary>
    ///     Classify every extracted string by whether anything in the dump points at it, and by
    ///     which runtime owner claims it.
    ///     <para>
    ///         This used to run only on hits whose <c>Category</c> a shape heuristic had already
    ///         recognised (<c>IsMeaningfulCategory</c>, i.e. anything but <c>Other</c>). That made
    ///         the headline "unattributed text" number unanswerable in principle: on xex44, 550,335
    ///         of 621,662 unique strings — 88.5% — are <c>Other</c>, so the analysis never asked
    ///         whether the majority of the dump's text ties back to anything. And the classifier is
    ///         deliberately conservative in ways that matter here: a dialogue line needs 25+
    ///         characters *with spaces*, so "Yeah." and "I'm listening." are <c>Other</c>; an
    ///         EditorID needs 6+ characters starting uppercase, so short or lowercase ones are too.
    ///         "Unattributed" was therefore partly a statement about the classifier.
    ///     </para>
    ///     <para>
    ///         Including everything is nearly free: <see cref="ScanInboundPointers" /> is one pass
    ///         over the dump's memory regions testing each 4-byte word against a hash set, so cost
    ///         is driven by dump size, not hit count — a 621k-entry target set instead of a 71k one.
    ///         The category is preserved on every hit, so reports can still separate recognised text
    ///         from the rest; what changes is that the rest now has a measured answer.
    ///     </para>
    /// </summary>
    internal void RunStringOwnershipAnalysis(BufferExplorationResult result)
    {
        var meaningfulHits = result.StringHits
            .OrderBy(hit => hit.FileOffset)
            .ToList();

        var analysis = new RuntimeStringOwnershipAnalysis();
        analysis.AllHits.AddRange(meaningfulHits);

        if (meaningfulHits.Count == 0)
        {
            result.StringOwnership = analysis;
            return;
        }

        // First-wins rather than ToDictionary: two hits can share a file offset (a string and a
        // suffix of it both start there once every category is in scope), and ToDictionary would
        // throw on the duplicate. The VA index below has always taken the same precaution.
        var hitsByFileOffset = meaningfulHits
            .GroupBy(hit => hit.FileOffset)
            .ToDictionary(group => group.Key, group => group.First());
        var hitsByVa = meaningfulHits
            .Where(hit => hit.VirtualAddress is >= 0 and <= uint.MaxValue)
            .GroupBy(hit => (uint)hit.VirtualAddress!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // Walking the module's RTTI tables costs ~20 MB of reads and yields every class the build
        // defines. Null for a dump with no captured game module, in which case the object sweep and
        // census below are simply skipped and nothing else changes.
        var rtti = DumpRttiIndex.Build(
            _ctx.MinidumpInfo, new MmfMemoryAccessor(_ctx.Accessor), _ctx.FileSize);

        var scan = ScanInboundPointers(hitsByVa, rtti);
        var referrersByVa = scan.Referrers;
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
                    ClaimSource = claim.ClaimSource,
                    OwnerRecordType = claim.OwnerRecordType,
                    OwnerFieldOrSubrecord = claim.OwnerFieldOrSubrecord,
                    ReferrerVa = referrerInfo?.ReferrerVa,
                    ReferrerFileOffset = referrerInfo?.ReferrerFileOffset,
                    ReferrerContext = referrerInfo?.ReferrerContext,
                    AllReferrers = referrerInfo?.AllReferrers
                };
                analysis.OwnedHits.Add(hit);

                analysis.ClaimSourceCounts.TryGetValue(claim.ClaimSource, out var sourceCount);
                analysis.ClaimSourceCounts[claim.ClaimSource] = sourceCount + 1;
            }
            else if (referrerInfo != null)
            {
                hit.OwnershipStatus = RuntimeStringOwnershipStatus.ReferencedOwnerUnknown;
                hit.OwnerResolution = new RuntimeStringOwnerResolution
                {
                    ReferrerVa = referrerInfo.ReferrerVa,
                    ReferrerFileOffset = referrerInfo.ReferrerFileOffset,
                    ReferrerContext = referrerInfo.ReferrerContext,
                    AllReferrers = referrerInfo.AllReferrers
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

        // Second pass: resolve remaining unknowns via BSStringT, vtable, and text-content strategies
        var secondPass = new SecondPassOwnershipResolver(_ctx);
        secondPass.Resolve(analysis);

        // Measure — do not claim. The census runs on what is STILL unowned after every strategy,
        // which is the population any future work would have to name.
        if (rtti != null)
        {
            analysis.ObjectCensus = BuildObjectCensus(rtti, scan.ObjectHits, analysis);
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

    /// <summary>
    ///     One pass over every captured region, asking two questions of each 4-aligned word: does it
    ///     point at a string we extracted, and is it a vtable address?
    ///     <para>
    ///         The second question rides along for almost nothing. Every vtable in the module sits
    ///         inside a band of roughly 1.5 MB, so two comparisons reject essentially every word
    ///         before a hash probe is needed — and the alternative, a separate sweep, would mean
    ///         reading the whole dump twice.
    ///     </para>
    /// </summary>
    private InboundScanResult ScanInboundPointers(
        IReadOnlyDictionary<uint, RuntimeStringHit> hitsByVa, DumpRttiIndex? rtti)
    {
        var refs = new Dictionary<uint, PointerRefInfo>();
        var objectHits = new List<(uint BaseVa, int ClassId)>();
        if (hitsByVa.Count == 0)
        {
            return new InboundScanResult(refs, objectHits);
        }

        var vtableMin = rtti?.MinVtableVa ?? 0;
        var vtableMax = rtti?.MaxVtableVa ?? 0;
        var pointerTargets = hitsByVa.Keys.ToHashSet();
        var buffer = new byte[PointerScanChunkSize];

        foreach (var region in _ctx.MinidumpInfo.MemoryRegions)
        {
            var alignDelta = (4 - (region.VirtualAddress & 3)) & 3;
            if (region.Size - alignDelta < 4)
            {
                continue;
            }

            var regionOffset = alignDelta;
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

                    if (rtti != null && targetVa >= vtableMin && targetVa <= vtableMax &&
                        rtti.TryGetVtable(targetVa, out var vtable))
                    {
                        // A secondary vtable sits ObjectOffset bytes into the complete object, so
                        // the object begins that much EARLIER than the slot holding it.
                        var slotVa = unchecked((uint)(region.VirtualAddress + regionOffset + i));
                        if (slotVa >= vtable.ObjectOffset)
                        {
                            objectHits.Add((slotVa - vtable.ObjectOffset, vtable.ClassId));
                        }
                    }

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
                    var context = DescribeReferrerContext(referrerFileOffset);
                    if (info.ReferrerFileOffset == null)
                    {
                        info.ReferrerFileOffset = referrerFileOffset;
                        info.ReferrerVa = referrerVa;
                        info.ReferrerContext = context;
                    }

                    info.AllReferrers ??= [];
                    if (info.AllReferrers.Count < 32)
                    {
                        info.AllReferrers.Add((referrerFileOffset, referrerVa, context));
                    }
                }

                regionOffset += readSize;
            }
        }

        return new InboundScanResult(refs, objectHits);
    }

    /// <summary>
    ///     Measure how much of the still-unowned string population the dump's objects could account
    ///     for. Produces numbers only — no claim is made and no hit changes status here.
    /// </summary>
    private static RuntimeObjectCensus BuildObjectCensus(
        DumpRttiIndex rtti,
        List<(uint BaseVa, int ClassId)> objectHits,
        RuntimeStringOwnershipAnalysis analysis)
    {
        var inventory = RuntimeObjectInventory.Build(rtti, objectHits);

        var census = new RuntimeObjectCensus
        {
            TypeDescriptorCount = rtti.TypeDescriptorCount,
            TypeDescriptorsWithNoColCount = rtti.TypeDescriptorsWithNoColCount,
            VtableCount = rtti.VtableCount,
            SecondaryVtableCount = rtti.VtableCount - rtti.PrimaryVtableCount,
            ClassCount = rtti.Classes.Count,
            ModuleBytesCaptured = rtti.ModuleBytesCaptured,
            ModuleBytesDeclared = rtti.ModuleBytesDeclared,
            MinVtableVa = rtti.MinVtableVa,
            MaxVtableVa = rtti.MaxVtableVa,
            RawVtableWordHits = inventory.RawHitCount,
            ObjectCount = inventory.ObjectCount,
            ObjectsWithDeclaredSize = inventory.DeclaredSizeCount,
            AmbiguousBaseCount = inventory.AmbiguousBaseCount,
            LiveClassCount = inventory.Spans.Select(s => s.ClassId).Distinct().Count()
        };

        foreach (var span in inventory.Spans)
        {
            var band = $"0x{span.BaseVa >> 24:X2}";
            census.ObjectsByVaBand[band] = census.ObjectsByVaBand.GetValueOrDefault(band) + 1;

            var alignment = (int)(span.BaseVa & 15);
            census.BaseAlignmentHistogram[alignment] =
                census.BaseAlignmentHistogram.GetValueOrDefault(alignment) + 1;
        }

        foreach (var hit in analysis.ReferencedOwnerUnknownHits)
        {
            census.UnknownHitsExamined++;

            var referrers = hit.OwnerResolution?.AllReferrers;
            if (referrers is not { Count: > 0 })
            {
                continue;
            }

            var heapReferrers = 0;
            var moduleReferrers = 0;
            RuntimeObjectSpan? container = null;

            foreach (var (_, va, _) in referrers)
            {
                // Module-space VAs are stored sign-extended, so they arrive negative — and every
                // ownership strategy drops them on exactly this test. Counting them here is the
                // whole point: a string reachable only from a global is unnameable by construction.
                if (va is < 0 or > uint.MaxValue)
                {
                    moduleReferrers++;
                    continue;
                }

                heapReferrers++;
                if (container is null && inventory.TryFind((uint)va, out var span))
                {
                    container = span;
                }
            }

            if (moduleReferrers > 0 && heapReferrers == 0)
            {
                census.ModuleOnlyReferrerHits++;
            }
            else if (moduleReferrers > 0)
            {
                census.MixedReferrerHits++;
            }
            else
            {
                census.HeapOnlyReferrerHits++;
            }

            if (container is not { } found)
            {
                continue;
            }

            census.UnknownHitsWithReferrerInObject++;
            if (found.SizeIsDeclared)
            {
                census.UnknownHitsWithDeclaredSizeContainer++;
            }

            var className = inventory.ClassName(found.ClassId);
            census.ContainingClassCounts[className] =
                census.ContainingClassCounts.GetValueOrDefault(className) + 1;
        }

        return census;
    }

    private sealed record InboundScanResult(
        Dictionary<uint, PointerRefInfo> Referrers,
        List<(uint BaseVa, int ClassId)> ObjectHits);

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
                    entry.TesFormOffset,
                    // Stated explicitly: omitting it took the record's default (ManagerGlobal), so
                    // the largest bucket in ClaimSourceCounts was reported as something it is not
                    // and ClaimSource.RuntimeEditorId was assigned nowhere in the codebase.
                    ClaimSource.RuntimeEditorId));
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

        if (_ctx.RuntimeEditorIds is { Count: > 0 })
        {
            var memoryContext = new RuntimeMemoryContext(
                new MmfMemoryAccessor(_ctx.Accessor),
                _ctx.FileSize,
                _ctx.MinidumpInfo);

            foreach (var claim in RuntimeStructStringClaimExtractor.ExtractClaims(_ctx.RuntimeEditorIds,
                         memoryContext))
            {
                if (hitsByFileOffset.ContainsKey(claim.StringFileOffset))
                {
                    claims.TryAdd(claim.StringFileOffset, claim);
                }
            }

            foreach (var claim in RuntimeNestedStringClaimExtractor.ExtractClaims(_ctx.RuntimeEditorIds,
                         memoryContext))
            {
                if (hitsByFileOffset.ContainsKey(claim.StringFileOffset))
                {
                    claims.TryAdd(claim.StringFileOffset, claim);
                }
            }
        }

        if (_ctx.MainRecords is { Count: > 0 })
        {
            foreach (var claim in RawRecordStringClaimExtractor.ExtractClaims(
                         _ctx.MainRecords,
                         _ctx.Accessor,
                         _ctx.FileSize))
            {
                if (hitsByFileOffset.ContainsKey(claim.StringFileOffset))
                {
                    claims.TryAdd(claim.StringFileOffset, claim);
                }
            }
        }

        return claims;
    }

    /// <summary>
    ///     Describe where a referrer lives, via a sorted index rather than a scan.
    ///     <para>
    ///         This runs for EVERY matched pointer word in the whole-dump sweep — including matches
    ///         past the 32-entry <c>AllReferrers</c> cap, whose result is then discarded — so the
    ///         old <c>FirstOrDefault</c> over the gap list made a few-thousand-entry linear scan the
    ///         hottest thing in the ownership pipeline. Coverage gaps are disjoint by construction
    ///         (they are the complement of the covered ranges), so the last gap starting at or
    ///         before the offset is the only one that can contain it.
    ///     </para>
    /// </summary>
    private string DescribeReferrerContext(long fileOffset)
    {
        EnsureGapIndex();
        if (_gapStarts!.Length == 0)
        {
            return "CapturedMemory";
        }

        var idx = Array.BinarySearch(_gapStarts, fileOffset);
        if (idx < 0)
        {
            idx = ~idx - 1;
        }

        if (idx < 0)
        {
            return "CapturedMemory";
        }

        var gap = _gapsByStart![idx];
        if (fileOffset >= gap.FileOffset + gap.Size)
        {
            return "CapturedMemory";
        }

        return string.IsNullOrWhiteSpace(gap.Context)
            ? gap.Classification.ToString()
            : $"{gap.Classification}:{gap.Context}";
    }

    private void EnsureGapIndex()
    {
        if (_gapStarts is not null)
        {
            return;
        }

        _gapsByStart = [.. _ctx.Coverage.Gaps.OrderBy(g => g.FileOffset)];
        _gapStarts = [.. _gapsByStart.Select(g => g.FileOffset)];
    }

    private sealed class PointerRefInfo
    {
        public int Count { get; set; }
        public long? ReferrerVa { get; set; }
        public long? ReferrerFileOffset { get; set; }
        public string? ReferrerContext { get; set; }
        public List<(long FileOffset, long Va, string? Context)>? AllReferrers { get; set; }
    }

    #endregion
}
