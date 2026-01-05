using System.IO.MemoryMappedFiles;
using Xbox360MemoryCarver.App;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver.App.HexViewer;

/// <summary>
///     Manages file data loading and region mapping for the hex viewer.
/// </summary>
internal sealed class HexDataManager : IDisposable
{
    private readonly List<FileRegion> _fileRegions = [];
    private MemoryMappedViewAccessor? _accessor;
    private bool _disposed;
    private string? _filePath;
    private long _fileSize;
    private MemoryMappedFile? _mmf;

    public MemoryMappedViewAccessor? Accessor => _accessor;
    public string? FilePath => _filePath;
    public long FileSize => _fileSize;
    public IReadOnlyList<FileRegion> FileRegions => _fileRegions;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }

    public void Cleanup()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
    }

    public void Clear()
    {
        Cleanup();
        _filePath = null;
        _fileSize = 0;
        _fileRegions.Clear();
    }

    public bool Load(string filePath, AnalysisResult analysisResult)
    {
        Cleanup();
        _filePath = filePath;
        _fileSize = new FileInfo(filePath).Length;
        BuildFileRegions(analysisResult);

        try
        {
            _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _accessor = _mmf.CreateViewAccessor(0, _fileSize, MemoryMappedFileAccess.Read);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void ReadBytes(long offset, byte[] buffer)
    {
        if (_accessor != null)
        {
            _accessor.ReadArray(offset, buffer, 0, buffer.Length);
        }
        else if (_filePath != null)
        {
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
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

        var sortedFiles = analysisResult.CarvedFiles.Where(f => f.Length > 0)
            .OrderBy(f => f.Offset).ThenBy(f => FileTypeColors.GetPriority(f.FileType)).ToList();

        var occupiedRanges = new List<(long Start, long End, int Priority)>();
        foreach (var file in sortedFiles)
        {
            var start = file.Offset;
            var end = file.Offset + file.Length;
            var priority = FileTypeColors.GetPriority(file.FileType);

            if (occupiedRanges.Any(r => start < r.End && end > r.Start && r.Priority <= priority))
            {
                continue;
            }

            _fileRegions.Add(new FileRegion
            {
                Start = start,
                End = end,
                TypeName = file.FileType,
                Color = FileTypeColors.GetColor(file.FileType)
            });
            occupiedRanges.Add((start, end, priority));
        }

        _fileRegions.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}
