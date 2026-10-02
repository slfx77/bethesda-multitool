using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BethesdaMultitool.Core.Formats.DiscImage.Chd;

/// <summary>One metadata record of a CHD: a FourCC tag, a flags byte and its payload.</summary>
internal sealed record ChdMetadata(uint Tag, byte Flags, byte[] Data);

/// <summary>
///     One track of a CD-shaped CHD, from its <c>CHT2</c>/<c>CHTR</c> metadata, plus where its
///     frames sit in the image: <see cref="ChdFrameOffset" /> counts the 2448-byte frames before
///     it (every track is padded to a multiple of four frames in the file).
/// </summary>
internal sealed record ChdCdTrack(
    int Number,
    string Type,
    string SubType,
    int Frames,
    int Pregap,
    string PregapType,
    string PregapSubType,
    int Postgap,
    long ChdFrameOffset)
{
    public bool IsAudio => Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Bytes of sector data each frame carries for this track type: the raw 2352 for
    ///     <c>MODE1_RAW</c>/<c>MODE2_RAW</c>/<c>AUDIO</c>, 2048 for cooked Mode 1 and Mode 2 Form 1,
    ///     2336 for cooked Mode 2 (subheader + data), 2324 for Form 2.
    /// </summary>
    public int DataSize => Type.ToUpperInvariant() switch
    {
        "MODE1_RAW" or "MODE2_RAW" or "AUDIO" => 2352,
        "MODE1" or "MODE2_FORM1" => 2048,
        "MODE2" or "MODE2_FORM_MIX" => 2336,
        "MODE2_FORM2" => 2324,
        _ => throw new InvalidDataException($"CHD track type '{Type}' is not a CD track type this reader knows."),
    };
}

/// <summary>
///     A native reader for MAME's CHD (Compressed Hunks of Data) container, version 5 — the form
///     every optical original in the corpus is stored in. Hunks are located through the
///     Huffman-and-delta coded map, decompressed by the codec the map names (<c>zlib</c>,
///     <c>lzma</c>, <c>huff</c>, <c>flac</c> and their CD-framed variants <c>cdzl</c>, <c>cdlz</c>,
///     <c>cdfl</c>), and checked against the CRC-16 the map carries for each. The header's
///     <see cref="RawSha1" /> is the SHA-1 of the whole logical image, so a full read can prove
///     itself against the writer's own hash.
///     <para>
///         Clean-room: written from the format's behaviour as libchdr (BSD-3-Clause) documents it
///         and from the public LZMA, FLAC and ECMA-130 specifications — see THIRD_PARTY_LICENSES.
///         MAME's own GPL sources were not opened. Parent-linked (delta) CHDs are refused; the
///         corpus never writes them.
///     </para>
///     <para>
///         Reads are serialised on one lock: the codec instances and scratch buffers are shared,
///         and the callers that matter read whole images sequentially.
///     </para>
/// </summary>
internal sealed class ChdFile : IDisposable
{
    public const int HeaderSize = 124;
    public const int CdFrameSize = 2448;
    public const int CdSectorSize = 2352;
    public const int CdSubcodeSize = 96;
    public const int CdTrackPadding = 4;

    public const uint TagZlib = 0x7A6C6962;   // 'zlib'
    public const uint TagLzma = 0x6C7A6D61;   // 'lzma'
    public const uint TagHuff = 0x68756666;   // 'huff'
    public const uint TagFlac = 0x666C6163;   // 'flac'
    public const uint TagCdZlib = 0x63647A6C; // 'cdzl'
    public const uint TagCdLzma = 0x63646C7A; // 'cdlz'
    public const uint TagCdFlac = 0x6364666C; // 'cdfl'

    public const uint MetaCdTrack2 = 0x43485432; // 'CHT2'
    public const uint MetaCdTrack = 0x43485452;  // 'CHTR'
    public const uint MetaGdTrack = 0x43484744;  // 'CHGD'
    public const uint MetaGdTrackOld = 0x43484754; // 'CHGT'
    public const uint MetaDvd = 0x44564420;      // 'DVD '

    private const byte CompressionNone = 4;

    /// <summary>Not a container code: this reader's own marker for an unallocated hunk of an uncompressed image.</summary>
    private const byte CompressionZero = 0xFF;
    private const byte CompressionSelf = 5;
    private const byte CompressionParent = 6;
    private const byte CompressionRleSmall = 7;
    private const byte CompressionRleLarge = 8;
    private const byte CompressionSelf0 = 9;
    private const byte CompressionSelf1 = 10;
    private const byte CompressionParentSelf = 11;
    private const byte CompressionParent0 = 12;
    private const byte CompressionParent1 = 13;

    /// <summary>
    ///     chdman encodes each CD sector's 16-bit sample words big-endian into FLAC (it swaps on a
    ///     little-endian host before encoding), so a decoded sample is written back high byte
    ///     first. Pinned by the cdfl fixture's SHA-1 in the tests.
    /// </summary>
    private const bool CdFlacBigEndian = true;

    private static readonly byte[] Magic = "MComprHD"u8.ToArray();

    private readonly SafeFileHandle _handle;
    private readonly MapEntry[] _map;
    private readonly Lock _lock = new();
    private LzmaRawDecoder? _lzma;
    private FlacStreamDecoder? _flac;
    private byte[] _sectorScratch = [];
    private byte[] _subcodeScratch = [];

    private ChdFile(string path, SafeFileHandle handle, uint[] compressors, long logicalBytes, long mapOffset, long metaOffset,
        int hunkBytes, int unitBytes, byte[] rawSha1, byte[] sha1, byte[] parentSha1)
    {
        Path = path;
        _handle = handle;
        Compressors = compressors;
        LogicalBytes = logicalBytes;
        MapOffset = mapOffset;
        MetaOffset = metaOffset;
        HunkBytes = hunkBytes;
        UnitBytes = unitBytes;
        RawSha1 = rawSha1;
        Sha1 = sha1;
        ParentSha1 = parentSha1;
        HunkCount = (int)((logicalBytes + hunkBytes - 1) / hunkBytes);
        _map = ReadMap();
        Metadata = ReadMetadata();
        Tracks = ParseTracks();
    }

    public string Path { get; }

    /// <summary>The four codec slots, as FourCC tags; zero for an unused slot. All zero means uncompressed.</summary>
    public IReadOnlyList<uint> Compressors { get; }

    public long LogicalBytes { get; }
    public long MapOffset { get; }
    public long MetaOffset { get; }
    public int HunkBytes { get; }
    public int UnitBytes { get; }
    public int HunkCount { get; }

    /// <summary>SHA-1 of the logical image (what <c>chdman info</c> prints as "Data SHA1").</summary>
    public byte[] RawSha1 { get; }

    /// <summary>SHA-1 over the raw data plus the metadata (what <c>chdman info</c> prints as "SHA1"; what <c>expectedSha1</c> pins).</summary>
    public byte[] Sha1 { get; }

    public byte[] ParentSha1 { get; }

    public IReadOnlyList<ChdMetadata> Metadata { get; }

    /// <summary>The CD tracks, in order; empty for a DVD-shaped (or raw) image.</summary>
    public IReadOnlyList<ChdCdTrack> Tracks { get; }

    public bool IsCdShaped => Tracks.Count > 0;

    public bool HasParent => ParentSha1.Any(b => b != 0);

    public string Sha1Hex => Convert.ToHexString(Sha1).ToLowerInvariant();

    public string RawSha1Hex => Convert.ToHexString(RawSha1).ToLowerInvariant();

    /// <summary>Whether the file opens with the CHD magic and is version 5 (the only version this reader takes).</summary>
    public static bool IsChd(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path);
            Span<byte> head = stackalloc byte[16];
            if (RandomAccess.Read(handle, head, 0) != head.Length)
            {
                return false;
            }

            return head[..8].SequenceEqual(Magic) && BinaryPrimitives.ReadUInt32BigEndian(head[12..]) == 5;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public static ChdFile Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var handle = File.OpenHandle(path);
        try
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            if (RandomAccess.Read(handle, header, 0) != HeaderSize)
            {
                throw new InvalidDataException($"'{System.IO.Path.GetFileName(path)}' is shorter than a CHD header.");
            }

            if (!header[..8].SequenceEqual(Magic))
            {
                throw new InvalidDataException($"'{System.IO.Path.GetFileName(path)}' does not open with the CHD magic.");
            }

            var headerLength = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
            var version = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
            if (version != 5 || headerLength != HeaderSize)
            {
                throw new InvalidDataException($"CHD version {version} (header {headerLength} bytes) is not the v5 layout this reader implements.");
            }

            var compressors = new uint[4];
            for (var i = 0; i < 4; i++)
            {
                compressors[i] = BinaryPrimitives.ReadUInt32BigEndian(header[(16 + 4 * i)..]);
            }

            var logicalBytes = (long)BinaryPrimitives.ReadUInt64BigEndian(header[32..]);
            var mapOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(header[40..]);
            var metaOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(header[48..]);
            var hunkBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(header[56..]);
            var unitBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(header[60..]);
            if (hunkBytes <= 0 || unitBytes <= 0 || hunkBytes % unitBytes != 0 || logicalBytes < 0 || mapOffset <= 0)
            {
                throw new InvalidDataException("CHD header carries an impossible hunk/unit/map geometry.");
            }

            return new ChdFile(path, handle, compressors, logicalBytes, mapOffset, metaOffset, hunkBytes, unitBytes,
                header.Slice(64, 20).ToArray(), header.Slice(84, 20).ToArray(), header.Slice(104, 20).ToArray());
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Decodes hunk <paramref name="hunkIndex" /> into the first <see cref="HunkBytes" /> bytes of <paramref name="destination" />.</summary>
    public void ReadHunk(int hunkIndex, Span<byte> destination)
    {
        if (hunkIndex < 0 || hunkIndex >= HunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(hunkIndex));
        }

        if (destination.Length < HunkBytes)
        {
            throw new ArgumentException("Destination is smaller than a hunk.", nameof(destination));
        }

        lock (_lock)
        {
            ReadHunkCore(hunkIndex, destination[..HunkBytes], 0);
        }
    }

    /// <summary>
    ///     SHA-1 of every logical byte, decoded through this reader. Equal to <see cref="RawSha1" />
    ///     when every hunk, codec and CRC agreed — the proof the reader rests on.
    /// </summary>
    public byte[] ComputeRawSha1()
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[HunkBytes];
        var remaining = LogicalBytes;
        for (var hunk = 0; hunk < HunkCount && remaining > 0; hunk++)
        {
            ReadHunk(hunk, buffer);
            var take = (int)Math.Min(HunkBytes, remaining);
            sha1.AppendData(buffer, 0, take);
            remaining -= take;
        }

        return sha1.GetHashAndReset();
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    private void ReadHunkCore(int hunkIndex, Span<byte> destination, int depth)
    {
        if (depth > 8)
        {
            throw new InvalidDataException("CHD map: self-references chain too deep.");
        }

        var entry = _map[hunkIndex];
        switch (entry.Type)
        {
            case CompressionZero:
                destination.Clear();
                return;

            case CompressionNone:
                ReadAt(entry.Offset, destination);
                VerifyCrc(entry, destination, hunkIndex);
                return;

            case CompressionSelf:
                if (entry.Offset < 0 || entry.Offset >= HunkCount)
                {
                    throw new InvalidDataException($"CHD map: hunk {hunkIndex} refers to hunk {entry.Offset}, outside the image.");
                }

                ReadHunkCore((int)entry.Offset, destination, depth + 1);
                return;

            case CompressionParent:
                throw new NotSupportedException("This CHD refers to a parent image; delta CHDs are not supported.");

            case < 4:
            {
                var tag = Compressors[entry.Type];
                if (tag == 0)
                {
                    throw new InvalidDataException($"CHD map: hunk {hunkIndex} names codec slot {entry.Type}, which the header leaves empty.");
                }

                var compressed = ArrayPool<byte>.Shared.Rent((int)entry.Length);
                try
                {
                    var span = compressed.AsSpan(0, (int)entry.Length);
                    ReadAt(entry.Offset, span);
                    Decompress(tag, span, destination);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(compressed);
                }

                VerifyCrc(entry, destination, hunkIndex);
                return;
            }

            default:
                throw new InvalidDataException($"CHD map: hunk {hunkIndex} carries unknown compression code {entry.Type}.");
        }
    }

    private static void VerifyCrc(in MapEntry entry, ReadOnlySpan<byte> hunk, int hunkIndex)
    {
        if (!entry.HasCrc)
        {
            // An uncompressed image's 4-byte map carries no CRC; checking against a zero would
            // fail every hunk of it.
            return;
        }

        var crc = Crc16Ccitt.Compute(hunk);
        if (crc != entry.Crc)
        {
            throw new InvalidDataException($"CHD hunk {hunkIndex} decoded to CRC-16 {crc:X4}; the map says {entry.Crc:X4}.");
        }
    }

    private void Decompress(uint tag, ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        switch (tag)
        {
            case TagZlib:
                InflateRaw(compressed, destination);
                return;

            case TagLzma:
                (_lzma ??= new LzmaRawDecoder()).Decode(compressed, destination);
                return;

            case TagHuff:
            {
                var reader = new ChdBitReader(compressed);
                var decoder = new ChdHuffmanDecoder(256, 16);
                decoder.ImportTreeHuffman(ref reader);
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (byte)decoder.DecodeOne(ref reader);
                }

                if (reader.Overflow)
                {
                    throw new InvalidDataException("CHD huff hunk ran past its compressed data.");
                }

                return;
            }

            case TagFlac:
            {
                if (compressed.Length < 1 || compressed[0] is not ((byte)'L' or (byte)'B'))
                {
                    throw new InvalidDataException("CHD flac hunk does not open with its 'L'/'B' byte-order marker.");
                }

                var bigEndian = compressed[0] == (byte)'B';
                (_flac ??= new FlacStreamDecoder()).Decode(compressed[1..], 2, destination.Length / 4, destination, bigEndian);
                return;
            }

            case TagCdZlib:
            case TagCdLzma:
            case TagCdFlac:
                DecompressCd(tag, compressed, destination);
                return;

            default:
                throw new NotSupportedException($"CHD codec '{TagToString(tag)}' is not supported by this reader.");
        }
    }

    /// <summary>
    ///     The CD framing, which is NOT the same for all three CD codecs.
    ///     <para>
    ///         <c>cdzl</c> and <c>cdlz</c>: a bitmap of which frames had their ECC stripped, then a
    ///         2-byte (3-byte once a hunk reaches 64 KiB) length for the sector stream, then the
    ///         sector data (2352 × frames) through the base codec, then the subcode (96 × frames)
    ///         through raw deflate. A set bit means chdman DROPPED that sector's sync header and
    ///         ECC because they were reconstructible, so the reader puts them back.
    ///     </para>
    ///     <para>
    ///         ⚠⚠ <c>cdfl</c> is framed differently and shares none of that: its FLAC stream starts
    ///         at byte 0 with NO ecc bitmap and NO length field, the subcode begins wherever the
    ///         FLAC decoder stopped, and it never strips ECC — so regenerating any is corruption.
    ///         Reading it the other way costs (frames+7)/8 bytes of the FLAC stream and then treats
    ///         FLAC payload as an ECC bitmap, which rewrites arbitrary sectors. (Two reviewers
    ///         caught this here on 2026-09-09; confirmed against libchdr's cdfl codec, which
    ///         hands <c>src</c> straight to the decoder while cdzl/cdlz delegate to the shared
    ///         <c>cd_codec_decompress</c>.)
    ///     </para>
    /// </summary>
    private void DecompressCd(uint tag, ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        if (HunkBytes % CdFrameSize != 0)
        {
            throw new InvalidDataException($"CHD CD hunk of {HunkBytes} bytes is not a whole number of 2448-byte frames.");
        }

        var frames = HunkBytes / CdFrameSize;
        var sectorBytes = frames * CdSectorSize;
        var subcodeBytes = frames * CdSubcodeSize;
        if (_sectorScratch.Length < sectorBytes)
        {
            _sectorScratch = new byte[sectorBytes];
        }

        if (_subcodeScratch.Length < subcodeBytes)
        {
            _subcodeScratch = new byte[subcodeBytes];
        }

        var sectors = _sectorScratch.AsSpan(0, sectorBytes);
        var subcode = _subcodeScratch.AsSpan(0, subcodeBytes);
        var flac = tag == TagCdFlac;
        var eccBytes = flac ? 0 : (frames + 7) / 8;
        int subcodeStart;
        if (flac)
        {
            var consumed = (_flac ??= new FlacStreamDecoder()).Decode(compressed, 2, sectorBytes / 4, sectors, CdFlacBigEndian);
            subcodeStart = consumed;
        }
        else
        {
            var lengthBytes = HunkBytes < 65536 ? 2 : 3;
            var header = eccBytes + lengthBytes;
            if (compressed.Length < header)
            {
                throw new InvalidDataException("CHD CD hunk is shorter than its framing header.");
            }

            var compressedLength = (compressed[eccBytes] << 8) | compressed[eccBytes + 1];
            if (lengthBytes == 3)
            {
                compressedLength = (compressedLength << 8) | compressed[eccBytes + 2];
            }

            if (header + compressedLength > compressed.Length)
            {
                throw new InvalidDataException("CHD CD hunk states more sector data than it holds.");
            }

            var payload = compressed.Slice(header, compressedLength);
            if (tag == TagCdLzma)
            {
                (_lzma ??= new LzmaRawDecoder()).Decode(payload, sectors);
            }
            else
            {
                InflateRaw(payload, sectors);
            }

            subcodeStart = header + compressedLength;
        }

        InflateRaw(compressed[subcodeStart..], subcode);

        for (var frame = 0; frame < frames; frame++)
        {
            var target = destination.Slice(frame * CdFrameSize, CdFrameSize);
            sectors.Slice(frame * CdSectorSize, CdSectorSize).CopyTo(target);
            subcode.Slice(frame * CdSubcodeSize, CdSubcodeSize).CopyTo(target[CdSectorSize..]);
            if (!flac && (compressed[frame / 8] & (1 << (frame % 8))) != 0)
            {
                CdSectorEcc.SyncPattern.CopyTo(target);
                CdSectorEcc.Generate(target[..CdSectorSize]);
            }
        }
    }

    private static void InflateRaw(ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        // DeflateStream is raw DEFLATE, which is exactly how the CHD zlib codec writes (no zlib
        // wrapper). It wants a stream, so the compressed bytes are handed over through a pooled copy.
        var rented = ArrayPool<byte>.Shared.Rent(compressed.Length);
        try
        {
            compressed.CopyTo(rented);
            using var input = new MemoryStream(rented, 0, compressed.Length, writable: false);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            inflater.ReadExactly(destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void ReadAt(long offset, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = RandomAccess.Read(_handle, buffer[total..], offset + total);
            if (read <= 0)
            {
                throw new InvalidDataException($"CHD '{System.IO.Path.GetFileName(Path)}' is truncated at byte {offset + total}.");
            }

            total += read;
        }
    }

    private MapEntry[] ReadMap()
    {
        var map = new MapEntry[HunkCount];
        if (Compressors[0] == 0)
        {
            // Uncompressed image: one big-endian 32-bit hunk NUMBER per hunk, and there is no CRC
            // to check. ⚠ A stored zero is the sparse sentinel — "this hunk was never written, it
            // reads as zeros" — not a pointer at hunk 0, which is why it becomes CompressionZero
            // rather than an offset of 0.
            var raw = new byte[HunkCount * 4];
            ReadAt(MapOffset, raw);
            for (var hunk = 0; hunk < HunkCount; hunk++)
            {
                var hunkNumber = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(hunk * 4));
                map[hunk] = hunkNumber == 0
                    ? new MapEntry(CompressionZero, (uint)HunkBytes, 0, 0, HasCrc: false)
                    : new MapEntry(CompressionNone, (uint)HunkBytes, (long)hunkNumber * HunkBytes, 0, HasCrc: false);
            }

            return map;
        }

        Span<byte> header = stackalloc byte[16];
        ReadAt(MapOffset, header);
        var mapBytes = BinaryPrimitives.ReadUInt32BigEndian(header);
        var firstOffset = ((long)BinaryPrimitives.ReadUInt16BigEndian(header[4..]) << 32) | BinaryPrimitives.ReadUInt32BigEndian(header[6..]);
        var mapCrc = BinaryPrimitives.ReadUInt16BigEndian(header[10..]);
        int lengthBits = header[12], selfBits = header[13], parentBits = header[14];
        if (lengthBits > 32 || selfBits > 32 || parentBits > 32)
        {
            throw new InvalidDataException("CHD map header carries impossible bit widths.");
        }

        var compressedMap = new byte[mapBytes];
        ReadAt(MapOffset + 16, compressedMap);
        var reader = new ChdBitReader(compressedMap);
        var decoder = new ChdHuffmanDecoder(16, 8);
        decoder.ImportTreeRle(ref reader);

        // Pass one: the compression code of every hunk, run-length coded.
        var types = new byte[HunkCount];
        byte lastType = 0;
        var repeat = 0;
        for (var hunk = 0; hunk < HunkCount; hunk++)
        {
            if (repeat > 0)
            {
                types[hunk] = lastType;
                repeat--;
                continue;
            }

            var value = (byte)decoder.DecodeOne(ref reader);
            if (value == CompressionRleSmall)
            {
                types[hunk] = lastType;
                repeat = 2 + (int)decoder.DecodeOne(ref reader);
            }
            else if (value == CompressionRleLarge)
            {
                types[hunk] = lastType;
                repeat = 2 + 16 + ((int)decoder.DecodeOne(ref reader) << 4);
                repeat += (int)decoder.DecodeOne(ref reader);
            }
            else
            {
                types[hunk] = lastType = value;
            }
        }

        // Pass two: lengths, offsets and CRCs, with self/parent references delta-coded.
        var currentOffset = firstOffset;
        long lastSelf = 0;
        long lastParent = 0;
        var raw12 = new byte[HunkCount * 12];
        for (var hunk = 0; hunk < HunkCount; hunk++)
        {
            var type = types[hunk];
            uint length = 0;
            var offset = currentOffset;
            ushort crc = 0;
            var hasCrc = false;
            switch (type)
            {
                case < 4:
                    length = reader.Read(lengthBits);
                    currentOffset += length;
                    crc = (ushort)reader.Read(16);
                    hasCrc = true;
                    break;
                case CompressionNone:
                    length = (uint)HunkBytes;
                    currentOffset += length;
                    crc = (ushort)reader.Read(16);
                    hasCrc = true;
                    break;
                case CompressionSelf:
                    offset = reader.Read(selfBits);
                    lastSelf = offset;
                    break;
                case CompressionParent:
                    offset = reader.Read(parentBits);
                    lastParent = offset;
                    break;
                case CompressionSelf0:
                    type = CompressionSelf;
                    offset = lastSelf;
                    break;
                case CompressionSelf1:
                    type = CompressionSelf;
                    offset = ++lastSelf;
                    break;
                case CompressionParentSelf:
                    type = CompressionParent;
                    offset = lastParent = (long)hunk * HunkBytes / UnitBytes;
                    break;
                case CompressionParent0:
                    type = CompressionParent;
                    offset = lastParent;
                    break;
                case CompressionParent1:
                    type = CompressionParent;
                    lastParent += HunkBytes / UnitBytes;
                    offset = lastParent;
                    break;
                default:
                    throw new InvalidDataException($"CHD map: unknown compression code {type} for hunk {hunk}.");
            }

            map[hunk] = new MapEntry(type, length, offset, crc, HasCrc: hasCrc);
            var entry = raw12.AsSpan(hunk * 12, 12);
            entry[0] = type;
            entry[1] = (byte)(length >> 16);
            entry[2] = (byte)(length >> 8);
            entry[3] = (byte)length;
            for (var i = 0; i < 6; i++)
            {
                entry[4 + i] = (byte)(offset >> (8 * (5 - i)));
            }

            BinaryPrimitives.WriteUInt16BigEndian(entry[10..], crc);
        }

        if (reader.Overflow)
        {
            throw new InvalidDataException("CHD map ran past its compressed bytes.");
        }

        var computed = Crc16Ccitt.Compute(raw12);
        if (computed != mapCrc)
        {
            throw new InvalidDataException($"CHD map CRC-16 {computed:X4} does not match the header's {mapCrc:X4}.");
        }

        return map;
    }

    private List<ChdMetadata> ReadMetadata()
    {
        var records = new List<ChdMetadata>();
        var offset = MetaOffset;
        Span<byte> header = stackalloc byte[16];
        while (offset != 0 && records.Count < 4096)
        {
            ReadAt(offset, header);
            var tag = BinaryPrimitives.ReadUInt32BigEndian(header);
            var lengthAndFlags = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            var next = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            var length = (int)(lengthAndFlags & 0x00FFFFFF);
            var data = new byte[length];
            ReadAt(offset + 16, data);
            records.Add(new ChdMetadata(tag, (byte)(lengthAndFlags >> 24), data));
            offset = next;
        }

        return records;
    }

    private List<ChdCdTrack> ParseTracks()
    {
        var tracks = new List<ChdCdTrack>();
        long frameOffset = 0;
        foreach (var record in Metadata)
        {
            if (record.Tag is not (MetaCdTrack2 or MetaCdTrack or MetaGdTrack or MetaGdTrackOld))
            {
                continue;
            }

            var text = Encoding.ASCII.GetString(record.Data).TrimEnd('\0');
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = token.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    fields[token[..colon]] = token[(colon + 1)..];
                }
            }

            if (!fields.TryGetValue("TRACK", out var numberText) || !int.TryParse(numberText, out var number) ||
                !fields.TryGetValue("FRAMES", out var framesText) || !int.TryParse(framesText, out var frames))
            {
                throw new InvalidDataException($"CHD track metadata is not in the TRACK:/FRAMES: form: '{text}'.");
            }

            fields.TryGetValue("PREGAP", out var pregapText);
            fields.TryGetValue("POSTGAP", out var postgapText);
            tracks.Add(new ChdCdTrack(
                number,
                fields.GetValueOrDefault("TYPE", "MODE1_RAW"),
                fields.GetValueOrDefault("SUBTYPE", "NONE"),
                frames,
                int.TryParse(pregapText, out var pregap) ? pregap : 0,
                fields.GetValueOrDefault("PGTYPE", "MODE1"),
                fields.GetValueOrDefault("PGSUB", "NONE"),
                int.TryParse(postgapText, out var postgap) ? postgap : 0,
                frameOffset));

            frameOffset += (frames + CdTrackPadding - 1) / CdTrackPadding * CdTrackPadding;
        }

        if (tracks.Count > 0)
        {
            tracks.Sort((a, b) => a.Number.CompareTo(b.Number));
            var declared = frameOffset * CdFrameSize;
            var stored = LogicalBytes;
            if (UnitBytes != CdFrameSize || declared > stored)
            {
                throw new InvalidDataException(
                    $"CHD track metadata accounts for {declared:N0} bytes of frames but the image holds {stored:N0} of {UnitBytes}-byte units.");
            }
        }

        return tracks;
    }

    private static string TagToString(uint tag)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, tag);
        return Encoding.ASCII.GetString(bytes);
    }

    private readonly record struct MapEntry(byte Type, uint Length, long Offset, ushort Crc, bool HasCrc);
}
