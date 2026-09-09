using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Zip;

/// <summary>
///     Exact-arithmetic reader for plain PKZIP archives — the J2ME JARs of the TES Travels titles
///     and Fallout Tactics' <c>.bos</c> archives, both verified plain zip. Written against the
///     APPNOTE structure from the bytes rather than through <c>System.IO.Compression.ZipArchive</c>,
///     which holds one stream and is not safe for the unsynchronised concurrent reads the archive
///     backend contract requires; here the directory is parsed once and every
///     <see cref="Extract" /> opens its own stream.
///     <para>
///         The probe is exact in the sense the archive chain demands: the file must open with a
///         local-header signature, the end-of-central-directory record must end exactly at EOF
///         (its comment length included), the central directory must end exactly where that
///         record begins, and walking the declared number of central headers must land exactly on
///         the record too. ZIP64, multi-disk, encrypted and non-stored/non-deflate members are
///         refused with a reason rather than guessed at — none appears in any fixture.
///     </para>
/// </summary>
public static class PkZipParser
{
    /// <summary>Compression method 0 — stored.</summary>
    public const ushort MethodStored = 0;

    /// <summary>Compression method 8 — DEFLATE.</summary>
    public const ushort MethodDeflate = 8;

    /// <summary>General-purpose flag bit 0: traditional PKWARE encryption.</summary>
    public const ushort FlagEncrypted = 0x0001;

    /// <summary>General-purpose flag bit 6: strong encryption.</summary>
    public const ushort FlagStrongEncryption = 0x0040;

    /// <summary>General-purpose flag bit 11: the name and comment are UTF-8.</summary>
    public const ushort FlagUtf8 = 0x0800;

    /// <summary>Fixed length of a local file header before its name and extra field.</summary>
    public const int LocalHeaderLength = 30;

    /// <summary>Fixed length of a central directory header before its variable fields.</summary>
    public const int CentralHeaderLength = 46;

    /// <summary>Fixed length of the end-of-central-directory record before its comment.</summary>
    public const int EndRecordLength = 22;

    private const uint LocalHeaderSignature = 0x04034B50; // "PK\3\4"
    private const uint CentralHeaderSignature = 0x02014B50; // "PK\1\2"
    private const uint EndRecordSignature = 0x06054B50; // "PK\5\6"
    private const uint Zip64LocatorSignature = 0x07064B50; // "PK\6\7"
    private const int Zip64LocatorLength = 20;
    private const int MaxCommentLength = ushort.MaxValue;

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>True when <paramref name="path" /> is a plain PKZIP archive by the exact rules above.</summary>
    public static bool TryProbe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadDirectory(stream, path, out _) is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Parses the central directory, throwing <see cref="InvalidDataException" /> with the reason on failure.</summary>
    public static PkZipArchive Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return TryReadDirectory(stream, path, out var reason) ??
               throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a plain PKZIP archive: {reason}");
    }

    /// <summary>
    ///     Reads one entry's payload from <paramref name="archive" />'s file with a private stream,
    ///     so concurrent callers never share a position. Verifies the local header signature, bounds
    ///     <paramref name="entry" />'s payload against the central directory, and checks both the
    ///     exact decompressed length and the CRC-32 before returning.
    /// </summary>
    public static byte[] Extract(PkZipArchive archive, PkZipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.IsDirectory)
        {
            return [];
        }

        if (entry.IsEncrypted || (entry.Flags & FlagStrongEncryption) != 0)
        {
            throw new InvalidDataException($"Zip entry '{entry.Name}' is encrypted; no decryption is implemented.");
        }

        if (!entry.IsStored && !entry.IsDeflated)
        {
            throw new InvalidDataException(
                $"Zip entry '{entry.Name}' uses compression method {entry.Method}; only stored (0) and deflate (8) are read.");
        }

        using var stream = new FileStream(archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (entry.LocalHeaderOffset + (long)LocalHeaderLength > archive.CentralDirectoryOffset)
        {
            throw new InvalidDataException(
                $"Zip entry '{entry.Name}' places its local header at {entry.LocalHeaderOffset}, past the central directory.");
        }

        Span<byte> local = stackalloc byte[LocalHeaderLength];
        stream.Position = entry.LocalHeaderOffset;
        stream.ReadExactly(local);
        if (BinaryPrimitives.ReadUInt32LittleEndian(local) != LocalHeaderSignature)
        {
            throw new InvalidDataException(
                $"Zip entry '{entry.Name}' has no local header signature at {entry.LocalHeaderOffset}.");
        }

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(local[26..]);
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(local[28..]);
        var dataStart = entry.LocalHeaderOffset + LocalHeaderLength + nameLength + extraLength;
        if (dataStart + entry.CompressedSize > archive.CentralDirectoryOffset)
        {
            throw new InvalidDataException(
                $"Zip entry '{entry.Name}' payload ({entry.CompressedSize} bytes at {dataStart}) runs into the central directory at {archive.CentralDirectoryOffset}.");
        }

        var compressed = new byte[entry.CompressedSize];
        stream.Position = dataStart;
        stream.ReadExactly(compressed);

        byte[] data;
        if (entry.IsStored)
        {
            if (entry.CompressedSize != entry.UncompressedSize)
            {
                throw new InvalidDataException(
                    $"Zip entry '{entry.Name}' is stored but declares {entry.CompressedSize} compressed vs {entry.UncompressedSize} uncompressed bytes.");
            }

            data = compressed;
        }
        else
        {
            data = new byte[entry.UncompressedSize];
            using var inflater = new DeflateStream(new MemoryStream(compressed, false), CompressionMode.Decompress);
            var read = 0;
            while (read < data.Length)
            {
                var n = inflater.Read(data, read, data.Length - read);
                if (n <= 0)
                {
                    throw new InvalidDataException(
                        $"Zip entry '{entry.Name}' inflated to {read} bytes, short of the declared {entry.UncompressedSize}.");
                }

                read += n;
            }

            if (inflater.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    $"Zip entry '{entry.Name}' inflates past its declared {entry.UncompressedSize} bytes.");
            }
        }

        var crc = ComputeCrc32(data);
        if (crc != entry.Crc32)
        {
            throw new InvalidDataException(
                $"Zip entry '{entry.Name}' CRC-32 mismatch: computed 0x{crc:X8}, directory says 0x{entry.Crc32:X8}.");
        }

        return data;
    }

    /// <summary>CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320) as PKZIP stores it.</summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static PkZipArchive? TryReadDirectory(FileStream stream, string path, out string reason)
    {
        var length = stream.Length;
        if (length < LocalHeaderLength + EndRecordLength)
        {
            reason = $"file is {length} bytes, shorter than one local header plus the end record.";
            return null;
        }

        Span<byte> head = stackalloc byte[4];
        stream.Position = 0;
        stream.ReadExactly(head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != LocalHeaderSignature)
        {
            reason = "the file does not open with a local file header (PK\\x03\\x04).";
            return null;
        }

        // The end record is the last thing in the file, followed only by its own comment, so it
        // lies within the final 22 + 65535 bytes. Scan that tail for a signature whose comment
        // length makes the record end exactly at EOF; the exact-tail condition is what rejects a
        // stray signature inside a payload.
        var tailLength = (int)Math.Min(length, EndRecordLength + MaxCommentLength);
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);

        var endPosInTail = -1;
        for (var i = tailLength - EndRecordLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EndRecordSignature)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
            if (i + EndRecordLength + commentLength == tailLength)
            {
                endPosInTail = i;
                break;
            }
        }

        if (endPosInTail < 0)
        {
            reason = "no end-of-central-directory record ends exactly at EOF.";
            return null;
        }

        var end = tail.AsSpan(endPosInTail);
        var endOffset = length - tailLength + endPosInTail;
        var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(end[4..]);
        var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[6..]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[8..]);
        var entriesTotal = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        var commentBytes = end.Slice(EndRecordLength);

        if (diskNumber != 0 || directoryDisk != 0 || entriesOnDisk != entriesTotal)
        {
            reason = "multi-disk archives are not supported.";
            return null;
        }

        if (entriesTotal == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
        {
            reason = "the end record carries ZIP64 sentinels; ZIP64 is not supported.";
            return null;
        }

        if (endPosInTail >= Zip64LocatorLength &&
            BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(endPosInTail - Zip64LocatorLength)) ==
            Zip64LocatorSignature)
        {
            reason = "a ZIP64 end-of-central-directory locator precedes the end record; ZIP64 is not supported.";
            return null;
        }

        if (directoryOffset + (long)directorySize != endOffset)
        {
            reason =
                $"the central directory ({directoryOffset} + {directorySize}) does not end exactly at the end record ({endOffset}).";
            return null;
        }

        if (entriesTotal == 0)
        {
            reason = "the archive declares no entries.";
            return null;
        }

        var directory = new byte[directorySize];
        stream.Position = directoryOffset;
        stream.ReadExactly(directory);

        var entries = new List<PkZipEntry>(entriesTotal);
        var position = 0;
        for (var i = 0; i < entriesTotal; i++)
        {
            if (position + CentralHeaderLength > directory.Length)
            {
                reason = $"central header {i} at {directoryOffset + position} runs past the directory end.";
                return null;
            }

            var header = directory.AsSpan(position, CentralHeaderLength);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralHeaderSignature)
            {
                reason = $"central header {i} at {directoryOffset + position} lacks the PK\\x01\\x02 signature.";
                return null;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            var startDisk = BinaryPrimitives.ReadUInt16LittleEndian(header[34..]);
            var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);

            var variableLength = nameLength + extraLength + commentLength;
            if (position + CentralHeaderLength + variableLength > directory.Length)
            {
                reason = $"central header {i}'s name/extra/comment run past the directory end.";
                return null;
            }

            if (startDisk != 0)
            {
                reason = $"central header {i} starts on disk {startDisk}; multi-disk archives are not supported.";
                return null;
            }

            if (compressedSize == uint.MaxValue || uncompressedSize == uint.MaxValue || localOffset == uint.MaxValue)
            {
                reason = $"central header {i} carries ZIP64 sentinels; ZIP64 is not supported.";
                return null;
            }

            if (localOffset >= directoryOffset)
            {
                reason =
                    $"central header {i} points its local header at {localOffset}, inside or past the central directory.";
                return null;
            }

            var nameBytes = directory.AsSpan(position + CentralHeaderLength, nameLength);
            var name = (flags & FlagUtf8) != 0
                ? Encoding.UTF8.GetString(nameBytes)
                : Encoding.Latin1.GetString(nameBytes);

            entries.Add(new PkZipEntry(name, method, flags, crc, compressedSize, uncompressedSize, localOffset));
            position += CentralHeaderLength + variableLength;
        }

        if (position != directory.Length)
        {
            reason =
                $"walking {entriesTotal} central headers consumed {position} of the {directory.Length}-byte directory.";
            return null;
        }

        var comment = Encoding.Latin1.GetString(commentBytes);
        reason = string.Empty;
        return new PkZipArchive(path, entries, directoryOffset, endOffset, comment);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
