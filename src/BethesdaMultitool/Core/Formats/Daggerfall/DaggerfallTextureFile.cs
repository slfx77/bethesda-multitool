// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/TextureFile.cs and the
//   CompressionFormats enum in BaseImageFile.cs. License texts are collected centrally in
//   THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Xngine;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     A Daggerfall <c>TEXTURE.nnn</c> archive — the game's packaged sprite/texture sets (472 of
///     them in a retail ARENA2). A 26-byte header names the set, fixed 20-byte record headers
///     point at 28-byte record descriptors, and each record holds one or more indexed frames.
///     <para>
///         Three storage forms. A single-frame uncompressed record stores its rows on a fixed
///         256-byte stride regardless of width. A multi-frame record stores per-frame
///         transparent-run/pixel-run streams behind an offset table. An RLE record stores per-row
///         headers whose flag word selects raw or run-length rows.
///     </para>
///     <para>
///         Two files are not textures at all: TEXTURE.000 and .001 are palettes of 32x32 solid
///         swatches (record N is colour N, or 128+N for .001), generated rather than stored.
///         TEXTURE.215, .217 and .436 are malformed in the retail data and refused by name, as in
///         the reference.
///     </para>
///     <para>
///         Two ways in. <see cref="Parse" /> decodes every record and frame, as the legacy sprite and
///         texture paths need. <see cref="ReadHeaders" /> reads the file header, the record headers and
///         the record descriptors and nothing else, the cut-1c inspection path (plan D9), which must
///         not decode a pixel, and derives each record's byte range from those headers alone.
///     </para>
/// </summary>
internal sealed class DaggerfallTextureFile
{
    /// <summary>Bytes in the file header (record count + name).</summary>
    private const int HeaderLength = 26;

    /// <summary>Bytes in one record header.</summary>
    private const int RecordHeaderLength = 20;

    /// <summary>Bytes in one record descriptor.</summary>
    private const int RecordDescriptorLength = 28;

    /// <summary>Row stride of single-frame uncompressed records, regardless of width.</summary>
    public const int UncompressedRowStride = 256;

    /// <summary>Edge length of the generated solid swatches in TEXTURE.000/.001.</summary>
    public const int SolidSize = 32;

    /// <summary>Row-header flag marking an RLE-encoded row.</summary>
    private const ushort RowIsRle = 0x8000;

    /// <summary>Compression forms, from the reference's enum.</summary>
    private const ushort CompressionRecordRle = 0x1108;

    private const ushort CompressionImageRle = 0x0108;

    /// <summary>Files the reference refuses: malformed in the retail data.</summary>
    private static readonly string[] UnsupportedNames = ["TEXTURE.215", "TEXTURE.217", "TEXTURE.436"];

    private DaggerfallTextureFile(string name, string setName, IReadOnlyList<DaggerfallTextureRecord> records)
    {
        Name = name;
        SetName = setName;
        Records = records;
    }

    /// <summary>Logical file name (e.g. <c>TEXTURE.010</c>).</summary>
    public string Name { get; }

    /// <summary>The set's display name from the header (e.g. "Desert Castle").</summary>
    public string SetName { get; }

    /// <summary>The decoded records, each with its frames.</summary>
    public IReadOnlyList<DaggerfallTextureRecord> Records { get; }

    /// <summary>True when the file is one of the retail archives the reference refuses by name.</summary>
    public static bool IsUnsupported(string fileName)
    {
        return UnsupportedNames.Contains(fileName.ToUpperInvariant());
    }

    /// <summary>Whether a file name follows the TEXTURE.nnn convention.</summary>
    public static bool IsTextureFileName(string fileName)
    {
        return fileName.StartsWith("TEXTURE.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses and decodes every record and frame.</summary>
    public static DaggerfallTextureFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        var upperName = name.ToUpperInvariant();
        if (IsUnsupported(upperName))
        {
            throw new NotSupportedException(
                $"'{name}' is one of the three retail TEXTURE archives with malformed data " +
                "(215/217/436); the reference refuses them and so does this decoder.");
        }

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException($"'{name}' is too small for a TEXTURE header ({bytes.Length} bytes).");
        }

        int recordCount = BinaryPrimitives.ReadInt16LittleEndian(bytes);
        if (recordCount <= 0)
        {
            throw new InvalidDataException($"'{name}' declares {recordCount} records.");
        }

        var setName = ReadCString(bytes.Slice(2, HeaderLength - 2));

        // TEXTURE.000/.001 hold solid colour swatches: descriptors exist but the pixels are
        // generated, one 32x32 fill per record.
        var solidBase = SolidBaseOf(upperName);

        var records = new List<DaggerfallTextureRecord>(recordCount);
        for (var r = 0; r < recordCount; r++)
        {
            var header = ReadRecordHeader(bytes, name, r, solidBase);
            var recordPosition = header.DescriptorOffset;
            var offsetX = header.OffsetX;
            var offsetY = header.OffsetY;
            var width = header.Width;
            var height = header.Height;
            var compression = header.Compression;
            var dataOffset = header.DataOffset;
            var frameCount = header.FrameCount;

            List<IndexedBitmap> frames;
            ByteArea? consumed = null;
            if (solidBase >= 0)
            {
                var pixels = new byte[SolidSize * SolidSize];
                Array.Fill(pixels, (byte)(solidBase + r));
                frames = [new IndexedBitmap(SolidSize, SolidSize, pixels, offsetX, offsetY)];
            }
            else if (frameCount <= 0)
            {
                // A handful of retail records are authored empty (e.g. TEXTURE.081 record 4 is
                // 6x0 with no frames) — placeholders, not corruption. They keep their slot with
                // zero frames so record indices stay aligned with the file.
                frames = [];
            }
            else
            {
                if (width <= 0 || height <= 0)
                {
                    throw new InvalidDataException(
                        $"'{name}' record {r} declares {frameCount} frame(s) with empty geometry ({width}x{height}).");
                }

                var dataStart = recordPosition + dataOffset;
                if (dataStart >= bytes.Length)
                {
                    throw new InvalidDataException($"'{name}' record {r}'s data offset is outside the file.");
                }

                int consumedEnd;
                frames = compression is CompressionRecordRle or CompressionImageRle
                    ? DecodeRleRecord(bytes, name, r, recordPosition, (int)dataStart, width, height, frameCount,
                        offsetX, offsetY, out consumedEnd)
                    : DecodeUncompressedRecord(bytes, name, r, (int)dataStart, width, height, frameCount, offsetX,
                        offsetY, out consumedEnd);
                consumed = new ByteArea($"record:{r}", (int)dataStart, consumedEnd);
            }

            records.Add(new DaggerfallTextureRecord(r, offsetX, offsetY, compression, frames)
            {
                Header = header,
                ConsumedRange = consumed
            });
        }

        return new DaggerfallTextureFile(name, setName, records);
    }

    /// <summary>
    ///     Reads the file header, every record header and every record descriptor, decoding nothing:
    ///     no row, run, frame table or pixel byte is touched, so the read costs the same on a
    ///     4,104-byte set and a 983,853-byte one. Each record's byte range comes from its headers
    ///     (<see cref="DaggerfallTextureRecordHeader.DeclaredEnd" />).
    ///     <para>
    ///         Unlike <see cref="Parse" /> this does not refuse TEXTURE.215, .217 and .436 by NAME. The
    ///         refusal is Daggerfall's: its 215 and 217 are 46-byte stubs whose only record points past
    ///         the end of the file, which the structural checks here reject the same way, and its 436
    ///         holds five authored-empty records, which read fine. Redguard's 3dart sets reuse the
    ///         numbering with real textures in 215 ("hideout 1"), 217 ("whitewash wood") and 436
    ///         ("coyle"), referenced by 1,059 of its planes (slice-2 measurement M-F), so a by-name
    ///         refusal here would blind a Redguard reader. <see cref="Parse" /> keeps the refusal, and
    ///         its callers' behavior, unchanged.
    ///     </para>
    /// </summary>
    /// <exception cref="InvalidDataException">The header, a record header or a descriptor does not fit, or declares frames with empty geometry.</exception>
    public static DaggerfallTextureFileHeaders ReadHeaders(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException($"'{name}' is too small for a TEXTURE header ({bytes.Length} bytes).");
        }

        int recordCount = BinaryPrimitives.ReadInt16LittleEndian(bytes);
        if (recordCount <= 0)
        {
            throw new InvalidDataException($"'{name}' declares {recordCount} records.");
        }

        var setName = ReadCString(bytes.Slice(2, HeaderLength - 2));
        var solidBase = SolidBaseOf(name.ToUpperInvariant());
        var records = new DaggerfallTextureRecordHeader[recordCount];
        for (var r = 0; r < recordCount; r++)
        {
            records[r] = ReadRecordHeader(bytes, name, r, solidBase);
            var header = records[r];
            if (header.Form is not (DaggerfallTextureRecordForm.Solid or DaggerfallTextureRecordForm.Empty) &&
                (header.Width <= 0 || header.Height <= 0))
            {
                throw new InvalidDataException(
                    $"'{name}' record {r} declares {header.FrameCount} frame(s) with empty geometry ({header.Width}x{header.Height}).");
            }
        }

        return new DaggerfallTextureFileHeaders(name, setName, solidBase >= 0 ? solidBase : null, bytes.Length, records);
    }

    /// <summary>The generated swatch base of TEXTURE.000 (0) and .001 (128), or -1 for a stored set.</summary>
    private static int SolidBaseOf(string upperName)
    {
        return upperName switch
        {
            "TEXTURE.000" => 0,
            "TEXTURE.001" => 128,
            _ => -1
        };
    }

    /// <summary>
    ///     Reads record <paramref name="r" />'s 20-byte header and the 28-byte descriptor it points at,
    ///     with the structural checks <see cref="Parse" /> has always made.
    /// </summary>
    private static DaggerfallTextureRecordHeader ReadRecordHeader(ReadOnlySpan<byte> bytes, string name, int r, int solidBase)
    {
        var headerOffset = HeaderLength + r * RecordHeaderLength;
        if (headerOffset + RecordHeaderLength > bytes.Length)
        {
            throw new InvalidDataException($"'{name}' ends inside record header {r}.");
        }

        var type1 = BinaryPrimitives.ReadInt16LittleEndian(bytes[headerOffset..]);
        var recordPosition = BinaryPrimitives.ReadInt32LittleEndian(bytes[(headerOffset + 2)..]);
        var type2 = BinaryPrimitives.ReadInt16LittleEndian(bytes[(headerOffset + 6)..]);
        var headerUnknown = BinaryPrimitives.ReadInt32LittleEndian(bytes[(headerOffset + 8)..]);
        if (recordPosition < 0 || recordPosition + RecordDescriptorLength > bytes.Length)
        {
            throw new InvalidDataException($"'{name}' record {r} points outside the file ({recordPosition}).");
        }

        var d = bytes[recordPosition..];
        var offsetX = BinaryPrimitives.ReadInt16LittleEndian(d);
        var offsetY = BinaryPrimitives.ReadInt16LittleEndian(d[2..]);
        int width = BinaryPrimitives.ReadInt16LittleEndian(d[4..]);
        int height = BinaryPrimitives.ReadInt16LittleEndian(d[6..]);
        var compression = (ushort)BinaryPrimitives.ReadInt16LittleEndian(d[8..]);
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(d[10..]);
        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(d[14..]);
        var isNormal = BinaryPrimitives.ReadInt16LittleEndian(d[18..]);
        int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(d[20..]);
        var unknown = BinaryPrimitives.ReadInt16LittleEndian(d[22..]);
        var scaleX = BinaryPrimitives.ReadInt16LittleEndian(d[24..]);
        var scaleY = BinaryPrimitives.ReadInt16LittleEndian(d[26..]);

        var form = FormOf(solidBase, frameCount, compression);

        return new DaggerfallTextureRecordHeader(
            r,
            recordPosition,
            offsetX,
            offsetY,
            width,
            height,
            compression,
            declaredSize,
            dataOffset,
            isNormal,
            frameCount,
            unknown,
            scaleX,
            scaleY,
            type1,
            type2,
            headerUnknown,
            form,
            solidBase >= 0 ? solidBase + r : null);
    }

    /// <summary>The storage form the record's headers declare, in the order <see cref="Parse" /> branches on them.</summary>
    private static DaggerfallTextureRecordForm FormOf(int solidBase, int frameCount, ushort compression)
    {
        if (solidBase >= 0)
        {
            return DaggerfallTextureRecordForm.Solid;
        }

        if (frameCount <= 0)
        {
            return DaggerfallTextureRecordForm.Empty;
        }

        if (compression is CompressionRecordRle or CompressionImageRle)
        {
            return DaggerfallTextureRecordForm.Rle;
        }

        return frameCount == 1 ? DaggerfallTextureRecordForm.SingleFrame : DaggerfallTextureRecordForm.MultiFrame;
    }

    /// <summary>
    ///     Uncompressed records. One frame reads rows on the fixed 256-byte stride; several frames
    ///     sit behind an i32 offset table, each a stream of alternating transparent-run and
    ///     pixel-run counts per row (a skip writes index 0). <paramref name="consumedEnd" /> is one
    ///     past the last source byte the decode read.
    /// </summary>
    private static List<IndexedBitmap> DecodeUncompressedRecord(
        ReadOnlySpan<byte> bytes,
        string name,
        int record,
        int dataStart,
        int width,
        int height,
        int frameCount,
        int offsetX,
        int offsetY,
        out int consumedEnd)
    {
        var frames = new List<IndexedBitmap>(frameCount);
        consumedEnd = dataStart;

        if (frameCount == 1)
        {
            var pixels = new byte[width * height];
            var source = dataStart;
            for (var y = 0; y < height; y++)
            {
                if (source + width > bytes.Length)
                {
                    throw new InvalidDataException($"'{name}' record {record} row {y} runs past end of file.");
                }

                bytes.Slice(source, width).CopyTo(pixels.AsSpan(y * width));
                consumedEnd = Math.Max(consumedEnd, source + width);
                source += UncompressedRowStride;
            }

            frames.Add(new IndexedBitmap(width, height, pixels, offsetX, offsetY));
            return frames;
        }

        for (var frame = 0; frame < frameCount; frame++)
        {
            var tableEntry = dataStart + frame * 4;
            if (tableEntry + 4 > bytes.Length)
            {
                throw new InvalidDataException($"'{name}' record {record} frame table is truncated.");
            }

            var frameStart = dataStart + BinaryPrimitives.ReadInt32LittleEndian(bytes[tableEntry..]);
            if (frameStart < 0 || frameStart + 4 > bytes.Length)
            {
                throw new InvalidDataException($"'{name}' record {record} frame {frame} points outside the file.");
            }

            int cx = BinaryPrimitives.ReadInt16LittleEndian(bytes[frameStart..]);
            int cy = BinaryPrimitives.ReadInt16LittleEndian(bytes[(frameStart + 2)..]);
            if (cx <= 0 || cy <= 0 || cx > width || cy > height)
            {
                throw new InvalidDataException(
                    $"'{name}' record {record} frame {frame} declares {cx}x{cy} inside a {width}x{height} record.");
            }

            var pixels = new byte[width * height];
            var source = frameStart + 4;
            var destination = 0;
            for (var y = 0; y < cy; y++)
            {
                var x = 0;
                while (x < cx)
                {
                    if (source + 2 > bytes.Length)
                    {
                        throw new InvalidDataException(
                            $"'{name}' record {record} frame {frame} ends mid-row at y={y}.");
                    }

                    // A transparent run (writes index 0), then a literal run.
                    int skip = bytes[source++];
                    for (var i = 0; i < skip && x < cx; i++, x++)
                    {
                        pixels[destination++] = 0;
                    }

                    int literal = bytes[source++];
                    if (source + literal > bytes.Length)
                    {
                        throw new InvalidDataException(
                            $"'{name}' record {record} frame {frame} literal run is truncated.");
                    }

                    for (var i = 0; i < literal && x < cx; i++, x++)
                    {
                        pixels[destination++] = bytes[source++];
                    }
                }
            }

            consumedEnd = Math.Max(consumedEnd, source);
            frames.Add(new IndexedBitmap(width, height, pixels, offsetX, offsetY));
        }

        return frames;
    }

    /// <summary>
    ///     RLE records: per frame, <c>height</c> four-byte row headers (an i16 offset from the
    ///     RECORD position — not the data offset — and a flag word). A flagged row is
    ///     <c>u16 rowWidth</c> then signed probes: negative repeats one byte, positive copies that
    ///     many; an unflagged row is <c>width</c> raw bytes. <paramref name="consumedEnd" /> is one
    ///     past the last source byte the decode read, row headers included.
    /// </summary>
    private static List<IndexedBitmap> DecodeRleRecord(
        ReadOnlySpan<byte> bytes,
        string name,
        int record,
        int recordPosition,
        int dataStart,
        int width,
        int height,
        int frameCount,
        int offsetX,
        int offsetY,
        out int consumedEnd)
    {
        var frames = new List<IndexedBitmap>(frameCount);
        consumedEnd = dataStart;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var headerStart = dataStart + height * frame * 4;
            if (headerStart + height * 4 > bytes.Length)
            {
                throw new InvalidDataException($"'{name}' record {record} frame {frame} row headers are truncated.");
            }

            consumedEnd = Math.Max(consumedEnd, headerStart + height * 4);
            var pixels = new byte[width * height];
            var destination = 0;

            for (var y = 0; y < height; y++)
            {
                var rowOffset = BinaryPrimitives.ReadInt16LittleEndian(bytes[(headerStart + y * 4)..]);
                var encoding = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(headerStart + y * 4 + 2)..]);
                var source = recordPosition + rowOffset;
                if (source < 0 || source >= bytes.Length)
                {
                    throw new InvalidDataException($"'{name}' record {record} row {y} points outside the file.");
                }

                if (encoding == RowIsRle)
                {
                    int rowWidth = BinaryPrimitives.ReadUInt16LittleEndian(bytes[source..]);
                    source += 2;
                    var written = 0;
                    while (written < rowWidth)
                    {
                        if (source + 2 > bytes.Length)
                        {
                            throw new InvalidDataException(
                                $"'{name}' record {record} row {y} RLE stream is truncated.");
                        }

                        int probe = BinaryPrimitives.ReadInt16LittleEndian(bytes[source..]);
                        source += 2;
                        if (probe < 0)
                        {
                            var value = bytes[source++];
                            for (var i = 0; i < -probe; i++, written++)
                            {
                                pixels[destination++] = value;
                            }
                        }
                        else if (probe > 0)
                        {
                            if (source + probe > bytes.Length)
                            {
                                throw new InvalidDataException(
                                    $"'{name}' record {record} row {y} literal run is truncated.");
                            }

                            bytes.Slice(source, probe).CopyTo(pixels.AsSpan(destination));
                            source += probe;
                            destination += probe;
                            written += probe;
                        }
                        else
                        {
                            throw new InvalidDataException(
                                $"'{name}' record {record} row {y} has a zero-length RLE probe.");
                        }
                    }

                    consumedEnd = Math.Max(consumedEnd, source);
                }
                else
                {
                    if (source + width > bytes.Length)
                    {
                        throw new InvalidDataException($"'{name}' record {record} raw row {y} runs past the file.");
                    }

                    bytes.Slice(source, width).CopyTo(pixels.AsSpan(destination));
                    destination += width;
                    consumedEnd = Math.Max(consumedEnd, source + width);
                }
            }

            frames.Add(new IndexedBitmap(width, height, pixels, offsetX, offsetY));
        }

        return frames;
    }

    private static string ReadCString(ReadOnlySpan<byte> raw)
    {
        var end = raw.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? raw : raw[..end]).Trim();
    }
}

/// <summary>
///     One TEXTURE record: its index, draw offsets, raw compression word and decoded frames, plus the
///     header-only view it was decoded from and the source byte range the decode consumed.
/// </summary>
internal sealed record DaggerfallTextureRecord(
    int Index,
    int OffsetX,
    int OffsetY,
    ushort Compression,
    IReadOnlyList<IndexedBitmap> Frames)
{
    /// <summary>The record's headers, as <see cref="DaggerfallTextureFile.ReadHeaders" /> would read them.</summary>
    public required DaggerfallTextureRecordHeader Header { get; init; }

    /// <summary>
    ///     The source bytes the decode read, from the data start to one past the last byte touched
    ///     (row headers and frame tables included), named <c>record:{index}</c>; null for a generated
    ///     solid swatch and for an authored-empty record, which read no pixel data. This is the range
    ///     the gate-1c texture oracle calls <c>consumed</c>, and on every retail record it ends where
    ///     <see cref="DaggerfallTextureRecordHeader.DeclaredEnd" /> says (measured 2026-09-28, 6,454 of
    ///     6,454 decodable Daggerfall records). ⚠ For a single-frame record it is a SPAN across the
    ///     256-byte-stride page, so it holds other records' strips too: on 2,760 of Daggerfall's 3,994
    ///     single-frame records another record's data starts inside it. ⚠ On a multi-frame record the end
    ///     is where the legacy decoder stopped reading: a literal run longer than its row advances the
    ///     source only by the pixels the row takes, while the gate-1c oracle (<c>texture_probe.py</c>)
    ///     advances by the whole run, so on such a run the two ends differ. Retail has no such run (0
    ///     literal and 0 skip overshoots over the 5,558 frames of the 1,101 multi-frame ARENA2 records,
    ///     measured 2026-09-28), which is what makes the equality above hold; the synthetic overshoot
    ///     test in <c>DaggerfallTextureFileHeaderTests</c> pins the C# side.
    /// </summary>
    public required ByteArea? ConsumedRange { get; init; }
}

/// <summary>The storage form a TEXTURE record's headers declare.</summary>
internal enum DaggerfallTextureRecordForm
{
    /// <summary>TEXTURE.000/.001: a generated 32x32 swatch; the descriptor holds no usable geometry.</summary>
    Solid,

    /// <summary>Zero frames: an authored-empty placeholder that keeps its record index.</summary>
    Empty,

    /// <summary>One uncompressed frame whose rows sit on the fixed 256-byte stride.</summary>
    SingleFrame,

    /// <summary>Several uncompressed frames behind an i32 offset table, each a transparent-run/literal-run stream.</summary>
    MultiFrame,

    /// <summary>Compression 0x1108 or 0x0108: per-row headers selecting raw or run-length rows.</summary>
    Rle
}

/// <summary>
///     The header-only view of one TEXTURE record: its 20-byte record header, its 28-byte descriptor
///     and the byte range those imply, read without decoding a pixel.
/// </summary>
/// <param name="Index">Position in the record header table.</param>
/// <param name="DescriptorOffset">Where the 28-byte descriptor starts (record header +2).</param>
/// <param name="OffsetX">Descriptor +0: horizontal draw offset.</param>
/// <param name="OffsetY">Descriptor +2: vertical draw offset.</param>
/// <param name="Width">Descriptor +4.</param>
/// <param name="Height">Descriptor +6.</param>
/// <param name="Compression">Descriptor +8, raw.</param>
/// <param name="DeclaredSize">Descriptor +10, raw (see <see cref="DeclaredEnd" /> for what it measures).</param>
/// <param name="DataOffset">Descriptor +14: the pixel data's offset from the descriptor.</param>
/// <param name="IsNormal">Descriptor +18, raw.</param>
/// <param name="FrameCount">Descriptor +20.</param>
/// <param name="Unknown">Descriptor +22, raw.</param>
/// <param name="ScaleX">Descriptor +24, raw.</param>
/// <param name="ScaleY">Descriptor +26, raw.</param>
/// <param name="Type1">Record header +0, raw.</param>
/// <param name="Type2">Record header +6, raw.</param>
/// <param name="HeaderUnknown">Record header +8, raw.</param>
/// <param name="Form">The storage form the headers declare.</param>
/// <param name="SolidIndex">The palette index a solid swatch fills with; null for a stored record.</param>
internal sealed record DaggerfallTextureRecordHeader(
    int Index,
    int DescriptorOffset,
    short OffsetX,
    short OffsetY,
    int Width,
    int Height,
    ushort Compression,
    uint DeclaredSize,
    uint DataOffset,
    short IsNormal,
    int FrameCount,
    short Unknown,
    short ScaleX,
    short ScaleY,
    short Type1,
    short Type2,
    int HeaderUnknown,
    DaggerfallTextureRecordForm Form,
    int? SolidIndex)
{
    /// <summary>Where the record's pixel data starts: the descriptor plus <see cref="DataOffset" />.</summary>
    public long DataStart => DescriptorOffset + DataOffset;

    /// <summary>
    ///     One past the record's last byte as the HEADERS alone imply it, or null for a solid swatch,
    ///     whose descriptor holds no usable geometry. Measured 2026-09-28 over the 469 decodable
    ///     Daggerfall sets against the decode-consumed range:
    ///     <list type="bullet">
    ///         <item>
    ///             Multi-frame and RLE records: <c>DescriptorOffset + DeclaredSize</c>, equal to the
    ///             consumed end on 1,101 of 1,101 and 1,359 of 1,359 (and on 57 of 57 Redguard multi-frame
    ///             records). An empty record's <see cref="DeclaredSize" /> is 28, the descriptor alone
    ///             (4 of 4).
    ///         </item>
    ///         <item>
    ///             Single-frame records: <c>DataStart + (Height - 1) x 256 + Width</c>, the span of the
    ///             256-byte-stride rows, equal to the consumed end on 3,994 of 3,994 (5,573 of 5,573 on
    ///             Redguard). Their <see cref="DeclaredSize" /> is instead <c>28 + Width x Height</c> on
    ///             3,994 of 3,994: the descriptor plus the record's OWN pixels, not the page span.
    ///         </item>
    ///     </list>
    /// </summary>
    public long? DeclaredEnd => Form switch
    {
        DaggerfallTextureRecordForm.Solid => null,
        DaggerfallTextureRecordForm.SingleFrame =>
            DataStart + (Height - 1L) * DaggerfallTextureFile.UncompressedRowStride + Width,
        _ => DescriptorOffset + (long)DeclaredSize
    };
}

/// <summary>
///     A TEXTURE file read by <see cref="DaggerfallTextureFile.ReadHeaders" />: the file header and every
///     record's headers, with nothing decoded.
/// </summary>
/// <param name="Name">Logical file name.</param>
/// <param name="SetName">The set's display name from the header.</param>
/// <param name="SolidBase">0 for TEXTURE.000, 128 for TEXTURE.001, null for a stored set.</param>
/// <param name="Length">The file's length in bytes.</param>
/// <param name="Records">Every record's headers, in record order.</param>
internal sealed record DaggerfallTextureFileHeaders(
    string Name,
    string SetName,
    int? SolidBase,
    int Length,
    IReadOnlyList<DaggerfallTextureRecordHeader> Records);
