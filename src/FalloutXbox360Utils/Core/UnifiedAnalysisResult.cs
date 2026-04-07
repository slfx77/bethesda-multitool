using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core;

/// <summary>
///     Result of format-agnostic file analysis and semantic parsing.
/// </summary>
public sealed class UnifiedAnalysisResult : IDisposable
{
    private MemoryMappedViewAccessor? _accessor;
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

    internal MemoryMappedViewAccessor? Accessor => _accessor;

    public void Dispose()
    {
        _accessor?.Dispose();
        _mmf?.Dispose();
    }

    internal void SetDisposables(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor)
    {
        _mmf = mmf;
        _accessor = accessor;
    }

    internal (MemoryMappedFile? MappedFile, MemoryMappedViewAccessor? Accessor) DetachDisposables()
    {
        var mappedFile = _mmf;
        var accessor = _accessor;
        _mmf = null;
        _accessor = null;
        return (mappedFile, accessor);
    }
}
