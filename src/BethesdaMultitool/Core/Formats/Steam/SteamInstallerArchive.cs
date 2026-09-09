using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace BethesdaMultitool.Core.Formats.Steam;

/// <summary>
///     A Steam retail disc's installer payload: the plaintext <c>.sim</c> manifest plus the
///     <c>.sid</c> data parts it indexes.
///     <para>
///         ⚑⚑ The payload is AES-256-CBC and it OPENS — see the block scheme below. The New Vegas
///         retail DVD decodes to 431 of 431 files, every one an exact size match, including a
///         <c>FalloutNV.exe</c> that exists on no Steam depot.
///     </para>
///     <para>
///         Key derivation — ⚠⚠ the key is NEVER the depot key on its own:
///         <code>
///         aesKey (32 B) = &lt;16-byte legacy depot key&gt; || KeyCompletion
///         realIv (16 B) = AES-256-ECB-decrypt(aesKey, first 16 bytes of the block payload)
///         plaintext     = AES-256-CBC-decrypt(aesKey, realIv, remainder)
///         </code>
///         Both halves matter and each omission fails identically (noise), which is why an
///         exhaustive key sweep can run for a long time proving nothing: it says nothing about the
///         scheme while the DERIVATION is unknown.
///     </para>
///     <para>
///         Block layout — 8 plaintext bytes, then <c>Size</c> bytes of payload:
///         <code>
///         u24 size       bytes after this header; INCLUDES the 16-byte encrypted IV
///         u8  padSize    17..32 = the 16 IV bytes + 1..16 bytes of trailing pad
///         u24 chunkSize  bytes this block contributes to the file (after inflate, if compressed)
///         u8  flags      bit0 = encrypted, bit1 = compressed
///         </code>
///         ⚠ The last byte is FLAGS, not a "method" enum — only two values occur on retail (1 and
///         3), which reads convincingly as "1 = stored, 3 = compressed" and is wrong.
///     </para>
///     <para>
///         ⚠⚠ A COMPRESSED block's plaintext opens with a PKZIP local file header and then RAW
///         DEFLATE — no zlib wrapper. So a zlib-magic check (<c>78 9C</c>) scores ZERO on a
///         perfectly correct decrypt; the magic to expect is <c>50 4B 03 04</c>. A wrong-magic
///         control is indistinguishable from a wrong key, so this trap costs days.
///     </para>
/// </summary>
internal sealed class SteamInstallerArchive : IDisposable
{
    private const int BlockHeaderSize = 8;
    private const int AesBlockSize = 16;
    private const int PkHeaderFixedSize = 30;
    private const uint PkLocalFileSignature = 0x04034B50;

    private const int FlagEncrypted = 1;
    private const int FlagCompressed = 2;

    /// <summary>
    ///     The fixed 16 bytes that complete a 16-byte depot key into the AES-256 key. A format
    ///     constant of Valve's installer, in the same category as a magic number: without it the
    ///     container cannot be read at all.
    /// </summary>
    private static readonly byte[] KeyCompletion =
    [
        0xA8, 0x19, 0x4D, 0x02, 0x19, 0x3C, 0xD0, 0x37,
        0x92, 0x93, 0x7D, 0x27, 0x59, 0x0A, 0xEC, 0xBD
    ];

    private readonly SteamDepotKeyStore _keys;

    private readonly Dictionary<int, string> _parts;

    private SteamInstallerArchive(
        SteamInstallerManifest manifest,
        Dictionary<int, string> parts,
        SteamDepotKeyStore keys,
        string manifestPath)
    {
        Manifest = manifest;
        _parts = parts;
        _keys = keys;
        ManifestPath = manifestPath;
    }

    /// <summary>The parsed <c>.sim</c>.</summary>
    internal SteamInstallerManifest Manifest { get; }

    /// <summary>Path of the manifest this was opened from.</summary>
    internal string ManifestPath { get; }

    /// <summary>Discovered <c>.sid</c> parts, by part index.</summary>
    internal IReadOnlyDictionary<int, string> Parts => _parts;

    /// <summary>Total bytes across manifest and every discovered part.</summary>
    internal long ContainerSizeBytes =>
        new FileInfo(ManifestPath).Length + _parts.Values.Sum(static p => new FileInfo(p).Length);

    /// <summary>Depots referenced by the manifest that this archive has no key for.</summary>
    internal IReadOnlyList<uint> MissingKeyDepots =>
        [.. Manifest.Depots.Where(d => _keys.Find(d) is null)];

    public void Dispose()
    {
        // Streams are opened per extraction and closed there; nothing is retained.
    }

    /// <summary>
    ///     Opens the installer anchored on a <c>.sim</c>. Parts are found by the sibling naming
    ///     convention <c>&lt;stem&gt;_&lt;part&gt;.sid</c>. Keys are optional: without them the
    ///     archive still lists.
    /// </summary>
    internal static SteamInstallerArchive Open(string manifestPath, SteamDepotKeyStore? keys = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);
        var full = Path.GetFullPath(manifestPath);
        var manifest = SteamInstallerManifest.Parse(full);

        var directory = Path.GetDirectoryName(full) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(full);
        var parts = new Dictionary<int, string>();
        foreach (var candidate in Directory.EnumerateFiles(directory, stem + "_*.sid"))
        {
            var suffix = Path.GetFileNameWithoutExtension(candidate)[(stem.Length + 1)..];
            if (int.TryParse(suffix, out var index))
            {
                parts[index] = candidate;
            }
        }

        return new SteamInstallerArchive(manifest, parts, keys ?? SteamDepotKeyStore.LoadBeside(full), full);
    }

    /// <summary>Extracts one file into memory.</summary>
    internal byte[] Extract(SteamInstallerFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var buffer = new MemoryStream(file.Size > int.MaxValue ? 0 : (int)file.Size);
        ExtractTo(file, buffer);
        return buffer.ToArray();
    }

    /// <summary>
    ///     Streams one file into <paramref name="destination" />. Preferred for the multi-hundred-MB
    ///     members (the voice BSA is 1.6 GB), which is why this exists beside
    ///     <see cref="Extract" />.
    /// </summary>
    internal void ExtractTo(SteamInstallerFile file, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);

        var key = _keys.Find(file.DepotId)
                  ?? throw new InvalidOperationException(
                      $"No decryption key for depot {file.DepotId}. Supply a legacy 16-byte depot key " +
                      $"(see {nameof(SteamDepotKeyStore)}); '{file.Path}' cannot be extracted without one.");

        var aesKey = new byte[key.Length + KeyCompletion.Length];
        key.CopyTo(aesKey, 0);
        KeyCompletion.CopyTo(aesKey, key.Length);

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.Padding = PaddingMode.None;

        var part = file.PartIndex;
        var position = file.Offset;
        long written = 0;
        var header = new byte[BlockHeaderSize];

        while (written < file.Size)
        {
            if (!_parts.TryGetValue(part, out var partPath))
            {
                throw new InvalidDataException(
                    $"'{file.Path}' needs .sid part {part}, which is not present beside the manifest.");
            }

            using var stream = File.OpenRead(partPath);
            stream.Position = position;

            while (written < file.Size)
            {
                if (stream.ReadAtLeast(header, BlockHeaderSize, false) < BlockHeaderSize)
                {
                    break; // part exhausted: the file continues in the next one
                }

                var size = header[0] | (header[1] << 8) | (header[2] << 16);
                var padSize = header[3];
                var chunkSize = header[4] | (header[5] << 8) | (header[6] << 16);
                var flags = header[7];

                if (size <= 0 || padSize > size)
                {
                    throw new InvalidDataException(
                        $"'{file.Path}': block at {stream.Position - BlockHeaderSize} in part {part} " +
                        $"declares size {size}, pad {padSize}.");
                }

                var payload = new byte[size];
                if (stream.ReadAtLeast(payload, size, false) < size)
                {
                    break;
                }

                var plain = Decode(aes, payload, flags);
                var useful = Math.Min(size - padSize, plain.Length);
                if (useful < 0)
                {
                    throw new InvalidDataException($"'{file.Path}': negative payload length after padding.");
                }

                written += Emit(plain, useful, chunkSize, flags, destination);
                position = stream.Position;
            }

            if (written < file.Size)
            {
                part++;
                position = 0;
            }
        }

        if (written != file.Size)
        {
            throw new InvalidDataException(
                $"'{file.Path}': produced {written} bytes, manifest declares {file.Size}.");
        }
    }

    /// <summary>
    ///     Decrypts a block payload when it is encrypted. ⚠ The first 16 bytes are the IV and are
    ///     themselves encrypted — ECB, with the same key — so they are decrypted before use rather
    ///     than taken literally.
    /// </summary>
    private static byte[] Decode(Aes aes, byte[] payload, int flags)
    {
        if ((flags & FlagEncrypted) == 0)
        {
            return payload;
        }

        if (payload.Length < AesBlockSize)
        {
            throw new InvalidDataException("Encrypted block is shorter than its IV.");
        }

        var iv = aes.DecryptEcb(payload.AsSpan(0, AesBlockSize), PaddingMode.None);
        return aes.DecryptCbc(payload.AsSpan(AesBlockSize), iv, PaddingMode.None);
    }

    /// <summary>Writes a decoded block's contribution, inflating it when the block is compressed.</summary>
    private static long Emit(byte[] plain, int useful, int chunkSize, int flags, Stream destination)
    {
        if ((flags & FlagCompressed) == 0)
        {
            var count = Math.Min(useful, chunkSize);
            destination.Write(plain, 0, count);
            return count;
        }

        if (useful < PkHeaderFixedSize)
        {
            throw new InvalidDataException("Compressed block is shorter than a PK local file header.");
        }

        var signature = BinaryPrimitives.ReadUInt32LittleEndian(plain);
        if (signature != PkLocalFileSignature)
        {
            throw new InvalidDataException(
                $"Compressed block does not open with a PK local file header (got 0x{signature:X8}). " +
                "A wrong depot key fails exactly here.");
        }

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(26));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(28));
        var headerSize = PkHeaderFixedSize + nameLength + extraLength;
        if (headerSize > useful)
        {
            throw new InvalidDataException("PK local file header runs past the block.");
        }

        // Raw deflate: the PK header replaces the zlib wrapper, so DeflateStream is correct here.
        // Copied by hand rather than with CopyTo so the count does not depend on the destination
        // being seekable — callers legitimately pass forward-only streams.
        using var source = new MemoryStream(plain, headerSize, useful - headerSize, false);
        using var inflate = new DeflateStream(source, CompressionMode.Decompress);
        var buffer = new byte[81920];
        long produced = 0;
        int read;
        while ((read = inflate.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            produced += read;
        }

        return produced;
    }
}
