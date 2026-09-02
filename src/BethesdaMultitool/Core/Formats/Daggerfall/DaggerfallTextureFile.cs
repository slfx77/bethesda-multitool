// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/TextureFile.cs and the
//   CompressionFormats enum in BaseImageFile.cs. License texts are collected centrally in
//   THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;
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
        var solidBase = upperName switch
        {
            "TEXTURE.000" => 0,
            "TEXTURE.001" => 128,
            _ => -1
        };

        var records = new List<DaggerfallTextureRecord>(recordCount);
        for (var r = 0; r < recordCount; r++)
        {
            var headerOffset = HeaderLength + (r * RecordHeaderLength);
            if (headerOffset + RecordHeaderLength > bytes.Length)
            {
                throw new InvalidDataException($"'{name}' ends inside record header {r}.");
            }

            var recordPosition = BinaryPrimitives.ReadInt32LittleEndian(bytes[(headerOffset + 2)..]);
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
            var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(d[14..]);
            int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(d[20..]);

            List<IndexedBitmap> frames;
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

                var dataStart = recordPosition + (long)dataOffset;
                if (dataStart >= bytes.Length)
                {
                    throw new InvalidDataException($"'{name}' record {r}'s data offset is outside the file.");
                }

                frames = compression is CompressionRecordRle or CompressionImageRle
                    ? DecodeRleRecord(bytes, name, r, recordPosition, (int)dataStart, width, height, frameCount, offsetX, offsetY)
                    : DecodeUncompressedRecord(bytes, name, r, (int)dataStart, width, height, frameCount, offsetX, offsetY);
            }

            records.Add(new DaggerfallTextureRecord(r, offsetX, offsetY, compression, frames));
        }

        return new DaggerfallTextureFile(name, setName, records);
    }

    /// <summary>
    ///     Uncompressed records. One frame reads rows on the fixed 256-byte stride; several frames
    ///     sit behind an i32 offset table, each a stream of alternating transparent-run and
    ///     pixel-run counts per row (a skip writes index 0).
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
        int offsetY)
    {
        var frames = new List<IndexedBitmap>(frameCount);

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
                source += UncompressedRowStride;
            }

            frames.Add(new IndexedBitmap(width, height, pixels, offsetX, offsetY));
            return frames;
        }

        for (var frame = 0; frame < frameCount; frame++)
        {
            var tableEntry = dataStart + (frame * 4);
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

            frames.Add(new IndexedBitmap(width, height, pixels, offsetX, offsetY));
        }

        return frames;
    }

    /// <summary>
    ///     RLE records: per frame, <c>height</c> four-byte row headers (an i16 offset from the
    ///     RECORD position — not the data offset — and a flag word). A flagged row is
    ///     <c>u16 rowWidth</c> then signed probes: negative repeats one byte, positive copies that
    ///     many; an unflagged row is <c>width</c> raw bytes.
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
        int offsetY)
    {
        var frames = new List<IndexedBitmap>(frameCount);

        for (var frame = 0; frame < frameCount; frame++)
        {
            var headerStart = dataStart + (height * frame * 4);
            if (headerStart + (height * 4) > bytes.Length)
            {
                throw new InvalidDataException($"'{name}' record {record} frame {frame} row headers are truncated.");
            }

            var pixels = new byte[width * height];
            var destination = 0;

            for (var y = 0; y < height; y++)
            {
                var rowOffset = BinaryPrimitives.ReadInt16LittleEndian(bytes[(headerStart + (y * 4))..]);
                var encoding = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(headerStart + (y * 4) + 2)..]);
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
                }
                else
                {
                    if (source + width > bytes.Length)
                    {
                        throw new InvalidDataException($"'{name}' record {record} raw row {y} runs past the file.");
                    }

                    bytes.Slice(source, width).CopyTo(pixels.AsSpan(destination));
                    destination += width;
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

/// <summary>One TEXTURE record: its index, draw offsets, raw compression word and decoded frames.</summary>
internal sealed record DaggerfallTextureRecord(
    int Index,
    int OffsetX,
    int OffsetY,
    ushort Compression,
    IReadOnlyList<IndexedBitmap> Frames);
