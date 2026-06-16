using System.IO.MemoryMappedFiles;
using Windows.UI;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Recovery;

namespace FalloutXbox360Utils;

/// <summary>
///     Manages file data loading and region mapping for the hex viewer.
/// </summary>
internal sealed class HexDataManager : IDisposable
{
    /// <summary>
    ///     Replaced wholesale, never mutated after publication: BuildFileRegions and
    ///     AddCoverageGapRegionsCore run on background threads (LoadAsync/AddCoverageGapRegionsAsync)
    ///     while the UI thread's row/minimap renderers binary-search the same list via
    ///     FindRegionForOffset — builders work on a local list and publish it with a single
    ///     atomic reference assignment; readers snapshot the field once per call.
    /// </summary>
    private List<FileRegion> _fileRegions = [];

    private bool _disposed;
    private Color _esmColor;
    private List<DetectedMainRecord>? _mainRecords;
    private MemoryMappedFile? _mmf;
    private bool _ownsAccessor = true;

    public MemoryMappedViewAccessor? Accessor { get; private set; }

    public string? FilePath { get; private set; }

    public long FileSize { get; private set; }

    public IReadOnlyList<FileRegion> FileRegions => _fileRegions;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }

    public void Cleanup()
    {
        if (_ownsAccessor)
        {
            Accessor?.Dispose();
            _mmf?.Dispose();
        }

        Accessor = null;
        _mmf = null;
        _ownsAccessor = true;
    }

    public void Clear()
    {
        Cleanup();
        FilePath = null;
        FileSize = 0;
        _fileRegions = [];
        _mainRecords = null;
    }

    public bool Load(string filePath, AnalysisResult analysisResult)
    {
        Cleanup();
        _ownsAccessor = true;
        FilePath = filePath;
        FileSize = new FileInfo(filePath).Length;
        BuildFileRegions(analysisResult);

        try
        {
            _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            Accessor = _mmf.CreateViewAccessor(0, FileSize, MemoryMappedFileAccess.Read);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Loads data using an externally-owned accessor (not disposed by this manager).
    /// </summary>
    public bool Load(string filePath, AnalysisResult analysisResult, MemoryMappedViewAccessor externalAccessor)
    {
        Cleanup();
        _ownsAccessor = false;
        FilePath = filePath;
        FileSize = new FileInfo(filePath).Length;
        Accessor = externalAccessor;
        BuildFileRegions(analysisResult);
        return true;
    }

    /// <summary>
    ///     Async version that offloads BuildFileRegions to a background thread.
    /// </summary>
    public async Task<bool> LoadAsync(string filePath, AnalysisResult analysisResult,
        MemoryMappedViewAccessor externalAccessor)
    {
        Cleanup();
        _ownsAccessor = false;
        FilePath = filePath;
        FileSize = new FileInfo(filePath).Length;
        Accessor = externalAccessor;
        await Task.Run(() => BuildFileRegions(analysisResult));
        return true;
    }

    /// <summary>
    ///     Adds classified gap regions from coverage analysis to the file region list.
    ///     Call after Load() to color-code unknown areas by their classification.
    ///     Most gap types are collapsed to a single "Gap" label; only RecordSignature and
    ///     AssetManagement retain distinct labels (they have semantic meaning).
    /// </summary>
    public void AddCoverageGapRegions(
        CoverageResult coverage,
        IReadOnlyList<DmpGapRecoveryCandidate>? recoverableCandidates = null)
    {
        AddCoverageGapRegionsCore(coverage, recoverableCandidates);
    }

    /// <summary>
    ///     Async version that offloads gap region building to a background thread.
    /// </summary>
    public async Task AddCoverageGapRegionsAsync(
        CoverageResult coverage,
        IReadOnlyList<DmpGapRecoveryCandidate>? recoverableCandidates = null)
    {
        await Task.Run(() => AddCoverageGapRegionsCore(coverage, recoverableCandidates));
    }

    private void AddCoverageGapRegionsCore(
        CoverageResult coverage,
        IReadOnlyList<DmpGapRecoveryCandidate>? recoverableCandidates)
    {
        var candidatesByGap = recoverableCandidates?
            .GroupBy(c => (c.GapFileOffset, c.GapSize))
            .ToDictionary(g => g.Key, g => g.ToList()) ??
                              new Dictionary<(long GapFileOffset, long GapSize), List<DmpGapRecoveryCandidate>>();

        // Merge into a local copy and publish atomically — this runs on a thread-pool thread
        // while the UI thread's renderers binary-search the live list.
        var merged = new List<FileRegion>(_fileRegions);
        foreach (var gap in coverage.Gaps)
        {
            candidatesByGap.TryGetValue((gap.FileOffset, gap.Size), out var candidates);
            var hasRecoveryCandidates = candidates is { Count: > 0 };
            var color = hasRecoveryCandidates
                ? FileTypeColors.GetMemoryMapGapColor(GapClassification.RecordSignature)
                : FileTypeColors.GetMemoryMapGapColor(gap.Classification);

            // Simplified display name for memory map
            var typeName = hasRecoveryCandidates
                ? FormatRecoverableGapLabel(candidates!, gap.Size)
                : gap.Classification switch
                {
                    GapClassification.RecordSignature => $"Record Signatures ({FormatGapSize(gap.Size)})",
                    GapClassification.AssetManagement => $"Asset Mgmt ({FormatGapSize(gap.Size)})",
                    _ => $"Gap ({FormatGapSize(gap.Size)})"
                };

            merged.Add(new FileRegion
            {
                Start = gap.FileOffset,
                End = gap.FileOffset + gap.Size,
                TypeName = typeName,
                Color = color,
                IsGap = true
            });
        }

        // Sort by Start, then by IsGap (file data before gaps at same offset)
        merged.Sort((a, b) =>
        {
            var startCmp = a.Start.CompareTo(b.Start);
            return startCmp != 0 ? startCmp : a.IsGap.CompareTo(b.IsGap);
        });

        _fileRegions = merged;
    }

    private static string FormatRecoverableGapLabel(
        IReadOnlyList<DmpGapRecoveryCandidate> candidates,
        long gapSize)
    {
        var prefix = candidates.All(c => c.Kind == DmpGapRecoveryCandidateKind.RawEsmRecord)
            ? "Raw ESM"
            : "Recoverable";
        var summary = string.Join(", ",
            candidates
                .GroupBy(c => c.RecordType ?? c.ClassName ?? c.Kind.ToString())
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(3)
                .Select(g => $"{g.Key} x{g.Count():N0}"));
        return $"{prefix}: {summary} ({FormatGapSize(gapSize)})";
    }

    private static string FormatGapSize(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes:N0} B"
        };
    }

    public void ReadBytes(long offset, byte[] buffer)
    {
        if (Accessor != null)
        {
            Accessor.ReadArray(offset, buffer, 0, buffer.Length);
        }
        else if (FilePath != null)
        {
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(offset, SeekOrigin.Begin);
            fs.ReadExactly(buffer);
        }
    }

    public FileRegion? FindRegionForOffset(long offset)
    {
        // Snapshot the field once — a background build may publish a new list mid-call.
        var regions = _fileRegions;
        if (regions.Count == 0) return FallbackEsmLookup(offset);

        int left = 0, right = regions.Count - 1;
        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            var region = regions[mid];
            if (offset >= region.Start && offset < region.End) return region;
            if (region.Start > offset) right = mid - 1;
            else left = mid + 1;
        }

        // Binary search didn't find a region - check if offset is within any MainRecord
        // This handles edge cases where grouped regions might not cover all record offsets
        return FallbackEsmLookup(offset);
    }

    /// <summary>
    ///     Fallback lookup for ESM records when binary search fails.
    ///     Returns a region if the offset falls within any detected MainRecord.
    /// </summary>
    private FileRegion? FallbackEsmLookup(long offset)
    {
        // Snapshot — BuildFileRegions republishes this field from a background thread.
        var records = _mainRecords;
        if (records == null || records.Count == 0) return null;

        foreach (var record in records)
        {
            var start = record.Offset;
            var end = record.Offset + record.DataSize + 24;
            if (offset >= start && offset < end)
            {
                return new FileRegion
                {
                    Start = start,
                    End = end,
                    TypeName = $"ESM {record.RecordType}",
                    Color = _esmColor
                };
            }
        }

        return null;
    }

    private void BuildFileRegions(AnalysisResult analysisResult)
    {
        // Build into a local list and publish atomically at the end — LoadAsync runs this on a
        // thread-pool thread while the viewer may already be paint-eligible.
        var regions = new List<FileRegion>();
        _mainRecords = null;

        var occupiedRanges = new List<(long Start, long End)>();

        // Add ESM record regions FIRST - they're schema-validated and more reliable
        // than simple signature matching which can produce false positives on texture
        // path strings embedded within ESM records
        if (analysisResult.EsmRecords?.MainRecords != null && analysisResult.EsmRecords.MainRecords.Count > 0)
        {
            // Store MainRecords for fallback lookup (handles edge cases in region grouping)
            _mainRecords = analysisResult.EsmRecords.MainRecords;
            _esmColor = FileTypeColors.GetColorByCategory(FileCategory.EsmData);

            var esmRegions = GroupEsmRecordsIntoRegions(_mainRecords);

            foreach (var region in esmRegions)
            {
                regions.Add(new FileRegion
                {
                    Start = region.Start,
                    End = region.End,
                    TypeName = $"ESM Data ({region.Count} records)",
                    Color = _esmColor
                });
                occupiedRanges.Add((region.Start, region.End));
            }
        }

        // Sort carved files by size ascending - smaller files processed first get priority
        // This ensures files contained within larger files are visible
        var sortedFiles = analysisResult.CarvedFiles
            .Where(f => f.Length > 0)
            .OrderBy(f => f.Length)
            .ToList();

        foreach (var file in sortedFiles)
        {
            var start = file.Offset;
            var end = file.Offset + file.Length;

            // Check if this file's range is already fully covered by existing regions
            // (including ESM regions added above)
            if (IsRangeFullyCovered(start, end, occupiedRanges)) continue;

            regions.Add(new FileRegion
            {
                Start = start,
                End = end,
                TypeName = file.FileType,
                Color = FileTypeColors.GetColor(file)
            });
            occupiedRanges.Add((start, end));
        }

        // Sort by Start, then by IsGap (file data before gaps at same offset)
        regions.Sort((a, b) =>
        {
            var startCmp = a.Start.CompareTo(b.Start);
            return startCmp != 0 ? startCmp : a.IsGap.CompareTo(b.IsGap);
        });

        _fileRegions = regions;
    }

    /// <summary>
    ///     Groups consecutive ESM records that are within 1KB of each other into regions.
    ///     This reduces visual noise in the memory map.
    /// </summary>
    private static List<(long Start, long End, int Count)> GroupEsmRecordsIntoRegions(
        List<DetectedMainRecord> records)
    {
        if (records.Count == 0) return [];

        const long maxGap = 1024; // Group records within 1KB of each other
        var sortedRecords = records.OrderBy(r => r.Offset).ToList();
        var regions = new List<(long Start, long End, int Count)>();

        var regionStart = sortedRecords[0].Offset;
        var regionEnd = sortedRecords[0].Offset + sortedRecords[0].DataSize + 24;
        var count = 1;

        for (var i = 1; i < sortedRecords.Count; i++)
        {
            var record = sortedRecords[i];
            var recordStart = record.Offset;
            var recordEnd = record.Offset + record.DataSize + 24;

            if (recordStart <= regionEnd + maxGap)
            {
                // Extend current region
                regionEnd = Math.Max(regionEnd, recordEnd);
                count++;
            }
            else
            {
                // Finish current region, start new one
                regions.Add((regionStart, regionEnd, count));
                regionStart = recordStart;
                regionEnd = recordEnd;
                count = 1;
            }
        }

        // Add final region
        regions.Add((regionStart, regionEnd, count));

        return regions;
    }

    private static bool IsRangeFullyCovered(long start, long end, List<(long Start, long End)> ranges)
    {
        // Check if every byte in [start, end) is covered by existing ranges
        var relevantRanges = ranges
            .Where(r => r.Start < end && r.End > start)
            .OrderBy(r => r.Start)
            .ToList();

        if (relevantRanges.Count == 0) return false;

        var currentPos = start;
        foreach (var range in relevantRanges)
        {
            if (range.Start > currentPos) return false; // Gap found
            currentPos = Math.Max(currentPos, range.End);
            if (currentPos >= end) return true; // Fully covered
        }

        return currentPos >= end;
    }
}
