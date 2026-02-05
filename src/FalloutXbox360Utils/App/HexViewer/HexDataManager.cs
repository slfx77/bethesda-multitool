using System.IO.MemoryMappedFiles;
using Windows.UI;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Manages file data loading and region mapping for the hex viewer.
/// </summary>
internal sealed class HexDataManager : IDisposable
{
    private readonly List<FileRegion> _fileRegions = [];
    private IReadOnlyList<DetectedMainRecord>? _mainRecords;
    private Color _esmColor;
    private bool _disposed;
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
        _fileRegions.Clear();
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
    ///     Adds classified gap regions from coverage analysis to the file region list.
    ///     Call after Load() to color-code unknown areas by their classification.
    ///     Most gap types are collapsed to a single "Gap" label; only ESM-like and
    ///     AssetManagement retain distinct labels (they have semantic meaning).
    /// </summary>
    public void AddCoverageGapRegions(CoverageResult coverage)
    {
        foreach (var gap in coverage.Gaps)
        {
            var color = FileTypeColors.GetMemoryMapGapColor(gap.Classification);

            // Simplified display name for memory map
            var typeName = gap.Classification switch
            {
                GapClassification.EsmLike => $"ESM-like ({FormatGapSize(gap.Size)})",
                GapClassification.AssetManagement => $"Asset Mgmt ({FormatGapSize(gap.Size)})",
                _ => $"Gap ({FormatGapSize(gap.Size)})"
            };

            _fileRegions.Add(new FileRegion
            {
                Start = gap.FileOffset,
                End = gap.FileOffset + gap.Size,
                TypeName = typeName,
                Color = color,
                IsGap = true
            });
        }

        // Sort by Start, then by IsGap (file data before gaps at same offset)
        _fileRegions.Sort((a, b) =>
        {
            var startCmp = a.Start.CompareTo(b.Start);
            return startCmp != 0 ? startCmp : a.IsGap.CompareTo(b.IsGap);
        });
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
        if (_fileRegions.Count == 0) return FallbackEsmLookup(offset);

        int left = 0, right = _fileRegions.Count - 1;
        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            var region = _fileRegions[mid];
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
        if (_mainRecords == null || _mainRecords.Count == 0) return null;

        foreach (var record in _mainRecords)
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
        _fileRegions.Clear();
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
                _fileRegions.Add(new FileRegion
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

            _fileRegions.Add(new FileRegion
            {
                Start = start,
                End = end,
                TypeName = file.FileType,
                Color = FileTypeColors.GetColor(file)
            });
            occupiedRanges.Add((start, end));
        }

        // Sort by Start, then by IsGap (file data before gaps at same offset)
        _fileRegions.Sort((a, b) =>
        {
            var startCmp = a.Start.CompareTo(b.Start);
            return startCmp != 0 ? startCmp : a.IsGap.CompareTo(b.IsGap);
        });
    }

    /// <summary>
    ///     Groups consecutive ESM records that are within 1KB of each other into regions.
    ///     This reduces visual noise in the memory map.
    /// </summary>
    private static List<(long Start, long End, int Count)> GroupEsmRecordsIntoRegions(
        IReadOnlyList<DetectedMainRecord> records)
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
