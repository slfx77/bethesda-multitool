// Chunk grammar and the line-table decompression follow ariscop/battlespire-tools
//   (https://github.com/ariscop/battlespire-tools, Unlicense/public domain) — bsitool/bsi.py,
//   bsistructs.py and bsi_format.txt. License texts are collected centrally in
//   THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Xngine;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>One image in a <c>.BSI</c>: its header fields, frames and the palettes that shipped with it.</summary>
internal sealed class BsiImage
{
    /// <summary>Steps in an <c>HTBL</c> light ramp — 16 on all 1,937 retail entries that carry one.</summary>
    public const int LightRampSteps = 16;

    /// <summary>Colours per ramp step.</summary>
    public const int LightRampColors = 256;

    /// <summary>Optional <c>NAME</c> chunk value that preceded this image.</summary>
    public required string? Name { get; init; }

    /// <summary>Draw offsets from the header.</summary>
    public required int XOffset { get; init; }

    /// <summary>Draw offsets from the header.</summary>
    public required int YOffset { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Frames stacked in the data block.</summary>
    public required int FrameCount { get; init; }

    /// <summary>Header compression word: 0 raw, 4 and 6 line-table compressed.</summary>
    public required int Compression { get; init; }

    /// <summary>The decoded frames, one <see cref="IndexedBitmap" /> each.</summary>
    public required IReadOnlyList<IndexedBitmap> Frames { get; init; }

    /// <summary>
    ///     The 256-colour <c>CMAP</c> palette (6-bit VGA), or null when the image carried none. This
    ///     is the palette that renders correctly: retail images index the whole 0-255 range.
    /// </summary>
    public required Palette? ColorMap { get; init; }

    /// <summary>
    ///     The <c>HICL</c> palette: 128 15-bit colours which the reference expands into the EVEN
    ///     palette slots, leaving the odd ones undefined. Kept for completeness; it cannot render an
    ///     image that uses indices above 127, which nearly every retail image does.
    /// </summary>
    public required Palette? HighColor { get; init; }

    /// <summary>
    ///     The raw <c>HTBL</c> chunk: a <b>16-step LIGHT RAMP</b> of the palette, 256 15-bit colours
    ///     per step.
    ///     <para>
    ///         ⚑ <b>Measured 2026-09-06, and it was previously recorded wrong twice over.</b> The
    ///         chunk holds <b>16</b> tables, never the 32 this comment used to claim, and they are
    ///         lighting ramps in fact rather than "believed": across all <b>1,937</b> retail BSI
    ///         entries that carry an HTBL, every one has exactly 16 tables and every one increases
    ///         monotonically in mean luminance.
    ///     </para>
    ///     <para>
    ///         ⚑ The ramp is LINEAR. Averaged over those 1,937 files, table <c>i</c>'s brightness
    ///         relative to the brightest is 0.011, 0.066, 0.127, 0.197, 0.256, 0.327, 0.390, 0.465,
    ///         0.522, 0.586, 0.650, 0.724, 0.784, 0.851, 0.911, 0.998 — which is <c>i / 15</c> to
    ///         within a percent at every step. Table 15 is full brightness, table 0 near black.
    ///     </para>
    ///     <para>
    ///         ⚠ Not used for rendering here: <c>sprite render</c> draws through the 256-colour
    ///         <c>CMAP</c>, since a static export has no light level to pick a step with. The ramp
    ///         is what a LIT renderer would index.
    ///     </para>
    /// </summary>
    public required ReadOnlyMemory<byte> HighColorTable { get; init; }
}

/// <summary>
///     A Battlespire <c>.BSI</c> image file: a stream of chunks, each a 4-byte ASCII tag and a
///     BIG-endian u32 length, with the payloads little-endian. <c>BHDR</c> opens an image and
///     <c>DATA</c> closes it, so one file can hold several.
///     <para>
///         Measured on the retail <c>BSI.BSA</c> (2026-09-03): 2,599 entries, 577 of them
///         LZSS-compressed, yield 2,621 images that all decode to exactly
///         <c>width * height * frames</c> bytes. Compression words are 0 (2,085), 6 (521) and 4
///         (15). Seven archive entries are not images at all (a batch file, DOS listings, an
///         executable), so a caller must tolerate a rejected entry, and one entry (BIP.BSI) is an
///         authored-empty file holding nothing but an END chunk.
///     </para>
///     <para>
///         Two ways in. <see cref="Parse" /> decodes every image, as the legacy sprite and texture
///         paths need. <see cref="ReadHeaders" /> walks the same chunk grammar and reads only the
///         <c>BHDR</c> fields and each chunk's byte range, never a <c>DATA</c> payload or a palette
///         entry: the cut-1c inspection path (plan D9).
///     </para>
/// </summary>
internal sealed class BsiFile
{
    /// <summary>Bytes in a <c>HICL</c> chunk: 128 15-bit colours.</summary>
    public const int HighColorLength = 256;

    /// <summary>Bytes in a <c>CMAP</c> chunk: 256 6-bit RGB triplets.</summary>
    public const int ColorMapLength = 768;

    private const int ChunkHeaderLength = 8;

    private BsiFile(string name, IReadOnlyList<BsiImage> images)
    {
        Name = name;
        Images = images;
    }

    /// <summary>Logical file name.</summary>
    public string Name { get; }

    /// <summary>Images in file order.</summary>
    public IReadOnlyList<BsiImage> Images { get; }

    /// <summary>True for a <c>.BSI</c> name.</summary>
    public static bool IsBsiFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.EndsWith(".BSI", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses a complete <c>.BSI</c> image file.</summary>
    public static BsiFile Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        var images = new List<BsiImage>();
        string? pendingName = null;
        BsiHeader? header = null;
        Palette? colorMap = null;
        Palette? highColor = null;
        var highColorTable = ReadOnlyMemory<byte>.Empty;

        var position = 0;
        var sawChunk = false;
        while (position + ChunkHeaderLength <= bytes.Length)
        {
            var tag = Encoding.ASCII.GetString(bytes, position, 4);
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 4));

            // BSIF's length counts from the header rather than after it, and the engine simply
            // skips the eight bytes, so this reader does too.
            if (tag == "BSIF")
            {
                position += ChunkHeaderLength;
                sawChunk = true;
                continue;
            }

            if (tag == "END ")
            {
                sawChunk = true;
                break;
            }

            if (!IsKnownTag(tag))
            {
                if (!sawChunk)
                {
                    throw new InvalidDataException($"{name} does not open with a BSI chunk (saw '{Printable(tag)}').");
                }

                throw new InvalidDataException($"{name}: unknown chunk '{Printable(tag)}' at {position}.");
            }

            sawChunk = true;
            if (position + ChunkHeaderLength + length > (uint)bytes.Length)
            {
                throw new InvalidDataException(
                    $"{name}: chunk '{tag}' at {position} declares {length} bytes, past the {bytes.Length}-byte file.");
            }

            var payload = new ReadOnlyMemory<byte>(bytes, position + ChunkHeaderLength, (int)length);
            position += ChunkHeaderLength + (int)length;

            switch (tag)
            {
                case "NAME":
                    pendingName = ReadCString(payload.Span);
                    break;
                case "BHDR":
                    header = BsiHeader.Read(payload.Span, name);
                    break;
                case "HICL":
                    highColor = payload.Length == HighColorLength ? FromHighColor(payload.Span) : null;
                    break;
                case "HTBL":
                    highColorTable = payload;
                    break;
                case "CMAP":
                    colorMap = payload.Length == ColorMapLength ? Palette.FromVga6Bit(payload.Span) : null;
                    break;
                case "DATA":
                    if (header is null)
                    {
                        throw new InvalidDataException($"{name}: a DATA chunk arrived before any BHDR.");
                    }

                    images.Add(BuildImage(header, payload, pendingName, colorMap, highColor, highColorTable, name));
                    header = null;
                    pendingName = null;
                    break;
            }
        }

        if (!sawChunk)
        {
            throw new InvalidDataException($"{name} holds no BSI chunks.");
        }

        // An authored-empty file is legal: retail's BIP.BSI is eight bytes holding only END.
        return new BsiFile(name, images);
    }

    /// <summary>
    ///     Walks the chunk stream the way <see cref="Parse" /> does, with the same tags, the same
    ///     structural rejections and the same <c>BHDR</c> checks, but reads nothing beyond the chunk
    ///     headers and the 26 <c>BHDR</c> bytes: no <c>DATA</c> byte, no line table, no palette entry.
    ///     Each image reports its declared geometry and where every chunk of it lies, so a caller can
    ///     slice the original bytes or judge a raw image's payload length without decoding.
    ///     <para>
    ///         Measured 2026-09-28 over the 2,592 parsable retail BSI.BSA entries (2,621 images): the
    ///         dominant chunk order is <c>IFHD, [NAME,] BHDR, [HICL,] [HTBL,] CMAP, DATA</c>, on 2,616 of
    ///         the 2,621 images (2,255 + 228 with a NAME + 98 without HTBL + 1 without HICL or HTBL, plus
    ///         the 34 images of the 4 multi-image entries, 6 to 11 each, which follow a leading BSIF).
    ///         Five images deviate: four entries read <c>BSIF, IFHD, BHDR, CMAP, HICL, DATA</c> (CMAP
    ///         before HICL) and one reads <c>BSIF, BHDR, HICL, CMAP, DATA</c> (no IFHD at all). Per chunk:
    ///         2,621 carry a CMAP, 2,620 an HICL, 2,517 an HTBL, 228 a NAME (always before BHDR), every
    ///         BHDR is 26 bytes, and no entry has bytes after END. An image is therefore anchored at its
    ///         first chunk after the previous image's DATA (or after a leading BSIF), never at an IFHD,
    ///         which is why its chunk range is right on the five deviating images as well.
    ///     </para>
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a BSI chunk stream, or a chunk or BHDR is malformed.</exception>
    public static BsiFileHeaders ReadHeaders(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        var images = new List<BsiImageHeader>();
        string? pendingName = null;
        BsiHeader? header = null;
        var headerOffset = -1;
        var imageStart = -1;
        ByteArea? colorMap = null;
        ByteArea? highColor = null;
        ByteArea? highColorTable = null;

        var position = 0;
        var sawChunk = false;
        int? endOffset = null;
        while (position + ChunkHeaderLength <= bytes.Length)
        {
            var tag = Encoding.ASCII.GetString(bytes, position, 4);
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 4));

            if (tag == "BSIF")
            {
                position += ChunkHeaderLength;
                sawChunk = true;
                continue;
            }

            if (tag == "END ")
            {
                sawChunk = true;
                endOffset = position;
                break;
            }

            if (!IsKnownTag(tag))
            {
                if (!sawChunk)
                {
                    throw new InvalidDataException($"{name} does not open with a BSI chunk (saw '{Printable(tag)}').");
                }

                throw new InvalidDataException($"{name}: unknown chunk '{Printable(tag)}' at {position}.");
            }

            sawChunk = true;
            if (position + ChunkHeaderLength + length > (uint)bytes.Length)
            {
                throw new InvalidDataException(
                    $"{name}: chunk '{tag}' at {position} declares {length} bytes, past the {bytes.Length}-byte file.");
            }

            if (imageStart < 0)
            {
                imageStart = position;
            }

            var payloadStart = position + ChunkHeaderLength;
            var payloadLength = (int)length;
            position = payloadStart + payloadLength;

            switch (tag)
            {
                case "NAME":
                    pendingName = ReadCString(bytes.AsSpan(payloadStart, payloadLength));
                    break;
                case "BHDR":
                    header = BsiHeader.Read(bytes.AsSpan(payloadStart, payloadLength), name);
                    headerOffset = payloadStart;
                    break;
                case "HICL":
                    highColor = payloadLength == HighColorLength
                        ? new ByteArea("HICL", payloadStart, payloadStart + payloadLength)
                        : null;
                    break;
                case "HTBL":
                    highColorTable = new ByteArea("HTBL", payloadStart, payloadStart + payloadLength);
                    break;
                case "CMAP":
                    colorMap = payloadLength == ColorMapLength
                        ? new ByteArea("CMAP", payloadStart, payloadStart + payloadLength)
                        : null;
                    break;
                case "DATA":
                    if (header is null)
                    {
                        throw new InvalidDataException($"{name}: a DATA chunk arrived before any BHDR.");
                    }

                    images.Add(new BsiImageHeader(
                        images.Count,
                        pendingName,
                        header.XOffset,
                        header.YOffset,
                        header.Width,
                        header.Height,
                        header.FrameCount,
                        header.Compression,
                        new ByteArea($"image:{images.Count}", imageStart, position),
                        headerOffset,
                        new ByteArea("DATA", payloadStart, position),
                        colorMap,
                        highColor,
                        highColorTable));
                    header = null;
                    pendingName = null;
                    imageStart = -1;
                    break;
            }
        }

        if (!sawChunk)
        {
            throw new InvalidDataException($"{name} holds no BSI chunks.");
        }

        return new BsiFileHeaders(name, bytes.Length, endOffset, images);
    }

    private static bool IsKnownTag(string tag)
    {
        return tag is "IFHD" or "NAME" or "BHDR" or "HICL" or "HTBL" or "CMAP" or "DATA";
    }

    private static BsiImage BuildImage(
        BsiHeader header,
        ReadOnlyMemory<byte> data,
        string? imageName,
        Palette? colorMap,
        Palette? highColor,
        ReadOnlyMemory<byte> highColorTable,
        string fileName)
    {
        var pixels = header.Compression == 0
            ? data.Span.ToArray()
            : Decompress(data.Span, header, fileName);

        var frameLength = header.Width * header.Height;
        if (pixels.Length != frameLength * header.FrameCount)
        {
            throw new InvalidDataException(
                $"{fileName}: image data is {pixels.Length} bytes, not the {frameLength * header.FrameCount} its {header.Width}x{header.Height}x{header.FrameCount} header declares.");
        }

        var frames = new IndexedBitmap[header.FrameCount];
        for (var i = 0; i < header.FrameCount; i++)
        {
            frames[i] = new IndexedBitmap(
                header.Width,
                header.Height,
                pixels.AsSpan(i * frameLength, frameLength).ToArray(),
                header.XOffset,
                header.YOffset);
        }

        return new BsiImage
        {
            Name = imageName,
            XOffset = header.XOffset,
            YOffset = header.YOffset,
            Width = header.Width,
            Height = header.Height,
            FrameCount = header.FrameCount,
            Compression = header.Compression,
            Frames = frames,
            ColorMap = colorMap,
            HighColor = highColor,
            HighColorTable = highColorTable
        };
    }

    /// <summary>
    ///     Expands a compressed image: the data block opens with one u32 per line (height x frames),
    ///     whose top bit says the line is run-length coded and whose low 31 bits are its offset from
    ///     the start of the block. A run byte with bit 7 set repeats the next byte; otherwise it
    ///     counts literal bytes.
    /// </summary>
    private static byte[] Decompress(ReadOnlySpan<byte> data, BsiHeader header, string fileName)
    {
        var lines = header.Height * header.FrameCount;
        if (lines * 4 > data.Length)
        {
            throw new InvalidDataException(
                $"{fileName}: the {lines}-line offset table does not fit in {data.Length} bytes.");
        }

        var output = new byte[header.Width * lines];
        var written = 0;
        for (var line = 0; line < lines; line++)
        {
            var entry = BinaryPrimitives.ReadUInt32LittleEndian(data[(line * 4)..]);
            var compressed = (entry & 0x8000_0000) != 0;
            var offset = (int)(entry & 0x7FFF_FFFF);
            if (offset < 0 || offset > data.Length)
            {
                throw new InvalidDataException(
                    $"{fileName}: line {line} starts at {offset}, outside the {data.Length}-byte data block.");
            }

            if (!compressed)
            {
                if (offset + header.Width > data.Length)
                {
                    throw new InvalidDataException($"{fileName}: raw line {line} runs past the data block.");
                }

                data.Slice(offset, header.Width).CopyTo(output.AsSpan(written));
                written += header.Width;
                continue;
            }

            var read = offset;
            var produced = 0;
            while (produced < header.Width)
            {
                if (read >= data.Length)
                {
                    throw new InvalidDataException(
                        $"{fileName}: line {line} ran out of input after {produced} of {header.Width} pixels.");
                }

                var control = data[read++];
                var count = control & 0x7F;
                if ((control & 0x80) != 0)
                {
                    if (read >= data.Length || produced + count > header.Width)
                    {
                        throw new InvalidDataException(
                            $"{fileName}: line {line} has a run of {count} that overruns its width.");
                    }

                    output.AsSpan(written + produced, count).Fill(data[read++]);
                }
                else
                {
                    if (read + count > data.Length || produced + count > header.Width)
                    {
                        throw new InvalidDataException(
                            $"{fileName}: line {line} has a literal run of {count} that overruns its width.");
                    }

                    data.Slice(read, count).CopyTo(output.AsSpan(written + produced));
                    read += count;
                }

                produced += count;
            }

            written += header.Width;
        }

        return output;
    }

    /// <summary>
    ///     Expands a HICL chunk the way the reference does: 128 15-bit colours land in the EVEN
    ///     palette slots (<c>index &lt;&lt; 1</c>), each channel scaled by 8. The odd slots stay
    ///     black — the format notes call values above 127 unknown.
    /// </summary>
    private static Palette FromHighColor(ReadOnlySpan<byte> chunk)
    {
        var rgb = new byte[768];
        for (var i = 0; i < 128; i++)
        {
            var colour = BinaryPrimitives.ReadUInt16LittleEndian(chunk[(i * 2)..]);
            var slot = (i << 1) * 3;
            rgb[slot] = (byte)(((colour >> 11) & 0x1F) * 8);
            rgb[slot + 1] = (byte)(((colour >> 6) & 0x1F) * 8);
            rgb[slot + 2] = (byte)(((colour >> 1) & 0x1F) * 8);
        }

        return Palette.FromRgb8(rgb);
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private static string Printable(string tag)
    {
        var builder = new StringBuilder(tag.Length);
        foreach (var character in tag)
        {
            builder.Append(char.IsControl(character) ? '.' : character);
        }

        return builder.ToString();
    }

    /// <summary>The <c>BHDR</c> fields this reader uses.</summary>
    private sealed record BsiHeader(int XOffset, int YOffset, int Width, int Height, int FrameCount, int Compression)
    {
        public static BsiHeader Read(ReadOnlySpan<byte> chunk, string fileName)
        {
            if (chunk.Length < 26)
            {
                throw new InvalidDataException(
                    $"{fileName}: BHDR is {chunk.Length} bytes, not the 26 the format declares.");
            }

            var width = BinaryPrimitives.ReadInt16LittleEndian(chunk[4..]);
            var height = BinaryPrimitives.ReadInt16LittleEndian(chunk[6..]);
            var frames = BinaryPrimitives.ReadInt16LittleEndian(chunk[14..]);
            if (width <= 0 || height <= 0 || frames <= 0 || (long)width * height * frames > 64_000_000)
            {
                throw new InvalidDataException($"{fileName}: implausible image {width}x{height}x{frames}.");
            }

            return new BsiHeader(
                BinaryPrimitives.ReadInt16LittleEndian(chunk),
                BinaryPrimitives.ReadInt16LittleEndian(chunk[2..]),
                width,
                height,
                frames,
                BinaryPrimitives.ReadInt16LittleEndian(chunk[24..]));
        }
    }
}

/// <summary>
///     The header-only view of one image in a <c>.BSI</c>: its <c>BHDR</c> fields and where its chunks
///     lie, read by <see cref="BsiFile.ReadHeaders" /> without touching the pixel data or a palette
///     entry. Palette chunk ranges carry forward across images the way <see cref="BsiFile.Parse" />
///     carries the decoded palettes (a later image without its own <c>CMAP</c> reports the previous
///     one's); a <c>CMAP</c> or <c>HICL</c> of the wrong length is reported as absent, as
///     <see cref="BsiFile.Parse" /> decodes it to null.
/// </summary>
/// <param name="Index">Position among the file's images.</param>
/// <param name="Name">The <c>NAME</c> chunk value that preceded the image, or null.</param>
/// <param name="XOffset">BHDR +0.</param>
/// <param name="YOffset">BHDR +2.</param>
/// <param name="Width">BHDR +4.</param>
/// <param name="Height">BHDR +6.</param>
/// <param name="FrameCount">BHDR +14.</param>
/// <param name="Compression">BHDR +24: 0 raw, 4 and 6 line-table compressed.</param>
/// <param name="Chunks">
///     The image's whole chunk range, <c>image:{index}</c>: from its first chunk after the previous
///     image's <c>DATA</c> (the file's first non-<c>BSIF</c> chunk for image 0) to the end of its own
///     <c>DATA</c> chunk.
/// </param>
/// <param name="HeaderOffset">Where the 26-byte <c>BHDR</c> payload starts.</param>
/// <param name="Data">The <c>DATA</c> payload range: the raw indices when <paramref name="Compression" /> is 0, else the line table and its lines.</param>
/// <param name="ColorMap">The 768-byte <c>CMAP</c> payload range in force for this image, or null.</param>
/// <param name="HighColor">The 256-byte <c>HICL</c> payload range in force for this image, or null.</param>
/// <param name="HighColorTable">The <c>HTBL</c> payload range in force for this image, or null.</param>
internal sealed record BsiImageHeader(
    int Index,
    string? Name,
    int XOffset,
    int YOffset,
    int Width,
    int Height,
    int FrameCount,
    int Compression,
    ByteArea Chunks,
    int HeaderOffset,
    ByteArea Data,
    ByteArea? ColorMap,
    ByteArea? HighColor,
    ByteArea? HighColorTable)
{
    /// <summary>The pixel bytes the header declares: <c>Width x Height x FrameCount</c>, what a raw <c>DATA</c> payload must hold.</summary>
    public long DeclaredPixelBytes => (long)Width * Height * FrameCount;
}

/// <summary>A <c>.BSI</c> file read by <see cref="BsiFile.ReadHeaders" />: every image's headers, nothing decoded.</summary>
/// <param name="Name">Logical file name.</param>
/// <param name="Length">The file's length in bytes.</param>
/// <param name="EndOffset">Where the <c>END </c> chunk starts, or null when the stream ended without one.</param>
/// <param name="Images">Every image's headers, in file order.</param>
internal sealed record BsiFileHeaders(string Name, int Length, int? EndOffset, IReadOnlyList<BsiImageHeader> Images);
