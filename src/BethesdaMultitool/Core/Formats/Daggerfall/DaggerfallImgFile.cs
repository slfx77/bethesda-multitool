// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/ImgFile.cs plus the
//   shared IMG header layout and RLE codec in BaseImageFile.cs. License texts are collected
//   centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     A Daggerfall <c>.IMG</c> single-image file (263 in a retail ARENA2). Two layouts, chosen
///     exactly as the reference does it:
///     <para>
///         <b>Headered</b> — a 12-byte header (i16 x-offset, i16 y-offset, i16 width, i16 height,
///         u16 compression, u16 pixel-data length) followed by the pixel data. Every retail
///         headered IMG is raw indices with pixel-data length == width*height and file size ==
///         12 + pixel-data length (verified 2026-09-02 across all 191, e.g. BANK00I0.IMG =
///         12 + 40725 bytes, 225x181). The compression word is 0x0000 on 189 of them; FRAM00I0.IMG
///         and TALK00I0.IMG carry 0x0800 — a value absent from the reference's enum — over plainly
///         raw data, which is why the reference (which never consults the word when reading an
///         IMG) decodes them anyway. 0x0002 would mean the byte-code RLE shared with CIF records;
///         no retail IMG uses it.
///     </para>
///     <para>
///         <b>Headerless</b> — raw fullscreen/HUD images with no header at all, recognised by
///         exact FILE SIZE through a 19-entry table ported verbatim from the reference
///         (<see cref="HeaderlessDimensionsBySize" />; e.g. AMAP00I0.IMG = 64,000 bytes =
///         320x200). Headerless images are never compressed and have no draw offsets. The
///         table's 44-byte entry is internally inconsistent in the reference (22x22 = 484
///         pixels cannot come from 44 bytes); no retail IMG has that size and this port throws
///         on it rather than zero-padding.
///     </para>
///     <para>
///         Exactly six IMGs (<see cref="EmbeddedPaletteFileNames" />, all 64,768-byte headerless
///         320x200 fullscreens) append their own palette after the pixel data: 768 bytes of 6-bit
///         VGA RGB triplets (measured 2026-09-02: no component exceeds 63 in any of the six),
///         promoted to 8-bit here. Which file gets one is keyed by NAME, not size — the trailing
///         768 bytes of a 64,768-byte file with any other name are ignored, as in the reference.
///     </para>
///     <para>
///         Three retail names are refused outright: FMAP0I00/FMAP0I01/FMAP0I16.IMG are 12-byte
///         files holding only an all-zero header, no image (the FMAPAI00/BI00/... quadrant files
///         replace them). All other IMGs are lit by an external palette named by
///         <see cref="GetPaletteFileName" />.
///     </para>
/// </summary>
internal sealed class DaggerfallImgFile
{
    /// <summary>Bytes in the IMG header shared with CIF records.</summary>
    private const int HeaderLength = 12;

    /// <summary>Compression word of a byte-code RLE image (defined for the shared header; no retail IMG uses it).</summary>
    private const ushort CompressionRleCompressed = 0x0002;

    /// <summary>
    ///     The headerless dimension table, ported verbatim from the reference's
    ///     <c>GetHeaderlessFileImageDimensions</c>. A file whose total size matches a key has no
    ///     header and these dimensions; every other size is parsed as headered. 64,768 is the
    ///     64,000-pixel fullscreen plus a 768-byte embedded palette.
    /// </summary>
    public static IReadOnlyDictionary<int, (int Width, int Height)> HeaderlessDimensionsBySize { get; } =
        new Dictionary<int, (int Width, int Height)>
        {
            [44] = (22, 22),
            [289] = (17, 17),
            [441] = (49, 9),
            [512] = (32, 16),
            [720] = (9, 80),
            [990] = (45, 22),
            [1720] = (43, 40),
            [2140] = (107, 20),
            [2916] = (81, 36),
            [3200] = (40, 80),
            [3938] = (179, 22),
            [4280] = (107, 40),
            [4508] = (322, 14),
            [20480] = (320, 64),
            [26496] = (184, 144),
            [64000] = (320, 200),
            [64768] = (320, 200),
            [68800] = (320, 215),
            [112128] = (512, 219),
        };

    /// <summary>
    ///     The six IMGs that append their own 6-bit VGA palette after the pixel data, from the
    ///     reference's <c>ReadPalette</c>. All are 64,768-byte headerless 320x200 fullscreens.
    /// </summary>
    public static IReadOnlyList<string> EmbeddedPaletteFileNames { get; } =
    [
        "CHGN00I0.IMG",
        "DIE_00I0.IMG",
        "PICK02I0.IMG",
        "PICK03I0.IMG",
        "PRIS00I0.IMG",
        "TITL00I0.IMG",
    ];

    /// <summary>
    ///     Files the reference refuses: 12-byte all-zero headers with no image data.
    /// </summary>
    public static IReadOnlyList<string> UnsupportedFileNames { get; } =
    [
        "FMAP0I00.IMG",
        "FMAP0I01.IMG",
        "FMAP0I16.IMG",
    ];

    private DaggerfallImgFile(string name, IndexedBitmap bitmap, Palette? embeddedPalette, ushort compression, bool hasHeader)
    {
        Name = name;
        Bitmap = bitmap;
        EmbeddedPalette = embeddedPalette;
        Compression = compression;
        HasHeader = hasHeader;
    }

    /// <summary>Logical file name (e.g. <c>BANK00I0.IMG</c>).</summary>
    public string Name { get; }

    /// <summary>The single decoded image, with the header's draw offsets (0 when headerless).</summary>
    public IndexedBitmap Bitmap { get; }

    /// <summary>
    ///     The promoted embedded palette, present only for the six
    ///     <see cref="EmbeddedPaletteFileNames" />; null for every other IMG, which is lit by the
    ///     external palette <see cref="GetPaletteFileName" /> names.
    /// </summary>
    public Palette? EmbeddedPalette { get; }

    /// <summary>The header's raw compression word (0 for headerless images, which are never compressed).</summary>
    public ushort Compression { get; }

    /// <summary>True when the file carried a 12-byte header; false when sized by the headerless table.</summary>
    public bool HasHeader { get; }

    /// <summary>Whether a file name follows the IMG convention.</summary>
    public static bool IsImgFileName(string fileName)
    {
        return fileName.EndsWith(".IMG", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the file is one of the three retail names with no image data.</summary>
    public static bool IsUnsupported(string fileName)
    {
        return UnsupportedFileNames.Contains(fileName.ToUpperInvariant());
    }

    /// <summary>True when the file is one of the six that append their own palette.</summary>
    public static bool HasEmbeddedPalette(string fileName)
    {
        return EmbeddedPaletteFileNames.Contains(fileName.ToUpperInvariant());
    }

    /// <summary>
    ///     Which external palette file lights this image, from the reference's <c>PaletteName</c>:
    ///     the six self-palettized files return empty, FMAP*-named maps use FMAP_PAL.COL,
    ///     NITE*-named sky panoramas use NIGHTSKY.COL, two one-offs have their own palette, and
    ///     everything else uses ART_PAL.COL.
    /// </summary>
    public static string GetPaletteFileName(string fileName)
    {
        var upperName = fileName.ToUpperInvariant();
        if (EmbeddedPaletteFileNames.Contains(upperName))
        {
            return string.Empty;
        }

        if (upperName.StartsWith("FMAP", StringComparison.Ordinal))
        {
            return "FMAP_PAL.COL";
        }

        if (upperName.StartsWith("NITE", StringComparison.Ordinal))
        {
            return "NIGHTSKY.COL";
        }

        return upperName switch
        {
            "DANK02I0.IMG" => "DANKBMAP.COL",
            "TMAP00I0.IMG" => "MAP.PAL",
            _ => "ART_PAL.COL",
        };
    }

    /// <summary>Parses and decodes the single image, routing headered vs headerless by file size.</summary>
    public static DaggerfallImgFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        var upperName = name.ToUpperInvariant();
        if (IsUnsupported(upperName))
        {
            throw new NotSupportedException(
                $"'{name}' is one of the three retail IMG files with no image data " +
                "(a 12-byte all-zero header); the reference refuses them and so does this decoder.");
        }

        int offsetX;
        int offsetY;
        int width;
        int height;
        ushort compression;
        int dataStart;
        bool hasHeader;

        if (HeaderlessDimensionsBySize.TryGetValue(bytes.Length, out var dimensions))
        {
            // Headerless images have no offsets and are never compressed; the size table is the
            // only source of geometry, and it is consulted BEFORE any header read, as in the
            // reference.
            (width, height) = dimensions;
            offsetX = 0;
            offsetY = 0;
            compression = 0;
            dataStart = 0;
            hasHeader = false;
        }
        else
        {
            if (bytes.Length < HeaderLength)
            {
                throw new InvalidDataException(
                    $"'{name}' is too small for an IMG header ({bytes.Length} bytes) and its size " +
                    "is not in the headerless table.");
            }

            offsetX = BinaryPrimitives.ReadInt16LittleEndian(bytes);
            offsetY = BinaryPrimitives.ReadInt16LittleEndian(bytes[2..]);
            width = BinaryPrimitives.ReadInt16LittleEndian(bytes[4..]);
            height = BinaryPrimitives.ReadInt16LittleEndian(bytes[6..]);
            compression = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
            // bytes[10..12] is the u16 pixel-data length. Retail always has it equal to
            // width*height and the reference never consults it when decoding an IMG, so neither
            // does this port.
            dataStart = HeaderLength;
            hasHeader = true;

            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException(
                    $"'{name}' declares empty geometry ({width}x{height}).");
            }
        }

        var pixelCount = width * height;
        byte[] pixels;
        int paletteStart;
        if (compression == CompressionRleCompressed)
        {
            // The reference's ImgFile reads raw bytes regardless of the compression word; the
            // shared header defines 0x0002 as the CIF byte-code RLE, so honour it here. No
            // retail IMG carries it (2026-09-02: 189 files at 0x0000, two at raw 0x0800).
            pixels = new byte[pixelCount];
            paletteStart = dataStart + DecodeRle(bytes, name, dataStart, pixels);
        }
        else
        {
            // Any other word — 0x0000, or the two retail 0x0800 files — is raw width*height
            // indices, exactly the reference's unconditional read.
            if (dataStart + pixelCount > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}' holds {bytes.Length - dataStart} pixel bytes but " +
                    $"{width}x{height} needs {pixelCount}.");
            }

            pixels = bytes.Slice(dataStart, pixelCount).ToArray();
            paletteStart = dataStart + pixelCount;
        }

        Palette? embeddedPalette = null;
        if (EmbeddedPaletteFileNames.Contains(upperName))
        {
            if (paletteStart + Palette.RgbByteCount > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}' should carry a {Palette.RgbByteCount}-byte embedded palette after " +
                    "its pixel data, but the file ends first.");
            }

            embeddedPalette = Palette.FromVga6Bit(bytes.Slice(paletteStart, Palette.RgbByteCount));
        }

        var bitmap = new IndexedBitmap(width, height, pixels, offsetX, offsetY);
        return new DaggerfallImgFile(name, bitmap, embeddedPalette, compression, hasHeader);
    }

    /// <summary>
    ///     The byte-code RLE from the reference's <c>BaseImageFile.ReadRleData</c>: a code byte
    ///     &gt; 127 repeats the following byte (code - 127) times; a code &lt;= 127 copies
    ///     (code + 1) literal bytes. Every code writes at least one pixel, so the loop always
    ///     progresses; truncation and frame overflow throw. Returns the compressed bytes consumed.
    /// </summary>
    private static int DecodeRle(ReadOnlySpan<byte> bytes, string name, int start, Span<byte> destination)
    {
        var source = start;
        var written = 0;
        while (written < destination.Length)
        {
            if (source >= bytes.Length)
            {
                throw new InvalidDataException($"'{name}': RLE stream is truncated.");
            }

            int code = bytes[source++];
            if (code > 127)
            {
                if (source >= bytes.Length)
                {
                    throw new InvalidDataException($"'{name}': RLE repeat run is truncated.");
                }

                var value = bytes[source++];
                var run = code - 127;
                if (written + run > destination.Length)
                {
                    throw new InvalidDataException(
                        $"'{name}': RLE repeat run overflows the {destination.Length}-pixel image.");
                }

                destination.Slice(written, run).Fill(value);
                written += run;
            }
            else
            {
                var run = code + 1;
                if (source + run > bytes.Length)
                {
                    throw new InvalidDataException($"'{name}': RLE literal run is truncated.");
                }

                if (written + run > destination.Length)
                {
                    throw new InvalidDataException(
                        $"'{name}': RLE literal run overflows the {destination.Length}-pixel image.");
                }

                bytes.Slice(source, run).CopyTo(destination[written..]);
                source += run;
                written += run;
            }
        }

        return source - start;
    }
}
