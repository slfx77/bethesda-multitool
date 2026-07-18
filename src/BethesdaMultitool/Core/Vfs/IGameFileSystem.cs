namespace BethesdaMultitool.Core.Vfs;

/// <summary>
///     One resolvable game file. <see cref="Path" /> is the normalized Data-relative virtual path
///     (backslash separators). <see cref="Size" /> is the size as stored in the source — the
///     on-disk size for loose files, the uncompressed size for BA2 entries, and the stored
///     (possibly compressed) payload size for BSA entries, whose true uncompressed size is only
///     known at extraction. <see cref="Source" /> labels where the winning copy lives (archive
///     path or loose root) for diagnostics.
/// </summary>
public sealed record GameFileEntry(string Path, long Size, string Source);

/// <summary>
///     A read-only virtual filesystem over game assets — one BSA, one BA2, a loose-file Data
///     directory, or an ordered layering of all three (see <see cref="LayeredGameFileSystem" />).
///     This is the shared API the archive formats are parsed through, so consumers stop caring
///     which container a path lives in.
///     <para>
///         <b>Thread-safety contract:</b> after construction, every member is safe for concurrent
///         use from any number of threads without external locking. Archive implementations sit on
///         memory-mapped files with position-independent reads (the same substrate the 3D viewer's
///         parallel mesh/texture workers already rely on), so N threads may read N different files
///         at once. Implementations must not add locks around reads.
///     </para>
///     <para>
///         Paths are case-insensitive and accept <c>/</c> or <c>\</c>. Opening/parsing an archive
///         is the expensive part — construct off the UI thread and share one instance.
///     </para>
/// </summary>
public interface IGameFileSystem : IDisposable
{
    /// <summary>Human-readable identity for diagnostics (archive path, loose root, or layer summary).</summary>
    string Label { get; }

    /// <summary>Whether <paramref name="path" /> resolves, without touching payload bytes.</summary>
    bool Exists(string path);

    /// <summary>Metadata for <paramref name="path" /> without extracting it, or null when absent.</summary>
    GameFileEntry? TryStat(string path);

    /// <summary>
    ///     The full decompressed payload of <paramref name="path" />, or null when absent
    ///     <b>or unextractable</b> (corrupt entry, unsupported codec, locked loose file) — so
    ///     <see cref="Exists" /> may be true where this returns null, and a layered filesystem
    ///     falls through to the next layer's copy instead of propagating the failure.
    /// </summary>
    byte[]? TryReadAllBytes(string path);

    /// <summary>
    ///     Enumerates entries, optionally filtered to virtual paths starting with
    ///     <paramref name="prefix" /> (case-insensitive, separator-normalized).
    /// </summary>
    IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null);
}

/// <summary>Path normalization shared by every <see cref="IGameFileSystem" /> implementation.</summary>
internal static class VfsPath
{
    /// <summary>Comparer for virtual-path keys.</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Normalizes separators to <c>\</c> and trims any leading separator.</summary>
    public static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');

    /// <summary>Whether a normalized path starts with a normalized prefix (null/empty = match all).</summary>
    public static bool MatchesPrefix(string normalizedPath, string? normalizedPrefix) =>
        string.IsNullOrEmpty(normalizedPrefix)
        || normalizedPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase);
}
