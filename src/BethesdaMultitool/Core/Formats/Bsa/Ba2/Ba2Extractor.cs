// Extraction follows the public BA2 format and fo76utils (extractBA2Texture / extractBlock in
// libfo76utils/src/ba2file.cpp). Decompression uses the framework's zlib and this repo's own
// LZ4 block decoder. Not derived from any copyleft source.

using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Orchestration;

namespace BethesdaMultitool.Core.Formats.Bsa.Ba2;

/// <summary>
///     Extracts files from BA2 (Fallout 4 / Fallout 76) archives. Thread-safe: a memory-mapped file
///     backs lock-free concurrent reads, mirroring <see cref="BsaExtractor" />. GNRL entries
///     decompress to their stored bytes; DX10 entries are prefixed with a synthesized DDS header
///     (<see cref="Ba2DdsHeaderWriter" />) and followed by their decompressed mip chunks to form a
///     standard .dds file.
/// </summary>
public sealed class Ba2Extractor : IDisposable
{
    // Four bounds the simultaneous packed + decoded + file-write buffers for large DX10 entries.
    // Keep this aligned with BsaExtractionEngine's established archive-extraction fan-out.
    internal const int MaxConcurrentBulkExtractions = 4;
    private readonly Ba2CompressionFormat _compression;

    private readonly MemoryMappedFile _mappedFile;
    private readonly uint _version;

    // ONE read-only view over the whole file, reused for every region read. Creating a view accessor is a
    // real MapViewOfFile (page-aligned, allocates a SafeHandle + view) — the old per-chunk accessor made a
    // DX10 texture pay N of those, which is why BA2 streamed much slower than BSA. A single read-only view
    // is safe for concurrent ReadArray from the resolve-queue workers (each reads into its own buffer at an
    // explicit absolute position; no shared mutable state).
    private readonly MemoryMappedViewAccessor _view;
    private bool _disposed;

    /// <summary>Open an extractor for a BA2 archive file.</summary>
    public Ba2Extractor(string filePath)
    {
        Archive = Ba2Parser.Parse(filePath);
        _compression = Archive.Header.CompressionFormat;
        _version = Archive.Header.Version;
        _mappedFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    /// <summary>Open an extractor over an already-parsed archive backed by a <see cref="FileStream" />.</summary>
    public Ba2Extractor(Ba2Archive archive, FileStream stream)
    {
        Archive = archive;
        _compression = archive.Header.CompressionFormat;
        _version = archive.Header.Version;
        _mappedFile = MemoryMappedFile.CreateFromFile(
            stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        _view = _mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    /// <summary>The parsed archive.</summary>
    public Ba2Archive Archive { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _view.Dispose();
        _mappedFile.Dispose();
        _disposed = true;
    }

    /// <summary>
    ///     Extract a single entry to a byte array. DX10 textures get a synthesized DDS header so the
    ///     result is a valid .dds. Thread-safe: each call reads absolute regions from the shared
    ///     read-only view into call-local buffers.
    /// </summary>
    public byte[] ExtractFile(Ba2FileRecord file)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return file.Kind == Ba2HeaderType.Texture
            ? ExtractTexture(file)
            : ExtractGeneral(file);
    }

    private byte[] ExtractGeneral(Ba2FileRecord file)
    {
        if (!file.Compressed)
        {
            return ReadRegion((long)file.Offset, (int)file.RealSize);
        }

        var packed = ReadRegion((long)file.Offset, (int)file.PackedSize);
        return Decompress(packed, (int)file.RealSize);
    }

    private byte[] ExtractTexture(Ba2FileRecord file)
    {
        var tex = file.Texture
                  ?? throw new InvalidDataException($"DX10 entry '{file.FullPath}' has no texture metadata.");

        var header = Ba2DdsHeaderWriter.BuildHeader(tex, _version);
        var surfaceBytes = tex.Chunks.Sum(c => c.FullSize);

        // Assemble straight into one pre-sized result buffer (header + decompressed mip chunks): the old
        // MemoryStream + ToArray() made a second full copy of the whole texture on every extract.
        var result = new byte[header.Length + (int)surfaceBytes];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        var pos = header.Length;

        foreach (var chunk in tex.Chunks)
        {
            var raw = ReadRegion((long)chunk.Offset, (int)(chunk.Compressed ? chunk.PackedSize : chunk.FullSize));
            var surface = chunk.Compressed ? Decompress(raw, (int)chunk.FullSize) : raw;
            Buffer.BlockCopy(surface, 0, result, pos, surface.Length);
            pos += surface.Length;
        }

        return result;
    }

    /// <summary>Reads <paramref name="length" /> bytes from the archive at absolute <paramref name="offset" />.</summary>
    private byte[] ReadRegion(long offset, int length)
    {
        if (length <= 0)
        {
            return [];
        }

        var buffer = new byte[length];
        // Absolute position into the whole-file view (no per-call MapViewOfFile).
        _view.ReadArray(offset, buffer, 0, length);
        return buffer;
    }

    /// <summary>
    ///     Decompress one packed region to exactly <paramref name="realSize" /> bytes using the
    ///     archive's codec: the shared <see cref="Lz4BlockDecoder" /> for raw-LZ4 (v3) archives,
    ///     otherwise zlib with a raw-deflate fallback (FO4 and most FO76).
    /// </summary>
    private byte[] Decompress(byte[] packed, int realSize)
    {
        if (realSize <= 0)
        {
            return [];
        }

        var result = new byte[realSize];

        if (_compression == Ba2CompressionFormat.Lz4)
        {
            Lz4BlockDecoder.DecodeBlock(packed, result);
            return result;
        }

        using var compressedStream = new MemoryStream(packed);
        try
        {
            using var zlib = new ZLibStream(compressedStream, CompressionMode.Decompress);
            zlib.ReadExactly(result, 0, realSize);
        }
        catch
        {
            compressedStream.Position = 0;
            using var deflate = new DeflateStream(compressedStream, CompressionMode.Decompress);
            deflate.ReadExactly(result, 0, realSize);
        }

        return result;
    }

    /// <summary>Extract one entry to disk under <paramref name="outputDir" /> at its virtual path.</summary>
    public async Task<bool> ExtractFileToDiskAsync(
        Ba2FileRecord file,
        string outputDir,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var outputPath = Path.Combine(outputDir, file.FullPath);
        if (!overwrite && File.Exists(outputPath))
        {
            return true;
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var data = ExtractFile(file);
        await File.WriteAllBytesAsync(outputPath, data, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Extract every entry to <paramref name="outputDir" />, reporting progress per file.</summary>
    public async Task<int> ExtractAllAsync(
        string outputDir,
        bool overwrite = false,
        IProgress<(int current, int total, string fileName)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = Archive.AllFiles.ToList();
        var extracted = 0;
        var started = 0;
        await ParallelWork.ForEachAsync(
            "ba2-extract",
            files,
            ConcurrencyPolicy.Fixed(MaxConcurrentBulkExtractions),
            async (file, ct) =>
            {
                var current = Interlocked.Increment(ref started);
                progress?.Report((current, files.Count, file.FullPath));
                if (await ExtractFileToDiskAsync(file, outputDir, overwrite, ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref extracted);
                }
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return extracted;
    }

    /// <summary>File-extension histogram (parity with <c>BsaExtractor.GetExtensionStats</c>).</summary>
    public Dictionary<string, int> GetExtensionStats()
    {
        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Archive.AllFiles)
        {
            var ext = Path.GetExtension(file.FullPath);
            if (string.IsNullOrEmpty(ext))
            {
                ext = string.IsNullOrEmpty(file.Extension) ? "(no extension)" : "." + file.Extension;
            }

            stats.TryGetValue(ext, out var count);
            stats[ext] = count + 1;
        }

        return stats.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
