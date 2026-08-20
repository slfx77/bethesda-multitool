using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Land;
using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Analysis;

/// <summary>
///     Result of format-agnostic file analysis and semantic parsing.
/// </summary>
public sealed class UnifiedAnalysisResult : IDisposable
{
    private MemoryMappedFile? _mmf;
    private BtdTerrainInjection? _terrainInjection;

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
        _terrainInjection?.Dispose();
        _terrainInjection = null;
    }

    internal void SetDisposables(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor)
    {
        _mmf = mmf;
        Accessor = accessor;
    }

    /// <summary>
    ///     Attaches the BTD terrain injection whose lazy height sources back this result's cells.
    ///     It must outlive every heightmap read, so it is disposed with the result — or handed to
    ///     whoever <see cref="DetachDisposables" />es the file mapping (the GUI session).
    /// </summary>
    internal void SetTerrainInjection(BtdTerrainInjection? terrainInjection)
    {
        _terrainInjection = terrainInjection;
    }

    /// <summary>
    ///     Hands ownership of the BTD terrain injection to a caller whose record graph outlives this
    ///     result — the detached-source path (<c>SemanticSourceSetBuilder.LoadSourceAsync</c>), which
    ///     deliberately disposes the result to close the ESM mapping but keeps <see cref="Records" />.
    ///     Without this, disposal would close the lazy height sources under cells that escaped.
    /// </summary>
    internal BtdTerrainInjection? DetachTerrainInjection()
    {
        var terrain = _terrainInjection;
        _terrainInjection = null;
        return terrain;
    }

    internal (MemoryMappedFile? MappedFile, MemoryMappedViewAccessor? Accessor, BtdTerrainInjection? TerrainInjection)
        DetachDisposables()
    {
        var mappedFile = _mmf;
        var accessor = Accessor;
        var terrain = _terrainInjection;
        _mmf = null;
        Accessor = null;
        _terrainInjection = null;
        return (mappedFile, accessor, terrain);
    }
}
