using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Bsa.Index;

namespace BethesdaMultitool.Core.Vfs;

/// <summary>
///     <see cref="IGameFileSystem" /> over a single BSA or BA2, dispatched by file magic via
///     <see cref="ArchiveReader.Open" />. Reads are lock-free and concurrent: BSA extraction is
///     memory-mapped per call and BA2 extraction reads absolute offsets from one shared read-only
///     view accessor — both proven under the 3D viewer's parallel decode workers.
///     <para>
///         Three open modes: eager-owned (<see cref="ArchiveFileSystem(string)" />), eager over a
///         registry lease (<see cref="ArchiveFileSystem(ArchiveLease)" /> — Dispose releases the
///         lease, not the shared reader), and lazy (<see cref="CreateLazy" /> — the open is
///         deferred to first access so mounting a Data folder stays cheap; a layer whose deferred
///         open fails logs once and behaves as empty, mirroring the mount-time corrupt-skip).
///     </para>
/// </summary>
public sealed class ArchiveFileSystem : IGameFileSystem
{
    private readonly string _archivePath;
    private readonly object _openLock = new();
    private readonly ArchiveHandleRegistry? _registry;
    private bool _disposed;
    private IDisposable? _handle;
    private volatile bool _openAttempted;
    private ArchiveReader? _reader;

    /// <summary>Opens <paramref name="archivePath" /> (BSA or BA2 by magic) and owns the reader.</summary>
    public ArchiveFileSystem(string archivePath)
    {
        _archivePath = archivePath;
        Label = archivePath;
        _reader = ArchiveReader.Open(archivePath);
        _handle = _reader;
        _openAttempted = true;
    }

    /// <summary>
    ///     Wraps a shared registry handle. Dispose releases the lease; the reader lives until the
    ///     last lease on it releases.
    /// </summary>
    public ArchiveFileSystem(ArchiveLease lease)
    {
        _archivePath = lease.ArchivePath;
        Label = lease.ArchivePath;
        _reader = lease.Reader;
        _handle = lease;
        _openAttempted = true;
    }

    private ArchiveFileSystem(string archivePath, ArchiveHandleRegistry? registry)
    {
        _archivePath = archivePath;
        _registry = registry;
        Label = archivePath;
        // _openAttempted stays false: the open happens on first member access.
    }

    /// <summary>
    ///     The wrapped reader, for consumers needing format-specific surfaces. Forces the open in
    ///     lazy mode and throws if it failed. When the reader came from a registry lease it is
    ///     shared state — never dispose it or enable conversion toggles on it.
    /// </summary>
#pragma warning disable S4275 // deliberate lazy-init: TryGetReader() performs the deferred open and returns _reader
    public ArchiveReader Reader =>
        TryGetReader() ?? throw new InvalidOperationException($"Archive failed to open: {Label}");
#pragma warning restore S4275

    public string Label { get; }

    public bool Exists(string path)
    {
        return TryGetReader()?.FindEntry(path) is not null;
    }

    public GameFileEntry? TryStat(string path)
    {
        return TryGetReader()?.FindEntry(path) is { } entry ? ToEntry(entry) : null;
    }

    public byte[]? TryReadAllBytes(string path)
    {
        if (TryGetReader() is not { } reader || reader.FindEntry(path) is not { } entry)
        {
            return null;
        }

        try
        {
            return reader.Extract(entry);
        }
        catch (Exception ex) when (IsExtractionFailure(ex))
        {
            // Corrupt/unsupported stored payload (truncated recovery, XMem/LZX entry, garbage
            // record fields): null lets a layered mount fall through to the next copy, matching
            // the per-source tolerance of the hand-rolled chains this API replaces.
            return null;
        }
    }

    public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null)
    {
        if (TryGetReader() is not { } reader)
        {
            yield break;
        }

        var normalizedPrefix = prefix is null ? null : VfsPath.Normalize(prefix);
        foreach (var entry in reader.ListFiles())
        {
            var normalized = VfsPath.Normalize(entry.FullPath);
            if (VfsPath.MatchesPrefix(normalized, normalizedPrefix))
            {
                yield return new GameFileEntry(normalized, entry.Size, Label);
            }
        }
    }

    public void Dispose()
    {
        lock (_openLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _openAttempted = true; // a lazy mount disposed before first touch must never open
            _handle?.Dispose();
            _handle = null;
            _reader = null;
        }
    }

    /// <summary>
    ///     Creates a filesystem whose archive open (parse + memory map) is deferred to the first
    ///     member access — via <paramref name="registry" /> when given, else a private open. A
    ///     failed deferred open is logged once and the filesystem behaves as empty.
    /// </summary>
    public static ArchiveFileSystem CreateLazy(string archivePath, ArchiveHandleRegistry? registry = null)
    {
        return new ArchiveFileSystem(archivePath, registry);
    }

    private ArchiveReader? TryGetReader()
    {
        if (_openAttempted)
        {
            return _reader;
        }

        lock (_openLock)
        {
            if (_openAttempted)
            {
                return _reader;
            }

            try
            {
                if (_registry is not null)
                {
                    var lease = _registry.Acquire(_archivePath);
                    _reader = lease.Reader;
                    _handle = lease;
                }
                else
                {
                    _reader = ArchiveReader.Open(_archivePath);
                    _handle = _reader;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                           or UnauthorizedAccessException or EndOfStreamException)
            {
                // Same tolerance the eager mount applies at construction, deferred to first
                // touch: the layer stays mounted but empty so resolution falls through.
                Logger.Instance.Info("Vfs: archive failed to open, treating as empty: {0} ({1})", Label, ex.Message);
            }

            _openAttempted = true;
            return _reader;
        }
    }

    private static bool IsExtractionFailure(Exception ex)
    {
        return ex is IOException or InvalidDataException or NotSupportedException or EndOfStreamException
            or ArgumentException or OverflowException;
    }

    private GameFileEntry ToEntry(ArchiveReader.ArchiveEntry entry)
    {
        return new GameFileEntry(VfsPath.Normalize(entry.FullPath), entry.Size, Label);
    }
}
