using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Semantic;

/// <summary>
///     Resolves ESM load order from MAST dependencies.
/// </summary>
internal static class EsmLoadOrderResolver
{
    /// <summary>
    ///     Reads every .esm in a directory and topologically sorts them into load order from their MAST master
    ///     dependencies.
    /// </summary>
    internal static async Task<IReadOnlyList<EsmLoadOrderFile>> ResolveDirectoryAsync(
        string baseDirPath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(baseDirPath))
        {
            throw new DirectoryNotFoundException($"Base directory not found: {baseDirPath}");
        }

        var esmFiles = Directory.GetFiles(baseDirPath, "*.esm").ToList();
        if (esmFiles.Count == 0)
        {
            return [];
        }

        var files = new List<(string Path, string FileName, EsmFileHeader Header)>();
        foreach (var esmFile in esmFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var header = await ReadHeaderAsync(esmFile, cancellationToken);
            if (header != null)
            {
                files.Add((esmFile, Path.GetFileName(esmFile), header));
            }
        }

        var knownFileNames = files
            .Select(file => file.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = files.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<(string Path, string FileName, EsmFileHeader Header)>();
        var orderedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(file => file.Header.Masters.All(master =>
                    !knownFileNames.Contains(master) || orderedNames.Contains(master)))
                .OrderBy(file => file.Header.Masters.Count)
                .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ready.Count == 0)
            {
                ready = remaining.Values
                    .OrderBy(file => file.Header.Masters.Count)
                    .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                    .Take(1)
                    .ToList();
            }

            foreach (var file in ready)
            {
                ordered.Add(file);
                orderedNames.Add(file.FileName);
                remaining.Remove(file.FileName);
            }
        }

        return ordered
            .Select((file, index) => new EsmLoadOrderFile(file.Path, file.FileName, file.Header, index))
            .ToList();
    }

    private static async Task<EsmFileHeader?> ReadHeaderAsync(string esmFile, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(esmFile);
        var headerBytes = new byte[Math.Min(8192, fileInfo.Length)];
        await using var fs = File.OpenRead(esmFile);
        var bytesRead = await fs.ReadAsync(headerBytes.AsMemory(0, headerBytes.Length), cancellationToken);
        var span = headerBytes.AsSpan(0, bytesRead);
        // ParseFileHeader is TES4-only; without the TES3 fallback a Morrowind Data Files directory
        // resolved EMPTY ("No ESM/ESP sources found") because every header read returned null.
        return EsmParser.ParseFileHeader(span) ?? ParseTes3FileHeader(span);
    }

    /// <summary>
    ///     Minimal TES3 (Morrowind) file-header parse — just what load-order resolution needs.
    ///     TES3 record header = 16 bytes (signature + u32 dataSize + u32 + u32 flags); subrecords are
    ///     sig[4] + u32 size + payload. Masters are MAST zstrings (Tribunal/Bloodmoon master
    ///     Morrowind.esm, so the MAST topological sort applies unchanged); HEDR = float version,
    ///     u32 fileType, char[32] author, char[256] description, u32 record count.
    /// </summary>
    private static EsmFileHeader? ParseTes3FileHeader(ReadOnlySpan<byte> data)
    {
        const int recordHeaderSize = 16;
        if (data.Length < recordHeaderSize) return null;
        if (data[0] != (byte)'T' || data[1] != (byte)'E' || data[2] != (byte)'S' || data[3] != (byte)'3')
        {
            return null;
        }

        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        // Callers pass a file PREFIX (~8 KB) — clamp like the TES4 path so a large header (many
        // masters) truncates cleanly instead of overrunning the buffer.
        var end = Math.Min(data.Length, recordHeaderSize + (int)Math.Min(dataSize, int.MaxValue));

        var masters = new List<string>();
        var version = 0f;
        string? author = null;
        string? description = null;

        var pos = recordHeaderSize;
        while (pos + 8 <= end)
        {
            var sig = Encoding.ASCII.GetString(data.Slice(pos, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            pos += 8;
            if (size > (uint)(end - pos)) break; // truncated tail — keep what we have
            var payload = data.Slice(pos, (int)size);
            switch (sig)
            {
                case "HEDR":
                    if (payload.Length >= 4)
                    {
                        version = BinaryPrimitives.ReadSingleLittleEndian(payload);
                    }

                    if (payload.Length >= 40)
                    {
                        author = ReadFixedString(payload.Slice(8, 32));
                    }

                    if (payload.Length >= 296)
                    {
                        description = ReadFixedString(payload.Slice(40, 256));
                    }

                    break;
                case "MAST":
                    masters.Add(ReadFixedString(payload));
                    break;
            }

            pos += (int)size;
        }

        return new EsmFileHeader
        {
            Version = version,
            Author = author,
            Description = description,
            Masters = masters,
            IsBigEndian = false
        };
    }

    /// <summary>ASCII up to the first NUL (TES3 fixed-size and zstring fields are both NUL-padded).</summary>
    private static string ReadFixedString(ReadOnlySpan<byte> data)
    {
        var nul = data.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul >= 0 ? data[..nul] : data);
    }
}
