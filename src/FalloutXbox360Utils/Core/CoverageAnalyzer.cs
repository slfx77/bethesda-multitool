using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core;

/// <summary>
///     Analyzes memory dump coverage: what percentage of memory regions contain
///     recognized data (carved files, ESM records, SCDA scripts, modules) vs unknown gaps.
/// </summary>
public static class CoverageAnalyzer
{
    /// <summary>
    ///     Analyze coverage of an already-analyzed dump.
    /// </summary>
    public static CoverageResult Analyze(
        AnalysisResult result,
        MemoryMappedViewAccessor accessor)
    {
        var minidump = result.MinidumpInfo;
        if (minidump == null || !minidump.IsValid)
        {
            return new CoverageResult { Error = "Invalid or missing minidump info" };
        }

        // Step 1: Build recognized intervals from all sources
        var recognizedIntervals = BuildRecognizedIntervals(result);

        // Step 2: Build memory region intervals (the total addressable space)
        var regionIntervals = BuildMemoryRegionIntervals(minidump);
        var totalRegionBytes = regionIntervals.Sum(r => r.End - r.Start);

        // Step 3: Compute coverage per category
        var categoryBytes = ComputeCategoryBytes(result, regionIntervals);

        // Step 4: Merge all recognized intervals and find gaps within memory regions
        var merged = MergeIntervals(recognizedIntervals);
        var gaps = FindGaps(regionIntervals, merged);

        // Step 5: Build asset VA lookup for pointer→asset cross-referencing
        var assetVas = BuildAssetVaSet(result, minidump);

        // Step 6: Build module VA ranges for context classification
        var moduleVaRanges = BuildModuleVaRanges(minidump);

        // Step 7: Classify each gap
        foreach (var gap in gaps)
        {
            gap.VirtualAddress = minidump.FileOffsetToVirtualAddress(gap.FileOffset);
            gap.Context = ClassifyContext(gap.VirtualAddress, moduleVaRanges);
            gap.Classification = ClassifyGap(accessor, gap, assetVas, moduleVaRanges);
        }

        var totalRecognized = merged
            .Sum(iv => ClampedOverlap(iv, regionIntervals));

        return new CoverageResult
        {
            FileSize = result.FileSize,
            TotalMemoryRegions = minidump.MemoryRegions.Count,
            TotalRegionBytes = totalRegionBytes,
            MinidumpOverhead = result.FileSize - totalRegionBytes,
            TotalRecognizedBytes = totalRecognized,
            CategoryBytes = categoryBytes,
            Gaps = gaps.OrderByDescending(g => g.Size).ToList()
        };
    }

    /// <summary>
    ///     Run PDB-guided analysis on the dump using known global symbols.
    /// </summary>
    public static PdbAnalysisResult? AnalyzePdbGlobals(
        AnalysisResult result,
        MemoryMappedViewAccessor accessor,
        string pdbGlobalsPath)
    {
        var minidump = result.MinidumpInfo;
        if (minidump == null || !minidump.IsValid)
        {
            return null;
        }

        // Find the game module
        var gameModule = MemoryDumpAnalyzer.FindGameModule(minidump);
        if (gameModule == null)
        {
            Console.WriteLine("[PDB] No Fallout game module found in dump");
            return null;
        }

        // Enumerate PE sections from the module
        var peSections = EsmRecordFormat.EnumeratePeSections(accessor, result.FileSize, minidump, gameModule);
        if (peSections == null || peSections.Count == 0)
        {
            Console.WriteLine("[PDB] Could not enumerate PE sections from game module");
            return null;
        }

        Console.WriteLine(
            $"[PDB] Module: {Path.GetFileName(gameModule.Name)} at 0x{gameModule.BaseAddress32:X8}, {peSections.Count} PE sections");

        // Parse PDB globals
        var globals = PdbGlobalResolver.ParseGlobals(pdbGlobalsPath);
        Console.WriteLine($"[PDB] Parsed {globals.Count} globals from {Path.GetFileName(pdbGlobalsPath)}");

        // Build asset VA set for cross-referencing
        var assetVas = BuildAssetVaSet(result, minidump);

        // Resolve and analyze
        var resolver = new PdbGlobalResolver(accessor, result.FileSize, minidump, gameModule, peSections);
        return resolver.ResolveAndAnalyze(globals, assetVas);
    }

    #region Step 1: Interval Building

    private static List<CoverageInterval> BuildRecognizedIntervals(AnalysisResult result)
    {
        var intervals = new List<CoverageInterval>();

        // Carved files (includes header, modules, textures, audio, models, etc.)
        foreach (var cf in result.CarvedFiles)
        {
            if (cf.Length <= 0)
            {
                continue;
            }

            var category = cf.Category switch
            {
                FileCategory.Header => CoverageCategory.Header,
                FileCategory.Module => CoverageCategory.Module,
                _ => CoverageCategory.CarvedFile
            };

            intervals.Add(new CoverageInterval(cf.Offset, cf.Offset + cf.Length, category));
        }

        // ESM main records: 24-byte header + DataSize
        if (result.EsmRecords != null)
        {
            foreach (var rec in result.EsmRecords.MainRecords)
            {
                var end = rec.Offset + 24 + rec.DataSize;
                intervals.Add(new CoverageInterval(rec.Offset, end, CoverageCategory.EsmRecord));
            }
        }

        // SCDA records: 4-byte magic + 2-byte size field + bytecode
        if (result.ScdaRecords != null)
        {
            foreach (var scda in result.ScdaRecords)
            {
                var end = scda.Offset + 6 + scda.BytecodeSize;
                intervals.Add(new CoverageInterval(scda.Offset, end, CoverageCategory.ScdaScript));
            }
        }

        return intervals;
    }

    #endregion

    #region Step 2: Memory Region Mapping

    private static List<CoverageInterval> BuildMemoryRegionIntervals(MinidumpInfo minidump)
    {
        return minidump.MemoryRegions
            .Select(r => new CoverageInterval(r.FileOffset, r.FileOffset + r.Size, CoverageCategory.Region))
            .OrderBy(r => r.Start)
            .ToList();
    }

    #endregion

    #region Step 3: Category Byte Counting

    private static Dictionary<CoverageCategory, long> ComputeCategoryBytes(
        AnalysisResult result,
        List<CoverageInterval> regionIntervals)
    {
        var byCategory = new Dictionary<CoverageCategory, List<CoverageInterval>>();

        foreach (var cf in result.CarvedFiles)
        {
            if (cf.Length <= 0)
            {
                continue;
            }

            var cat = cf.Category switch
            {
                FileCategory.Header => CoverageCategory.Header,
                FileCategory.Module => CoverageCategory.Module,
                _ => CoverageCategory.CarvedFile
            };

            if (!byCategory.TryGetValue(cat, out var list))
            {
                list = [];
                byCategory[cat] = list;
            }

            list.Add(new CoverageInterval(cf.Offset, cf.Offset + cf.Length, cat));
        }

        if (result.EsmRecords != null)
        {
            var esmList = new List<CoverageInterval>();
            foreach (var rec in result.EsmRecords.MainRecords)
            {
                esmList.Add(new CoverageInterval(rec.Offset, rec.Offset + 24 + rec.DataSize,
                    CoverageCategory.EsmRecord));
            }

            byCategory[CoverageCategory.EsmRecord] = esmList;
        }

        if (result.ScdaRecords is { Count: > 0 })
        {
            var scdaList = new List<CoverageInterval>();
            foreach (var scda in result.ScdaRecords)
            {
                scdaList.Add(new CoverageInterval(scda.Offset, scda.Offset + 6 + scda.BytecodeSize,
                    CoverageCategory.ScdaScript));
            }

            byCategory[CoverageCategory.ScdaScript] = scdaList;
        }

        // For each category, merge intervals then compute overlap with memory regions
        var bytes = new Dictionary<CoverageCategory, long>();
        foreach (var (cat, intervals) in byCategory)
        {
            var merged = MergeIntervals(intervals);
            var overlap = merged.Sum(iv => ClampedOverlap(iv, regionIntervals));
            if (overlap > 0)
            {
                bytes[cat] = overlap;
            }
        }

        return bytes;
    }

    #endregion

    #region Step 4: Interval Merging & Gap Detection

    private static List<CoverageInterval> MergeIntervals(List<CoverageInterval> intervals)
    {
        if (intervals.Count == 0)
        {
            return [];
        }

        var sorted = intervals.OrderBy(i => i.Start).ThenBy(i => i.End).ToList();
        var merged = new List<CoverageInterval> { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            var current = sorted[i];
            var last = merged[^1];

            if (current.Start <= last.End)
            {
                // Overlapping or adjacent — extend
                merged[^1] = new CoverageInterval(last.Start, Math.Max(last.End, current.End), last.Category);
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    private static List<CoverageGap> FindGaps(
        List<CoverageInterval> regionIntervals,
        List<CoverageInterval> mergedRecognized)
    {
        var gaps = new List<CoverageGap>();

        foreach (var region in regionIntervals)
        {
            var cursor = region.Start;

            foreach (var recognized in mergedRecognized)
            {
                if (recognized.End <= cursor)
                {
                    continue;
                }

                if (recognized.Start >= region.End)
                {
                    break;
                }

                // Clamp recognized interval to this region
                var clampedStart = Math.Max(recognized.Start, region.Start);

                if (clampedStart > cursor)
                {
                    // Gap between cursor and this recognized interval
                    gaps.Add(new CoverageGap
                    {
                        FileOffset = cursor,
                        Size = clampedStart - cursor
                    });
                }

                cursor = Math.Max(cursor, Math.Min(recognized.End, region.End));
            }

            // Trailing gap after last recognized interval in this region
            if (cursor < region.End)
            {
                gaps.Add(new CoverageGap
                {
                    FileOffset = cursor,
                    Size = region.End - cursor
                });
            }
        }

        return gaps;
    }

    /// <summary>
    ///     Compute total bytes of an interval that overlap with any memory region.
    /// </summary>
    private static long ClampedOverlap(CoverageInterval interval, List<CoverageInterval> regions)
    {
        long total = 0;
        foreach (var region in regions)
        {
            var overlapStart = Math.Max(interval.Start, region.Start);
            var overlapEnd = Math.Min(interval.End, region.End);
            if (overlapEnd > overlapStart)
            {
                total += overlapEnd - overlapStart;
            }
        }

        return total;
    }

    #endregion

    #region Step 5-6: Asset VA Set & Module VA Ranges

    private static HashSet<long> BuildAssetVaSet(AnalysisResult result, MinidumpInfo minidump)
    {
        var vaSet = new HashSet<long>();
        foreach (var cf in result.CarvedFiles)
        {
            if (cf.Category is FileCategory.Module or FileCategory.Header)
            {
                continue;
            }

            var va = minidump.FileOffsetToVirtualAddress(cf.Offset);
            if (va.HasValue)
            {
                vaSet.Add(va.Value);
            }
        }

        return vaSet;
    }

    private static List<(long start, long end, string name)> BuildModuleVaRanges(MinidumpInfo minidump)
    {
        return minidump.Modules
            .Select(m => (start: m.BaseAddress, end: m.BaseAddress + m.Size, name: Path.GetFileName(m.Name)))
            .OrderBy(r => r.start)
            .ToList();
    }

    #endregion

    #region Step 7: Gap Classification

    private static string ClassifyContext(long? va, List<(long start, long end, string name)> moduleVaRanges)
    {
        if (!va.HasValue)
        {
            return "Unknown VA";
        }

        foreach (var (start, end, name) in moduleVaRanges)
        {
            if (va.Value >= start && va.Value < end)
            {
                return $"Module: {name}";
            }
        }

        return "Heap/stack";
    }

    private static GapClassification ClassifyGap(
        MemoryMappedViewAccessor accessor,
        CoverageGap gap,
        HashSet<long> assetVas,
        List<(long start, long end, string name)> moduleVaRanges)
    {
        var sampleSize = (int)Math.Min(gap.Size, 4096);
        var buffer = new byte[sampleSize];
        accessor.ReadArray(gap.FileOffset, buffer, 0, sampleSize);

        // Check zero-fill first
        var zeroCount = 0;
        foreach (var b in buffer)
        {
            if (b == 0)
            {
                zeroCount++;
            }
        }

        if (zeroCount > sampleSize * 0.9)
        {
            return GapClassification.ZeroFill;
        }

        // Check ASCII text
        var printableCount = buffer.Count(b => b is >= 0x20 and <= 0x7E or (byte)'\n' or (byte)'\r' or (byte)'\t');

        if (printableCount > sampleSize * 0.7)
        {
            return GapClassification.AsciiText;
        }

        // Check string pool pattern: runs of printable chars separated by null bytes
        if (IsStringPool(buffer))
        {
            return GapClassification.StringPool;
        }

        // Check pointer density
        var (pointerCount, assetPointerCount) = CountPointers(buffer, assetVas, moduleVaRanges);
        var alignedSlots = sampleSize / 4;

        if (pointerCount > alignedSlots * 0.3)
        {
            // Check if any pointers resolve to known carved file VAs
            if (assetPointerCount > 0)
            {
                return GapClassification.AssetManagement;
            }

            return GapClassification.PointerDense;
        }

        // Check for ESM-like signatures
        if (ContainsEsmSignatures(buffer))
        {
            return GapClassification.EsmLike;
        }

        return GapClassification.BinaryData;
    }

    private static bool IsStringPool(byte[] buffer)
    {
        // Look for pattern: printable runs (≥4 chars) separated by single null bytes
        var stringCount = 0;
        var runLength = 0;

        foreach (var b in buffer)
        {
            if (b is >= 0x20 and <= 0x7E)
            {
                runLength++;
            }
            else
            {
                if (b == 0 && runLength >= 4)
                {
                    stringCount++;
                }

                runLength = 0;
            }
        }

        // Need at least 3 distinct strings in the sample
        return stringCount >= 3;
    }

    private static (int pointerCount, int assetPointerCount) CountPointers(
        byte[] buffer,
        HashSet<long> assetVas,
        List<(long start, long end, string name)> moduleVaRanges)
    {
        var pointerCount = 0;
        var assetPointerCount = 0;

        for (var i = 0; i <= buffer.Length - 4; i += 4)
        {
            // Read as big-endian uint32 (Xbox 360 is big-endian)
            var val = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));

            if (!IsPlausiblePointer(val, moduleVaRanges))
            {
                continue;
            }

            pointerCount++;

            if (assetVas.Contains(val))
            {
                assetPointerCount++;
            }
        }

        return (pointerCount, assetPointerCount);
    }

    private static bool IsPlausiblePointer(uint val, List<(long start, long end, string name)> moduleVaRanges)
    {
        // Xbox 360 memory layout:
        // 0x00000000-0x3FFFFFFF: User space (unlikely for heap pointers in Fallout)
        // 0x40000000-0x7FFFFFFF: Physical memory mapping
        // 0x80000000-0x8FFFFFFF: Cached user space (common for modules)
        // 0x90000000-0x9FFFFFFF: Uncached
        // 0xA0000000-0xBFFFFFFF: Write-combined (common for GPU resources)
        // 0xC0000000-0xDFFFFFFF: Physical RAM direct mapping
        // 0xE0000000-0xFFFFFFFF: Kernel space

        // Accept module range pointers
        foreach (var (start, end, _) in moduleVaRanges)
        {
            if (val >= (uint)start && val < (uint)end)
            {
                return true;
            }
        }

        // Accept heap-range pointers (0x40000000-0xDFFFFFFF covers most user-accessible ranges)
        return val is >= 0x40000000 and < 0xE0000000;
    }

    private static bool ContainsEsmSignatures(byte[] buffer)
    {
        // Check for 4-byte ASCII signatures typical of ESM records
        var knownSigs = new HashSet<string>
        {
            "TES4", "GRUP", "GLOB", "CLAS", "FACT", "RACE", "MGEF", "ENCH",
            "SPEL", "ACTI", "ALCH", "AMMO", "ARMO", "BOOK", "CONT", "DOOR",
            "FURN", "GRAS", "HAIR", "IDLE", "INGR", "KEYM", "LIGH", "MISC",
            "STAT", "WEAP", "NPC_", "CREA", "LVLC", "LVLN", "LVLI", "CELL",
            "WRLD", "DIAL", "INFO", "QUST", "PACK", "PERK", "NOTE", "TERM",
            "REPU", "RCPE", "IMOD", "CHAL", "MESG", "EXPL", "PROJ", "NAVM",
            "REFR", "ACRE", "ACHR", "PGRE", "LAND", "MUSC"
        };

        var count = 0;
        for (var i = 0; i <= buffer.Length - 4; i++)
        {
            // Check if 4 bytes form a known ESM signature
            if (buffer[i] < 0x20 || buffer[i] > 0x7E)
            {
                continue;
            }

            var sig = Encoding.ASCII.GetString(buffer, i, 4);
            if (knownSigs.Contains(sig))
            {
                count++;
                if (count >= 2)
                {
                    return true;
                }
            }
        }

        return false;
    }

    #endregion
}
