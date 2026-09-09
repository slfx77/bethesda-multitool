using BethesdaMultitool.Core.Formats.DiscImage;
using Microsoft.Win32.SafeHandles;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     The original Xbox's disc filesystem behind the archive seam — see <see cref="XdvdfsVolume" />
///     for the layout and for why this must be probed BEFORE the ISO9660 backend. Fallout:
///     Brotherhood of Steel's Xbox release is one such image: 491 files, 2,773,983,632 bytes, and
///     the same <c>.DDF</c>/<c>.SDB</c>/<c>.CLP</c> content the PS2 disc ships under <c>DATA\</c>,
///     here under <c>resx\</c> and the per-level directories beneath <c>resx\c1</c>…<c>c4</c>.
///     <para>
///         ⚑ NOT only the original Xbox: the Xbox 360 uses the same filesystem, so this backend is
///         also what claims a full XGD2 redump — <c>Fallout - New Vegas (USA, Europe).iso</c>
///         (7,838,695,424 B, partition at <c>0x0FD90000</c>, 172 files, 5,064,591,774 payload
///         bytes) mounts here rather than through the ISO9660 backend, which sees only that
///         image's 13-file <c>_SYSTEMU</c>/<c>VIDEO_TS</c> dummy partition.
///     </para>
///     <para>
///         Immutable after open and safe for unsynchronised concurrent reads, per the
///         <c>Core/Vfs</c> contract: every read is a positioned <see cref="RandomAccess" /> call on
///         one shared handle, so no seek state is shared and no lock is needed. The image is 2.6 GB
///         with a single 670 MB entry, which is why extraction to disk streams rather than going
///         through the interface's default <c>byte[]</c>.
///     </para>
/// </summary>
internal sealed class XdvdfsBackend : IArchiveBackend
{
    private const int CopyBufferSize = 1 << 20;
    private readonly SafeFileHandle _handle;

    private readonly XdvdfsVolume _volume;

    private XdvdfsBackend(XdvdfsVolume volume, SafeFileHandle handle)
    {
        _volume = volume;
        _handle = handle;
    }

    public string FormatName =>
        _volume.PartitionOffset == 0
            ? "XDVDFS (Xbox disc)"
            : $"XDVDFS (Xbox disc, partition at 0x{_volume.PartitionOffset:X})";

    public string PlatformLabel => "Xbox";

    public int TotalFiles => _volume.Files.Count;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var list = new List<ArchiveEntry>(_volume.Files.Count);
        foreach (var file in _volume.Files)
        {
            var extension = Path.GetExtension(file.Name);
            list.Add(new ArchiveEntry(
                file.FullPath,
                file.Directory,
                file.Name,
                extension.Length <= 1 ? string.Empty : extension.ToLowerInvariant(),
                file.Size,
                _volume.OffsetOf(file),
                false,
                file));
        }

        return list;
    }

    public IEnumerable<string> EnumerateFilePaths()
    {
        foreach (var file in _volume.Files)
        {
            yield return file.FullPath;
        }
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        var record = RecordOf(entry);
        var bytes = new byte[record.Size];
        if (record.Size == 0)
        {
            // Legal: the retail disc ships one zero-length file.
            return bytes;
        }

        var read = RandomAccess.Read(_handle, bytes, _volume.OffsetOf(record));
        var total = read;
        while (total < bytes.Length && read > 0)
        {
            read = RandomAccess.Read(_handle, bytes.AsSpan(total), _volume.OffsetOf(record) + total);
            total += read;
        }

        if (total != bytes.Length)
        {
            throw new InvalidDataException(
                $"XDVDFS entry '{record.FullPath}' is truncated: read {total} of {record.Size} bytes.");
        }

        return bytes;
    }

    /// <summary>
    ///     Streams an entry to disk. Overridden because the interface default materialises the whole
    ///     payload first, and this format's largest single entry on the retail disc is
    ///     <c>resx\sfx.clp</c> at 670,697,472 bytes.
    /// </summary>
    public async Task<bool> ExtractToDiskAsync(ArchiveEntry entry, string outputDir, bool overwrite)
    {
        var record = RecordOf(entry);
        var relative = record.FullPath.Replace('/', '\\').TrimStart('\\');
        if (relative.Length == 0 || Path.IsPathRooted(relative) ||
            relative.Split('\\').Any(static part => part == ".."))
        {
            throw new InvalidOperationException($"Archive entry path is not extractable: '{record.FullPath}'.");
        }

        var target = Path.Combine(outputDir, relative);
        if (!overwrite && File.Exists(target))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var output = new FileStream(
            target, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, true);

        var buffer = new byte[CopyBufferSize];
        var offset = _volume.OffsetOf(record);
        long remaining = record.Size;
        while (remaining > 0)
        {
            var wanted = (int)Math.Min(buffer.Length, remaining);
            var read = await RandomAccess.ReadAsync(_handle, buffer.AsMemory(0, wanted), offset).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new InvalidDataException(
                    $"XDVDFS entry '{record.FullPath}' is truncated: {remaining} bytes unread at image offset {offset}.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            offset += read;
            remaining -= read;
        }

        return true;
    }

    /// <summary>
    ///     Folder census straight from the parsed tree, which is what
    ///     <see cref="IArchiveBackend.GetFolderStats" /> asks a backend with a real folder tree to
    ///     provide. The difference from the default is the seeding: every DIRECTORY the walk found
    ///     is listed, including the ones that hold no file at all. ⚑ Deriving folders from file
    ///     paths — what the default does — loses five of the Brotherhood of Steel disc's 67
    ///     (<c>resx/c1</c>, <c>c2</c>, <c>c3</c>, <c>pc</c>, <c>t</c>: pure container directories
    ///     whose children are all directories) and five of the Fallout: New Vegas X360 disc's 41
    ///     (<c>Data/Music</c> and its four subdirectories).
    /// </summary>
    public Dictionary<string, int> GetFolderStats()
    {
        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["(root)"] = 0
        };

        foreach (var directory in _volume.Directories)
        {
            stats[directory] = 0;
        }

        foreach (var file in _volume.Files)
        {
            var folder = file.Directory.Length == 0 ? "(root)" : file.Directory;
            stats.TryGetValue(folder, out var count);
            stats[folder] = count + 1;
        }

        return stats.OrderByDescending(static kv => kv.Value)
            .ToDictionary(static kv => kv.Key, static kv => kv.Value);
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    /// <summary>Whether <paramref name="path" /> is an XDVDFS image. See <see cref="XdvdfsVolume.TryProbe" />.</summary>
    public static bool TryProbe(string path)
    {
        return XdvdfsVolume.TryProbe(path);
    }

    /// <summary>Opens an image, throwing <see cref="InvalidDataException" /> when the walk is not exact.</summary>
    public static XdvdfsBackend Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        XdvdfsVolume volume;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            volume = XdvdfsVolume.Read(stream);
        }

        var handle = File.OpenHandle(path);
        return new XdvdfsBackend(volume, handle);
    }

    private static XdvdfsEntry RecordOf(ArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Record as XdvdfsEntry
               ?? throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
    }
}
