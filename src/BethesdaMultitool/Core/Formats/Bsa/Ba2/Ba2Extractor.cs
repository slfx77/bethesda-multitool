// Copyright (c) 2026 BethesdaMultitool Contributors
// Licensed under the MIT License.

using System.IO.Compression;
using System.IO.MemoryMappedFiles;

namespace BethesdaMultitool.Core.Formats.Bsa.Ba2;

/// <summary>
///     Extracts files from BA2 (Fallout 4 / Fallout 76) archives. BA2 is always little-endian.
///     Thread-safe: uses a memory-mapped file for lock-free concurrent reads, mirroring
///     <see cref="BsaExtractor" />. GNRL entries decompress to their stored data; DX10 entries get a
///     synthesized DDS header (<see cref="Ba2DdsHeaderWriter" />) followed by their decompressed mip
///     chunks, producing a standard .dds file.
/// </summary>
public sealed class Ba2Extractor : IDisposable
{
    private readonly MemoryMappedFile _mappedFile;
    private readonly Ba2CompressionFormat _compression;
    private readonly uint _version;
    private bool _disposed;

    /// <summary>Create an extractor for a BA2 archive file.</summary>
    public Ba2Extractor(string filePath)
    {
        Archive = Ba2Parser.Parse(filePath);
        _compression = Archive.Header.CompressionFormat;
        _version = Archive.Header.Version;
        _mappedFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
    }

    /// <summary>Create an extractor from an already-parsed archive backed by a <see cref="FileStream" />.</summary>
    public Ba2Extractor(Ba2Archive archive, FileStream stream)
    {
        Archive = archive;
        _compression = archive.Header.CompressionFormat;
        _version = archive.Header.Version;
        _mappedFile = MemoryMappedFile.CreateFromFile(
            stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
    }

    /// <summary>The parsed archive.</summary>
    public Ba2Archive Archive { get; }

    public void Dispose()
    {
        if (!_disposed)
        {
            _mappedFile.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    ///     Extract a single file to a byte array. For DX10 textures this includes a synthesized DDS
    ///     header so the result is a valid .dds. Thread-safe: each call creates its own view accessors.
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
        if (file.Compressed)
        {
            var packed = ReadRegion((long)file.Offset, (int)file.PackedSize);
            return Decompress(packed, (int)file.RealSize);
        }

        return ReadRegion((long)file.Offset, (int)file.RealSize);
    }

    private byte[] ExtractTexture(Ba2FileRecord file)
    {
        var tex = file.Texture
                  ?? throw new InvalidDataException($"DX10 entry '{file.FullPath}' has no texture metadata.");

        var header = Ba2DdsHeaderWriter.BuildHeader(tex, _version);

        using var ms = new MemoryStream(header.Length + (int)tex.Chunks.Sum(c => (long)c.FullSize));
        ms.Write(header, 0, header.Length);

        foreach (var chunk in tex.Chunks)
        {
            if (chunk.Compressed)
            {
                var packed = ReadRegion((long)chunk.Offset, (int)chunk.PackedSize);
                var data = Decompress(packed, (int)chunk.FullSize);
                ms.Write(data, 0, data.Length);
            }
            else
            {
                var data = ReadRegion((long)chunk.Offset, (int)chunk.FullSize);
                ms.Write(data, 0, data.Length);
            }
        }

        return ms.ToArray();
    }

    /// <summary>Reads <paramref name="length" /> bytes from the archive at <paramref name="offset" />.</summary>
    private byte[] ReadRegion(long offset, int length)
    {
        if (length <= 0)
        {
            return [];
        }

        var buffer = new byte[length];
        using var accessor = _mappedFile.CreateViewAccessor(offset, length, MemoryMappedFileAccess.Read);
        accessor.ReadArray(0, buffer, 0, length);
        return buffer;
    }

    /// <summary>
    ///     Decompresses one packed region to <paramref name="realSize" /> bytes using the archive's
    ///     codec: a raw LZ4 block (FO76 v3) via the shared <see cref="Lz4BlockDecoder" />, otherwise
    ///     zlib with a raw-deflate fallback (FO4 + most FO76).
    /// </summary>
    private byte[] Decompress(byte[] packed, int realSize)
    {
        if (realSize <= 0)
        {
            return [];
        }

        if (_compression == Ba2CompressionFormat.Lz4)
        {
            var dst = new byte[realSize];
            Lz4BlockDecoder.DecodeBlock(packed, dst);
            return dst;
        }

        var result = new byte[realSize];
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

    /// <summary>Extract a single file to disk under <paramref name="outputDir" /> at its virtual path.</summary>
    public async Task<bool> ExtractFileToDiskAsync(Ba2FileRecord file, string outputDir, bool overwrite = false)
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
        await File.WriteAllBytesAsync(outputPath, data).ConfigureAwait(false);
        return true;
    }

    /// <summary>Extract all files to <paramref name="outputDir" />, reporting progress per file.</summary>
    public async Task<int> ExtractAllAsync(
        string outputDir,
        bool overwrite = false,
        IProgress<(int current, int total, string fileName)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = Archive.AllFiles.ToList();
        var extracted = 0;
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            progress?.Report((i + 1, files.Count, file.FullPath));
            if (await ExtractFileToDiskAsync(file, outputDir, overwrite).ConfigureAwait(false))
            {
                extracted++;
            }
        }

        return extracted;
    }

    /// <summary>File-extension distribution (parity with <c>BsaExtractor.GetExtensionStats</c>).</summary>
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
