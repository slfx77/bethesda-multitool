// Copyright (c) 2026 BethesdaMultitool Contributors
// Licensed under the MIT License.

using BethesdaMultitool.Core.Formats.Bsa.Ba2;

namespace BethesdaMultitool.Core.Formats.Bsa;

/// <summary>
///     Unified read-only accessor over a Bethesda mesh/general archive — a classic BSA or a Bethesda
///     Archive 2 (<c>.ba2</c>) — chosen by file magic (<see cref="Ba2Parser.IsBa2File(string)" />). Lets
///     explicit-path callers (the CLI <c>render --archive</c> batch, the GUI NIF browser) accept either
///     container without caring which it is. The texture side already dispatches via
///     <c>NifTextureArchiveSourceFactory</c>; this is the mesh-side parallel. Both backing extractors
///     are memory-mapped and thread-safe, so <see cref="Extract" /> may be called concurrently.
/// </summary>
public sealed class ArchiveReader : IDisposable
{
    private readonly BsaExtractor? _bsa;
    private readonly Ba2Extractor? _ba2;
    private Dictionary<string, ArchiveEntry>? _byPath;

    private ArchiveReader(BsaExtractor bsa) => _bsa = bsa;

    private ArchiveReader(Ba2Extractor ba2) => _ba2 = ba2;

    /// <summary>True when the underlying container is a BA2 (vs a classic BSA).</summary>
    public bool IsBa2 => _ba2 != null;

    /// <summary>Opens <paramref name="path" /> as a BA2 when it carries the BTDX magic, else as a BSA.</summary>
    public static ArchiveReader Open(string path) =>
        Ba2Parser.IsBa2File(path)
            ? new ArchiveReader(new Ba2Extractor(path))
            : new ArchiveReader(new BsaExtractor(path));

    /// <summary>All entries in the archive (BSA folder tree flattened; BA2 is already a flat list).</summary>
    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        if (_ba2 != null)
        {
            return _ba2.Archive.Files.Select(f => new ArchiveEntry(f.FullPath, f)).ToList();
        }

        return _bsa!.Archive.Folders
            .SelectMany(folder => folder.Files)
            .Select(f => new ArchiveEntry(f.FullPath, f))
            .ToList();
    }

    /// <summary>Extracts an entry returned by <see cref="ListFiles" /> to bytes. Thread-safe.</summary>
    public byte[] Extract(ArchiveEntry entry) => entry.Record switch
    {
        Ba2FileRecord r => _ba2!.ExtractFile(r),
        BsaFileRecord r => _bsa!.ExtractFile(r),
        _ => throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.")
    };

    /// <summary>
    ///     Reads a file by its full virtual path (case-insensitive, accepts <c>/</c> or <c>\</c>), or
    ///     null when absent. Builds a path index on first use for O(1) lookups.
    /// </summary>
    public byte[]? ReadFile(string fullPath)
    {
        _byPath ??= BuildIndex();
        return _byPath.TryGetValue(Normalize(fullPath), out var entry) ? Extract(entry) : null;
    }

    public void Dispose()
    {
        _bsa?.Dispose();
        _ba2?.Dispose();
    }

    private Dictionary<string, ArchiveEntry> BuildIndex()
    {
        var map = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ListFiles())
        {
            map[Normalize(entry.FullPath)] = entry;
        }

        return map;
    }

    private static string Normalize(string path) => path.Replace('/', '\\');

    /// <summary>One archive entry: its virtual path plus the backing BSA/BA2 record used to extract it.</summary>
    public readonly record struct ArchiveEntry(string FullPath, object Record);
}
