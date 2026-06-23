using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Analysis;

/// <summary>
///     Result of format-agnostic file analysis and semantic parsing.
/// </summary>
public sealed class UnifiedAnalysisResult : IDisposable
{
    private MemoryMappedFile? _mmf;

    /// <summary>The detected file type.</summary>
    public AnalysisFileType FileType { get; init; }

    /// <summary>Parsed records (NPCs, quests, dialogue, items, etc.).</summary>
    public RecordCollection Records { get; init; } = null!;

    /// <summary>FormID resolver for name lookups.</summary>
    public FormIdResolver Resolver { get; init; } = FormIdResolver.Empty;

    /// <summary>Raw analysis result (for accessing RuntimeEditorIds, CarvedFiles, MinidumpInfo, etc.).</summary>
    public AnalysisResult RawResult { get; init; } = null!;

    /// <summary>Source file path.</summary>
    public string FilePath { get; init; } = "";

    internal MemoryMappedViewAccessor? Accessor { get; private set; }

    public void Dispose()
    {
        Accessor?.Dispose();
        _mmf?.Dispose();
    }

    internal void SetDisposables(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor)
    {
        _mmf = mmf;
        Accessor = accessor;
    }

    internal (MemoryMappedFile? MappedFile, MemoryMappedViewAccessor? Accessor) DetachDisposables()
    {
        var mappedFile = _mmf;
        var accessor = Accessor;
        _mmf = null;
        Accessor = null;
        return (mappedFile, accessor);
    }
}

