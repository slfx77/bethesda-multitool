using System.Buffers.Binary;
using System.IO.Compression;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     The envelope every compressed Shadowkey (N-Gage) per-zone file uses: a little-endian u32
///     inflated length followed by one raw zlib stream (RFC 1950) that runs to the end of the file.
///     Nothing follows the stream. Original RE 2026-09-05 from the retail bytes; the engine's own
///     string table calls the loader <c>load_compressed_file: %s</c>.
///     <para>
///         Shadowkey is Symbian/ARM, so every multi-byte value in these families is LITTLE-endian —
///         the opposite of the Redguard/Battlespire DOS-era readers next door.
///     </para>
///     <para>
///         Measured on all 21 retail zones: the six families that use this envelope
///         (<c>.zmp .zcp .zsk .ztx .zlu .zfg</c>) inflate to exactly their declared length,
///         126/126 files, every stream opening <c>78 9C</c> (deflate, default level). The sibling
///         <c>.zon .ent .sur .pal</c> files are NOT wrapped — their first u32 is already record
///         data, and inflating them fails.
///     </para>
///     <para>
///         Trap: the inflated payloads are in-memory struct dumps taken from an MSVC debug build.
///         Fixed-width string fields are followed by <c>0xCD</c> heap fill and by stale bytes left
///         over from earlier, longer records, so a reader must cut every string at its first NUL
///         and must not treat trailing bytes as content.
///     </para>
/// </summary>
internal static class ShadowkeyCompressedFile
{
    /// <summary>Bytes of little-endian inflated length in front of the zlib stream.</summary>
    public const int HeaderLength = 4;

    /// <summary>The zlib CMF byte every retail file carries (deflate, 32 KiB window).</summary>
    public const byte ZlibCmf = 0x78;

    /// <summary>Shortest file this envelope can describe: the length prefix plus a zlib header.</summary>
    public const int MinimumLength = HeaderLength + 2;

    /// <summary>
    ///     Content probe: a length prefix followed by a well-formed zlib header (CMF 0x78 and the
    ///     CMF/FLG word a multiple of 31). Cheap enough to run over every file in a directory.
    /// </summary>
    public static bool LooksLike(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < MinimumLength || bytes[HeaderLength] != ZlibCmf)
        {
            return false;
        }

        var header = (bytes[HeaderLength] << 8) | bytes[HeaderLength + 1];
        return header % 31 == 0;
    }

    /// <summary>
    ///     Inflates one envelope and returns the payload, verifying that its length equals the
    ///     declared one exactly. Throws <see cref="InvalidDataException" /> naming
    ///     <paramref name="name" /> and the byte position when the file is too short, does not open
    ///     a zlib stream at byte 4, does not inflate, or inflates to the wrong size.
    /// </summary>
    public static byte[] Inflate(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength + 1)
        {
            throw new InvalidDataException(
                $"'{name}': {bytes.Length} bytes is too short for the {HeaderLength}-byte inflated-length prefix and a zlib stream.");
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (!LooksLike(bytes))
        {
            var opening = bytes.Length >= MinimumLength
                ? $"0x{bytes[HeaderLength]:X2}{bytes[HeaderLength + 1]:X2}"
                : $"0x{bytes[HeaderLength]:X2}";
            throw new InvalidDataException(
                $"'{name}': no zlib stream at byte {HeaderLength} (opens {opening}, expected a 0x78 CMF whose CMF/FLG word divides by 31).");
        }

        byte[] payload;
        try
        {
            using var source = new MemoryStream(bytes, HeaderLength, bytes.Length - HeaderLength, false);
            using var inflater = new ZLibStream(source, CompressionMode.Decompress);
            using var target = new MemoryStream(declared <= 64 * 1024 * 1024 ? (int)declared : 0);
            inflater.CopyTo(target);
            payload = target.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"'{name}': the zlib stream at byte {HeaderLength} did not inflate ({ex.Message}).", ex);
        }

        if (payload.Length != declared)
        {
            throw new InvalidDataException(
                $"'{name}': the stream inflated to {payload.Length} bytes but byte 0 declares {declared}.");
        }

        return payload;
    }
}
