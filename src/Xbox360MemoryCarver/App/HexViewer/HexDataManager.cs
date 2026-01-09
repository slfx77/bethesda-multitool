using System.IO.MemoryMappedFiles;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver;

/// <summary>
///     Manages file data loading and region mapping for the hex viewer.
/// </summary>
internal sealed class HexDataManager : IDisposable
{
    private readonly List<FileRegion> _fileRegions = [];
    private bool _disposed;
    private MemoryMappedFile? _mmf;

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
        Accessor?.Dispose();
        Accessor = null;
        _mmf?.Dispose();
        _mmf = null;
    }

    public void Clear()
    {
        Cleanup();
        FilePath = null;
        FileSize = 0;
        _fileRegions.Clear();
    }

    public bool Load(string filePath, AnalysisResult analysisResult)
    {
        Cleanup();
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
        if (_fileRegions.Count == 0) return null;

        int left = 0, right = _fileRegions.Count - 1;
        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            var region = _fileRegions[mid];
            if (offset >= region.Start && offset < region.End) return region;
            if (region.Start > offset) right = mid - 1;
            else left = mid + 1;
        }

        return null;
    }

    private void BuildFileRegions(AnalysisResult analysisResult)
    {
        _fileRegions.Clear();

        // Sort by size ascending - smaller files processed first get priority
        // This ensures files contained within larger files are visible
        var sortedFiles = analysisResult.CarvedFiles
            .Where(f => f.Length > 0)
            .OrderBy(f => f.Length)
            .ToList();

        var occupiedRanges = new List<(long Start, long End)>();

        foreach (var file in sortedFiles)
        {
            var start = file.Offset;
            var end = file.Offset + file.Length;

            // Check if this file's range is already fully covered by existing regions
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

        _fileRegions.Sort((a, b) => a.Start.CompareTo(b.Start));
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
