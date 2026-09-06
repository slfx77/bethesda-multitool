// Structure and sector handling ported from NeversoftMultitool (MIT License),
// src/NeversoftMultitool/Core/Formats/DiscImage/DiscImageArchive.cs at commit 314bc9e0
// (2026-08-14), reshaped onto this repository's IArchiveBackend seam. See THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Archives;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.DiscImage;

/// <summary>
///     CD images behind the archive seam: plain <c>.iso</c> (ISO9660), and <c>.cue</c> +
///     <c>.bin</c> (redump raw 2352-byte sectors, single- or multi-file). Data files list under
///     their ISO9660 paths and extract through the filesystem; a file that turns out to hold
///     Mode2 Form2 sectors (XA audio/video streams) extracts as a 2336-byte-sector stream so its
///     subheaders survive; CD audio tracks list as <c>audio/trackNN.wav</c> and extract as 44.1 kHz
///     16-bit stereo WAV — which is how Redguard's and Battlespire's Redbook music reaches disk.
///     <para>
///         The classic-game CD media this serves: Redguard Disc 1 (the install tree, including
///         the 3dfx <c>fxart</c> the Steam build omits), Disc 2 (the Smacker movies plus seven
///         audio tracks) and the Battlespire disc (eight audio tracks).
///     </para>
/// </summary>
internal sealed class DiscImageBackend : IArchiveBackend
{
    private const int SectorSize = 2048;
    private const int XaSectorSize = 2336;
    private const string AudioFolder = "audio";

    private static readonly byte[] SyncPattern =
        [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    private readonly IDiscSectorSource _source;
    private readonly List<DiscFileEntry> _files;
    private readonly List<(int Number, DiscTrackRegion Region)> _audioTracks;
    private readonly Lock _readLock = new();

    private DiscImageBackend(
        IDiscSectorSource source, string formatName, string? volumeId,
        List<DiscFileEntry> files, List<(int Number, DiscTrackRegion Region)> audioTracks)
    {
        _source = source;
        FormatName = formatName;
        VolumeId = volumeId;
        _files = files;
        _audioTracks = audioTracks;
    }

    public string FormatName { get; }

    public string PlatformLabel => "CD";

    /// <summary>The ISO9660 volume identifier, when the descriptor carries one.</summary>
    public string? VolumeId { get; }

    public int TotalFiles => _files.Count + _audioTracks.Count;

    /// <summary>
    ///     Extension + content gate. <c>.bin</c> needs a raw-sector sync pattern (bare .bin files
    ///     elsewhere are Dreamcast executables and Redguard's own <c>REDGUARD.bin</c> installer
    ///     payload), <c>.cue</c> must parse with every referenced file present, <c>.iso</c> must
    ///     carry a volume descriptor.
    /// </summary>
    public static bool TryProbe(string path)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".iso" => SniffIso(path),
                ".cue" => TryParseCue(path) != null,
                ".bin" => HasSyncPattern(path),
                _ => false
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Opens an image, throwing <see cref="InvalidDataException" /> when no ISO9660 volume is found.</summary>
    public static DiscImageBackend Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = Path.GetFileName(path);
        IDiscSectorSource? source = null;
        var audioTracks = new List<(int Number, DiscTrackRegion Region)>();
        string formatName;
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".cue":
                {
                    var cue = TryParseCue(path) ?? throw new InvalidDataException($"'{name}' is not a usable cue sheet (a data track and every referenced file are required).");
                    var regions = cue.BuildRegions();
                    source = new RawSectorSource(regions);
                    formatName = "CD image (CUE/BIN)";
                    var number = 0;
                    foreach (var region in regions)
                    {
                        number++;
                        if (region.IsAudio)
                        {
                            audioTracks.Add((number, region));
                        }
                    }

                    break;
                }

                case ".bin":
                {
                    if (!HasSyncPattern(path))
                    {
                        throw new InvalidDataException($"'{name}' has no raw CD sector sync pattern.");
                    }

                    var length = new FileInfo(path).Length;
                    var region = new DiscTrackRegion(0, length / RawSectorSource.RawSectorSize, path, 0, RawSectorSource.RawSectorSize, false);
                    source = new RawSectorSource([region]);
                    formatName = "CD image (raw BIN)";
                    break;
                }

                default:
                    source = new IsoSectorSource(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    formatName = "CD image (ISO)";
                    break;
            }

            if (!Iso9660FileSystem.HasVolumeDescriptor(source))
            {
                throw new InvalidDataException($"'{name}' carries no ISO9660 volume descriptor.");
            }

            var files = Iso9660FileSystem.ReadFileList(source);
            return new DiscImageBackend(source, formatName, Iso9660FileSystem.ReadVolumeId(source), files, audioTracks);
        }
        catch
        {
            source?.Dispose();
            throw;
        }
    }

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var list = new List<ArchiveEntry>(TotalFiles);
        foreach (var file in _files)
        {
            var ext = Path.GetExtension(file.Name);
            list.Add(new ArchiveEntry(
                file.FullPath, file.Directory, file.Name,
                string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant(),
                file.Size, file.ExtentLba * SectorSize, false, file));
        }

        foreach (var (number, region) in _audioTracks)
        {
            var name = $"track{number:D2}.wav";
            list.Add(new ArchiveEntry(
                $"{AudioFolder}/{name}", AudioFolder, name, ".wav",
                44 + region.SectorCountValue * region.PhysicalSectorSize,
                region.FileByteOffset, false, region));
        }

        return list;
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        using var output = new MemoryStream();
        ExtractTo(entry, output);
        return output.ToArray();
    }

    public async Task<bool> ExtractToDiskAsync(ArchiveEntry entry, string outputDir, bool overwrite)
    {
        var relative = entry.FullPath.Replace('/', '\\').TrimStart('\\');
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('\\').Any(static part => part == ".."))
        {
            throw new InvalidOperationException($"Archive entry path is not extractable: '{entry.FullPath}'.");
        }

        var target = Path.Combine(outputDir, relative);
        if (!overwrite && File.Exists(target))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await Task.Run(() => ExtractTo(entry, output)).ConfigureAwait(false);
        return true;
    }

    private void ExtractTo(ArchiveEntry entry, Stream output)
    {
        // The sector sources share one read buffer, so reads are serialised; the VFS contract
        // wants concurrent Extract calls to be safe, not fast.
        lock (_readLock)
        {
            switch (entry.Record)
            {
                case DiscFileEntry file:
                    ExtractFile(file, output);
                    break;
                case DiscTrackRegion region:
                    ExtractAudioTrack(region, output);
                    break;
                default:
                    throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
            }
        }
    }

    private void ExtractFile(DiscFileEntry file, Stream output)
    {
        var sectors = (file.Size + SectorSize - 1) / SectorSize;
        var buffer = new byte[SectorSize];
        long written = 0;
        for (long i = 0; i < sectors; i++)
        {
            var submode = _source.ReadSector(file.ExtentLba + i, buffer);
            if ((submode & 0x20) != 0 && _source.HasRawSectors)
            {
                // Form2 sector (XA audio / STR stream) — restart the file as a 2336-byte-sector
                // stream to preserve subheaders and Form2 payloads.
                output.SetLength(0);
                output.Position = 0;
                var tail = new byte[XaSectorSize];
                for (long k = 0; k < sectors; k++)
                {
                    _source.ReadSectorTail(file.ExtentLba + k, tail);
                    output.Write(tail, 0, XaSectorSize);
                }

                return;
            }

            var chunk = (int)Math.Min(SectorSize, file.Size - written);
            output.Write(buffer, 0, chunk);
            written += chunk;
        }
    }

    /// <summary>CD-DA: raw 2352-byte sectors are 44.1 kHz 16-bit LE stereo PCM.</summary>
    private static void ExtractAudioTrack(DiscTrackRegion region, Stream output)
    {
        using var input = new FileStream(region.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        input.Position = region.FileByteOffset;

        var dataBytes = region.SectorCountValue * region.PhysicalSectorSize;
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2); // stereo
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 44100 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);
        output.Write(header);

        var buffer = new byte[64 * 1024];
        var remaining = dataBytes;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            input.ReadExactly(buffer.AsSpan(0, chunk));
            output.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }

    private static bool SniffIso(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 17 * SectorSize)
        {
            return false;
        }

        using var source = new IsoSectorSource(stream);
        return Iso9660FileSystem.HasVolumeDescriptor(source);
    }

    private static CueSheet? TryParseCue(string path)
    {
        try
        {
            var cue = CueSheet.Parse(path);
            return cue.Tracks.Any(t => !t.IsAudio) && cue.Tracks.All(t => File.Exists(t.FilePath)) ? cue : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool HasSyncPattern(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < RawSectorSource.RawSectorSize)
        {
            return false;
        }

        Span<byte> head = stackalloc byte[12];
        stream.ReadExactly(head);
        return head.SequenceEqual(SyncPattern);
    }

    public void Dispose()
    {
        _source.Dispose();
    }
}
