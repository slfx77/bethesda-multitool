// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/CifRciFile.cs plus the
//   shared IMG header layout and RLE codec in BaseImageFile.cs. License texts are collected
//   centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     A Daggerfall <c>.CIF</c> / <c>.RCI</c> multi-image file — UI art, portrait sets and the
///     first-person weapon animations. Neither extension has a magic number or a master header;
///     the layout is chosen by FILENAME, exactly as the engine (and the reference) does it:
///     <para>
///         <b>Plain CIF</b> — a contiguous run of IMG-style records walked to end of file. Each
///         record is a 12-byte header (i16 x-offset, i16 y-offset, i16 width, i16 height,
///         u16 compression, u16 pixel-data length) followed by that many data bytes; the next
///         record starts at header + 12 + pixel-data length. Compression is 0x0000 (raw indices,
///         data length == width*height) or 0x0002 (byte-code RLE).
///     </para>
///     <para>
///         <b>RCI</b> — HEADERLESS: back-to-back raw frames whose dimensions come only from a
///         per-filename table (ported verbatim from the reference). Record count is the integer
///         quotient of file length over frame size; a remainder is ignored (retail TFAC00I0.RCI
///         carries 7 trailing junk bytes). FACES.CIF is an RCI in CIF clothing and routes here.
///     </para>
///     <para>
///         <b>Weapon CIF</b> (name contains "WEAPO") — one plain IMG "wielding" record (absent
///         from WEAPON09.CIF, the bow), then animation records walked to end of file. An
///         animation record is a 76-byte header (u16 width, u16 height, u16 last-frame width,
///         i16 x-offset, i16 last-frame y-offset, i16 data length, 31 u16 frame offsets,
///         u16 total size) whose nonzero offsets each point, relative to the record START, at an
///         RLE stream decoding to width*height pixels; the next record starts at record start +
///         total size.
///     </para>
///     <para>
///         The RLE (shared by plain-CIF 0x0002 records and every weapon frame): a code byte
///         &gt; 127 repeats the following byte (code - 127) times; a code &lt;= 127 copies
///         (code + 1) literal bytes. Verified against the retail install 2026-09-02: all 50 plain
///         CIFs tile exactly to EOF, all 19 weapon CIFs tile exactly to EOF with every one of
///         their 547 frames decoding to exactly width*height, and all 7 RCI-style files divide by
///         their table size (TFAC00I0.RCI alone leaving the 7-byte remainder).
///     </para>
/// </summary>
internal sealed class DaggerfallCifRciFile
{
    /// <summary>Bytes in the IMG-style record header shared with .IMG files.</summary>
    private const int ImgHeaderLength = 12;

    /// <summary>Slots in a weapon animation's frame offset table.</summary>
    private const int WeaponFrameOffsetSlots = 31;

    /// <summary>
    ///     Bytes of fixed fields before a weapon animation's frame offset table (width, height,
    ///     last-frame width, x-offset, last-frame y-offset, data length — six 16-bit values).
    /// </summary>
    private const int WeaponAnimationFixedFieldsLength = 12;

    /// <summary>
    ///     Bytes in a weapon animation header: 12 fixed + 31 u16 frame offsets + u16 total size.
    ///     The first frame always starts here (offset 76 on every retail animation).
    /// </summary>
    private const int WeaponAnimationHeaderLength =
        WeaponAnimationFixedFieldsLength + (WeaponFrameOffsetSlots * 2) + 2;

    /// <summary>Compression word of a raw record (data is width*height indices).</summary>
    private const ushort CompressionUncompressed = 0x0000;

    /// <summary>
    ///     Compression word of a byte-code RLE record. Weapon animation records carry no
    ///     compression word of their own; their records report this value because every weapon
    ///     frame is stored in the same RLE.
    /// </summary>
    private const ushort CompressionRleCompressed = 0x0002;

    /// <summary>Palette every CIF/RCI file is authored against (the reference hardcodes it).</summary>
    public const string PaletteName = "ART_PAL.COL";

    private DaggerfallCifRciFile(string name, IReadOnlyList<DaggerfallCifRciRecord> records)
    {
        Name = name;
        Records = records;
    }

    /// <summary>Logical file name (e.g. <c>WEAPON04.CIF</c>).</summary>
    public string Name { get; }

    /// <summary>The decoded records, each with its frames.</summary>
    public IReadOnlyList<DaggerfallCifRciRecord> Records { get; }

    /// <summary>Whether a file name follows the CIF/RCI convention.</summary>
    public static bool IsCifRciFileName(string fileName)
    {
        return fileName.EndsWith(".CIF", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".RCI", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses and decodes every record and frame, routing the layout by file name.</summary>
    public static DaggerfallCifRciFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        var upperName = name.ToUpperInvariant();

        // FACES.CIF is a headerless RCI despite the extension — the reference special-cases it.
        if (upperName.EndsWith(".RCI", StringComparison.Ordinal) || upperName == "FACES.CIF")
        {
            return new DaggerfallCifRciFile(name, ParseRci(bytes, name, upperName));
        }

        if (upperName.Contains("WEAPO", StringComparison.Ordinal))
        {
            return new DaggerfallCifRciFile(name, ParseWeaponCif(bytes, name, upperName));
        }

        return new DaggerfallCifRciFile(name, ParseMultiImageCif(bytes, name));
    }

    /// <summary>
    ///     The RCI dimension table, ported verbatim from the reference. RCI files carry NO header
    ///     at all, so these per-filename sizes are the only source of geometry.
    /// </summary>
    private static (int Width, int Height)? RciDimensions(string upperName)
    {
        return upperName switch
        {
            "FACES.CIF" or "CHLD00I0.RCI" or "TFAC00I0.RCI" => (64, 64),
            "BUTTONS.RCI" => (32, 16),
            "MPOP.RCI" => (17, 17),
            "NOTE.RCI" => (44, 9),
            "SPOP.RCI" => (22, 22),
            _ => null
        };
    }

    /// <summary>
    ///     Headerless RCI: file length divided by the table's frame size gives the record count
    ///     (61 for retail FACES.CIF, 503 for TFAC00I0.RCI); any remainder is trailing junk and
    ///     ignored. Every record is one raw width*height frame with no offsets.
    /// </summary>
    private static List<DaggerfallCifRciRecord> ParseRci(ReadOnlySpan<byte> bytes, string name, string upperName)
    {
        var dimensions = RciDimensions(upperName)
            ?? throw new NotSupportedException(
                $"'{name}' is not in the RCI dimension table; RCI files are headerless and " +
                "cannot be decoded without a known size.");

        var (width, height) = dimensions;
        var frameLength = width * height;
        var count = bytes.Length / frameLength;
        if (count == 0)
        {
            throw new InvalidDataException(
                $"'{name}' is smaller than one {width}x{height} RCI record ({bytes.Length} bytes).");
        }

        var records = new List<DaggerfallCifRciRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var pixels = bytes.Slice(i * frameLength, frameLength).ToArray();
            var frame = new IndexedBitmap(width, height, pixels);
            records.Add(new DaggerfallCifRciRecord(i, 0, 0, CompressionUncompressed, [frame]));
        }

        return records;
    }

    /// <summary>Plain CIF: IMG-style records walked back to back until end of file.</summary>
    private static List<DaggerfallCifRciRecord> ParseMultiImageCif(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length == 0)
        {
            throw new InvalidDataException($"'{name}' is empty.");
        }

        var records = new List<DaggerfallCifRciRecord>();
        var position = 0;
        while (position < bytes.Length)
        {
            records.Add(ReadImgRecord(bytes, name, records.Count, ref position));
        }

        return records;
    }

    /// <summary>
    ///     Weapon CIF: an IMG wielding record (except WEAPON09.CIF, the bow, which has none),
    ///     then multi-frame animation records to end of file.
    /// </summary>
    private static List<DaggerfallCifRciRecord> ParseWeaponCif(ReadOnlySpan<byte> bytes, string name, string upperName)
    {
        if (bytes.Length == 0)
        {
            throw new InvalidDataException($"'{name}' is empty.");
        }

        var records = new List<DaggerfallCifRciRecord>();
        var position = 0;
        if (upperName != "WEAPON09.CIF")
        {
            records.Add(ReadImgRecord(bytes, name, 0, ref position));
        }

        while (position < bytes.Length)
        {
            records.Add(ReadWeaponAnimationRecord(bytes, name, records.Count, ref position));
        }

        return records;
    }

    /// <summary>
    ///     One IMG-style record: 12-byte header, then pixel data. The walk advances by
    ///     12 + pixel-data length regardless of compression, which is what keeps consecutive
    ///     records aligned (an RLE record's data length is its COMPRESSED size).
    /// </summary>
    private static DaggerfallCifRciRecord ReadImgRecord(ReadOnlySpan<byte> bytes, string name, int index, ref int position)
    {
        if (position + ImgHeaderLength > bytes.Length)
        {
            throw new InvalidDataException($"'{name}' ends inside record {index}'s IMG header.");
        }

        var header = bytes[position..];
        int offsetX = BinaryPrimitives.ReadInt16LittleEndian(header);
        int offsetY = BinaryPrimitives.ReadInt16LittleEndian(header[2..]);
        int width = BinaryPrimitives.ReadInt16LittleEndian(header[4..]);
        int height = BinaryPrimitives.ReadInt16LittleEndian(header[6..]);
        var compression = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        int pixelDataLength = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        var dataPosition = position + ImgHeaderLength;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException(
                $"'{name}' record {index} declares empty geometry ({width}x{height}).");
        }

        var pixelCount = width * height;
        byte[] pixels;
        switch (compression)
        {
            case CompressionUncompressed:
                // Retail data always has pixelDataLength == width*height for raw records; fewer
                // data bytes than pixels would make the walk read into the next record.
                if (pixelCount > pixelDataLength)
                {
                    throw new InvalidDataException(
                        $"'{name}' record {index} declares {pixelDataLength} data bytes for " +
                        $"{width}x{height} = {pixelCount} pixels.");
                }

                if (dataPosition + pixelCount > bytes.Length)
                {
                    throw new InvalidDataException(
                        $"'{name}' record {index}'s pixel data runs past end of file.");
                }

                pixels = bytes.Slice(dataPosition, pixelCount).ToArray();
                break;

            case CompressionRleCompressed:
                pixels = new byte[pixelCount];
                DecodeRle(bytes, name, $"record {index}", dataPosition, pixels);
                break;

            default:
                throw new InvalidDataException(
                    $"'{name}' record {index} has unknown compression 0x{compression:X4} " +
                    "(CIF records are 0x0000 raw or 0x0002 RLE).");
        }

        position = dataPosition + pixelDataLength;
        var frame = new IndexedBitmap(width, height, pixels, offsetX, offsetY);
        return new DaggerfallCifRciRecord(index, offsetX, offsetY, compression, [frame]);
    }

    /// <summary>
    ///     One weapon animation record. Frame offsets are relative to the record START (the first
    ///     frame's offset is 76, the header length, on every retail animation), nonzero offsets
    ///     mark live frames, and each frame RLE-decodes to width*height. The record's total-size
    ///     word — not the frame data — advances the walk.
    /// </summary>
    private static DaggerfallCifRciRecord ReadWeaponAnimationRecord(
        ReadOnlySpan<byte> bytes,
        string name,
        int index,
        ref int position)
    {
        var start = position;
        if (start + WeaponAnimationHeaderLength > bytes.Length)
        {
            throw new InvalidDataException($"'{name}' ends inside record {index}'s animation header.");
        }

        var header = bytes[start..];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(header);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        // header[4..6] last-frame width, header[6..8] i16 x-offset, header[8..10] i16 last-frame
        // y-offset, header[10..12] i16 data length: draw-time metadata the reference reads but
        // never uses to decode; frames all decode at width*height.

        var frames = new List<IndexedBitmap>();
        for (var slot = 0; slot < WeaponFrameOffsetSlots; slot++)
        {
            int frameOffset = BinaryPrimitives.ReadUInt16LittleEndian(
                header[(WeaponAnimationFixedFieldsLength + (slot * 2))..]);
            if (frameOffset == 0)
            {
                continue;
            }

            if (width == 0 || height == 0)
            {
                throw new InvalidDataException(
                    $"'{name}' record {index} declares frames with empty geometry ({width}x{height}).");
            }

            var pixels = new byte[width * height];
            DecodeRle(bytes, name, $"record {index} frame {frames.Count}", start + frameOffset, pixels);
            frames.Add(new IndexedBitmap(width, height, pixels));
        }

        int totalSize = BinaryPrimitives.ReadUInt16LittleEndian(
            header[(WeaponAnimationFixedFieldsLength + (WeaponFrameOffsetSlots * 2))..]);
        if (totalSize < WeaponAnimationHeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}' record {index} declares total size {totalSize}, smaller than its own " +
                $"{WeaponAnimationHeaderLength}-byte header — the walk would never advance.");
        }

        position = start + totalSize;
        return new DaggerfallCifRciRecord(
            index, 0, 0, CompressionRleCompressed, frames, IsWeaponAnimation: true);
    }

    /// <summary>
    ///     The byte-code RLE shared by 0x0002 CIF records and weapon frames: code &gt; 127
    ///     repeats the next byte (code - 127) times; code &lt;= 127 copies (code + 1) literal
    ///     bytes. Decoding stops when the frame is full; a run that would overflow it is an
    ///     error (retail streams land exactly, verified across all 577 RLE images).
    /// </summary>
    private static void DecodeRle(ReadOnlySpan<byte> bytes, string name, string context, int source, Span<byte> destination)
    {
        var written = 0;
        while (written < destination.Length)
        {
            if (source >= bytes.Length)
            {
                throw new InvalidDataException($"'{name}' {context}: RLE stream is truncated.");
            }

            int code = bytes[source++];
            if (code > 127)
            {
                if (source >= bytes.Length)
                {
                    throw new InvalidDataException($"'{name}' {context}: RLE repeat run is truncated.");
                }

                var value = bytes[source++];
                var run = code - 127;
                if (written + run > destination.Length)
                {
                    throw new InvalidDataException(
                        $"'{name}' {context}: RLE repeat run overflows the {destination.Length}-pixel frame.");
                }

                destination.Slice(written, run).Fill(value);
                written += run;
            }
            else
            {
                var run = code + 1;
                if (source + run > bytes.Length)
                {
                    throw new InvalidDataException($"'{name}' {context}: RLE literal run is truncated.");
                }

                if (written + run > destination.Length)
                {
                    throw new InvalidDataException(
                        $"'{name}' {context}: RLE literal run overflows the {destination.Length}-pixel frame.");
                }

                bytes.Slice(source, run).CopyTo(destination[written..]);
                source += run;
                written += run;
            }
        }
    }
}

/// <summary>
///     One CIF/RCI record: its index, draw offsets (always 0 for RCI and weapon-animation
///     records, matching the reference's GetOffset), the compression actually applied to its
///     pixel data, and the decoded frames — one for plain/RCI records, up to 31 for a weapon
///     animation.
/// </summary>
internal sealed record DaggerfallCifRciRecord(
    int Index,
    int OffsetX,
    int OffsetY,
    ushort Compression,
    IReadOnlyList<IndexedBitmap> Frames,
    bool IsWeaponAnimation = false);
