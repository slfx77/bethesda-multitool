// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/IMGFile.cpp / IMGFile.h (header parsing, raw-dimension override
//   table, wall-texture handling, embedded palette) and SETFile.cpp / SETFile.h (.SET tile
//   splitting). License texts are collected centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using BethesdaMultitool.Core.Compression;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     Result of decoding an Arena .IMG or .SET file: the decoded image(s) plus the embedded
///     palette when the file carries one (flag bit 0x0100).
/// </summary>
internal sealed class ArenaImgDecodeResult
{
    public ArenaImgDecodeResult(IReadOnlyList<IndexedBitmap> images, Palette? embeddedPalette)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            throw new ArgumentException("At least one image is required.", nameof(images));
        }

        Images = images;
        EmbeddedPalette = embeddedPalette;
    }

    /// <summary>
    ///     Decoded images: exactly one entry for a .IMG; one 64x64 tile per 4096-byte chunk,
    ///     in file order, for a .SET (mirroring OpenTESArena's SETFile, which models a .SET as
    ///     a list of tiles rather than one tall bitmap).
    /// </summary>
    public IReadOnlyList<IndexedBitmap> Images { get; }

    /// <summary>The first (for plain .IMGs, the only) image.</summary>
    public IndexedBitmap Image => Images[0];

    /// <summary>
    ///     The 768-byte 6-bit palette stored after the pixel data when header flag 0x0100 is
    ///     set, promoted to 8-bit with index 0 transparent; null when the file has none.
    /// </summary>
    public Palette? EmbeddedPalette { get; }
}

/// <summary>
///     Decodes Arena .IMG images and .SET wall-tile packs. Most .IMGs carry a 12-byte LE
///     header (x u16, y u16, width u16, height u16, flags u16, dataLen u16); the flags low
///     byte selects the compression (0 raw, 2 RLE, 4 LZSS, 8 LZHUF) and bit 0x0100 marks a
///     768-byte 6-bit palette after the pixel data. Headerless variants: a hardcoded
///     filename table of raw images, any 4096-byte file as a raw 64x64 wall texture, and
///     .SET files as concatenated raw 64x64 tiles. The reference additionally returns a 1x1
///     dummy for two misspelled filenames its .INFs reference but that never exist on disk
///     (IMGFile.cpp MisspelledIMGs); that path is deliberately not ported — callers here
///     always supply real file bytes.
/// </summary>
internal static class ArenaImgDecoder
{
    private const int HeaderSize = 12;
    private const int WallDimension = 64;
    private const int WallByteCount = WallDimension * WallDimension;

    /// <summary>
    ///     Headerless .IMGs with hardcoded dimensions, ported verbatim from OpenTESArena
    ///     IMGFile.cpp (the RawImgOverride table).
    /// </summary>
    private static readonly Dictionary<string, (int Width, int Height)> RawImgOverride =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ARENARW.IMG"] = (16, 16),
            ["CITY.IMG"] = (16, 11),
            ["DITHER.IMG"] = (8, 100),
            ["DITHER2.IMG"] = (8, 100),
            ["DUNGEON.IMG"] = (14, 8),
            ["DZTTAV.IMG"] = (32, 34),
            ["NOCAMP.IMG"] = (25, 19),
            ["NOSPELL.IMG"] = (25, 19),
            ["P1.IMG"] = (320, 53),
            ["POPTALK.IMG"] = (320, 77),
            ["S2.IMG"] = (320, 36),
            ["SLIDER.IMG"] = (289, 7),
            ["TOWN.IMG"] = (9, 10),
            ["UPDOWN.IMG"] = (8, 16),
            ["VILLAGE.IMG"] = (8, 8),
        };

    /// <summary>
    ///     Decode an Arena .IMG or .SET. <paramref name="fileName" /> (any path prefix is
    ///     stripped, comparisons are case-insensitive) selects .SET handling and the
    ///     headerless-dimension table.
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown when the file is truncated or malformed.</exception>
    public static ArenaImgDecodeResult Decode(ReadOnlySpan<byte> file, string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var name = Path.GetFileName(fileName);

        if (name.EndsWith(".SET", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeSet(file, name);
        }

        if (RawImgOverride.TryGetValue(name, out var dims))
        {
            return DecodeHeaderless(file, name, dims.Width, dims.Height);
        }

        if (file.Length == WallByteCount)
        {
            // Wall texture: raw 64x64 with no header. The size check comes before header
            // parsing because some walls start with rows of index 0 that would read as a
            // zeroed header (IMGFile.cpp handles 4096-byte files the same way).
            return Single(new IndexedBitmap(WallDimension, WallDimension, file.ToArray()));
        }

        if (file.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"IMG \"{name}\" is too small for its {HeaderSize}-byte header ({file.Length} bytes).");
        }

        var xOffset = BinaryPrimitives.ReadUInt16LittleEndian(file);
        var yOffset = BinaryPrimitives.ReadUInt16LittleEndian(file[2..]);
        var width = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(file[6..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(file[8..]);
        var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(file[10..]);

        var indices = DecodePixelData(file, HeaderSize, flags & 0x00FF, dataLength, width * height, name);

        Palette? palette = null;
        if ((flags & 0x0100) != 0)
        {
            palette = ReadEmbeddedPalette(file, HeaderSize + dataLength, name);
        }

        return new ArenaImgDecodeResult(
            [new IndexedBitmap(width, height, indices, xOffset, yOffset)], palette);
    }

    /// <summary>
    ///     Decode one IMG/CIF-style pixel payload at <paramref name="dataOffset" /> (just past
    ///     a 12-byte header) per the compression type in the header flags' low byte. Shared
    ///     with <see cref="ArenaCifDecoder" />, whose frames use the identical layout.
    ///     Type 02 (RLE) is accepted here although OpenTESArena's IMGFile treats it as
    ///     unrecognized — only its CIF frames use type 02; the layout is the same
    ///     (CIFFile.cpp), and the reference RLE decode takes no end bound, so
    ///     <paramref name="dataLength" /> only matters to the caller's frame advance.
    /// </summary>
    internal static byte[] DecodePixelData(
        ReadOnlySpan<byte> file, int dataOffset, int compression, int dataLength, int pixelCount, string name)
    {
        switch (compression)
        {
            case 0x00:
                // Uncompressed: exactly pixelCount bytes (the reference copies width * height,
                // not dataLength, for .IMGs).
                if (dataOffset + pixelCount > file.Length)
                {
                    throw new InvalidDataException(
                        $"\"{name}\": uncompressed pixel data at offset {dataOffset} is truncated " +
                        $"({pixelCount} bytes needed, {file.Length - dataOffset} remain).");
                }

                return file.Slice(dataOffset, pixelCount).ToArray();

            case 0x02:
                return RleCodec.Decompress(file[dataOffset..], pixelCount);

            case 0x04:
                if (dataOffset + dataLength > file.Length)
                {
                    throw new InvalidDataException(
                        $"\"{name}\": type-04 compressed data at offset {dataOffset} is truncated " +
                        $"({dataLength} bytes declared, {file.Length - dataOffset} remain).");
                }

                return LzssCodec.Decompress(file.Slice(dataOffset, dataLength), pixelCount);

            case 0x08:
                // A u16 decompressed length (should equal width * height) sits first and is
                // counted inside dataLength; the reference skips it without validating.
                if (dataLength < 2 || dataOffset + dataLength > file.Length)
                {
                    throw new InvalidDataException(
                        $"\"{name}\": type-08 compressed data at offset {dataOffset} is truncated " +
                        $"({dataLength} bytes declared, {file.Length - dataOffset} remain).");
                }

                return LzhufCodec.Decompress(file.Slice(dataOffset + 2, dataLength - 2), pixelCount);

            default:
                throw new InvalidDataException(
                    $"\"{name}\": unrecognized compression type 0x{compression:X2}.");
        }
    }

    /// <summary>
    ///     Read the 768-byte embedded palette that follows the pixel data. Each 6-bit
    ///     component is clamped to 0x3F (as the reference does) before promotion through the
    ///     shared VGA seam, and index 0 is made transparent per the reference's readPalette.
    ///     Note the reference scales with <c>v * 255 / 63</c> while <see cref="Palette.FromVga6Bit" />
    ///     uses <c>(v &lt;&lt; 2) | (v &gt;&gt; 4)</c>; the two agree at the range endpoints and
    ///     differ by at most 1 mid-range.
    /// </summary>
    private static Palette ReadEmbeddedPalette(ReadOnlySpan<byte> file, int offset, string name)
    {
        if (offset + Palette.RgbByteCount > file.Length)
        {
            throw new InvalidDataException(
                $"\"{name}\": embedded palette at offset {offset} is truncated " +
                $"({Palette.RgbByteCount} bytes needed, {file.Length - offset} remain).");
        }

        var source = file.Slice(offset, Palette.RgbByteCount);
        var clamped = new byte[Palette.RgbByteCount];
        for (var i = 0; i < clamped.Length; i++)
        {
            clamped[i] = Math.Min(source[i], (byte)0x3F);
        }

        return Palette.FromVga6Bit(clamped).WithTransparentIndex(0);
    }

    /// <summary>Decode a headerless .IMG from the raw-dimension table.</summary>
    private static ArenaImgDecodeResult DecodeHeaderless(ReadOnlySpan<byte> file, string name, int width, int height)
    {
        var pixelCount = width * height;
        if (file.Length < pixelCount)
        {
            throw new InvalidDataException(
                $"Headerless IMG \"{name}\" is truncated: {pixelCount} bytes needed for {width}x{height}, got {file.Length}.");
        }

        if (string.Equals(name, "DZTTAV.IMG", StringComparison.OrdinalIgnoreCase))
        {
            // The game expects a 64x64 texture: place the 32x34 payload at destination X + 32
            // so it lines up with DZTTEP.IMG; the rest of the canvas stays index 0.
            var canvas = new byte[WallByteCount];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    canvas[x + 32 + (y * WallDimension)] = file[x + (y * width)];
                }
            }

            return Single(new IndexedBitmap(WallDimension, WallDimension, canvas));
        }

        return Single(new IndexedBitmap(width, height, file[..pixelCount].ToArray()));
    }

    /// <summary>
    ///     Decode a .SET: concatenated raw 64x64 tiles, one per 4096-byte chunk. A partial
    ///     trailing chunk is discarded (integer division), faithful to SETFile.cpp. TBS2.SET
    ///     ships one byte short of a chunk boundary, so a dummy zero byte is appended for it.
    /// </summary>
    private static ArenaImgDecodeResult DecodeSet(ReadOnlySpan<byte> file, string name)
    {
        var data = file;
        if (string.Equals(name, "TBS2.SET", StringComparison.OrdinalIgnoreCase))
        {
            var padded = new byte[file.Length + 1];
            file.CopyTo(padded);
            data = padded;
        }

        var chunkCount = data.Length / WallByteCount;
        if (chunkCount == 0)
        {
            throw new InvalidDataException(
                $"SET \"{name}\" is smaller than one {WallByteCount}-byte tile ({data.Length} bytes).");
        }

        var tiles = new IndexedBitmap[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            tiles[i] = new IndexedBitmap(
                WallDimension, WallDimension, data.Slice(i * WallByteCount, WallByteCount).ToArray());
        }

        return new ArenaImgDecodeResult(tiles, null);
    }

    private static ArenaImgDecodeResult Single(IndexedBitmap image)
    {
        return new ArenaImgDecodeResult([image], null);
    }
}
