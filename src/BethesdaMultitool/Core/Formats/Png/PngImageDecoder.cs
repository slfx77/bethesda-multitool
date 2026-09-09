using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Dds;
using ImageMagick;

namespace BethesdaMultitool.Core.Formats.Png;

/// <summary>
///     Reads standard PNG images into the RGBA <see cref="DecodedTexture" /> the texture seam
///     already consumes, and reports their IHDR header without inflating anything.
///     <para>
///         The TES Travels mobile art is PNG rather than a bespoke format. Measured across the
///         retail fixtures 2026-09-05, all 88 images (16 loose in Stormhold, 43 inside the
///         Dawnstar <c>imgfiles.lmp</c> lump plus 3 loose, and 26 Oblivion mobile tilesets) are
///         colour type 3, PALETTE, at bit depths 2, 4 and 8, and 33 of them carry a <c>tRNS</c>
///         chunk. ⚠ One Stormhold image is ADAM7 INTERLACED, which a naive sequential reader
///         turns into a scrambled picture with no error, so interlacing is not a skippable case.
///     </para>
///     <para>
///         PNG is a published standard rather than a game format, so the pixel decode goes
///         through Magick.NET — already a dependency of this project and already used for image
///         work under <c>Core/</c> — instead of hand-rolling Adam7 de-interlacing, the five
///         filter types and sub-byte index unpacking. <see cref="ReadInfo" /> is ours because the
///         asset browser wants the header cheaply and an unsupported file should be reported
///         precisely rather than as a decode failure.
///     </para>
/// </summary>
internal static class PngImageDecoder
{
    /// <summary>Signature (8) + IHDR length, type, body and CRC (4 + 4 + 13 + 4).</summary>
    private const int SignatureAndIhdrLength = 33;

    /// <summary>The 8-byte PNG signature.</summary>
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>True when <paramref name="data" /> opens with the PNG signature.</summary>
    public static bool HasPngSignature(ReadOnlySpan<byte> data)
    {
        return data.Length >= Signature.Length && data[..Signature.Length].SequenceEqual(Signature);
    }

    /// <summary>
    ///     Reads the IHDR header, and walks the chunk list only far enough to answer whether a
    ///     <c>tRNS</c> chunk is present. Returns <c>null</c> when <paramref name="data" /> is not
    ///     a PNG or is truncated before the end of IHDR.
    /// </summary>
    public static PngImageInfo? ReadInfo(ReadOnlySpan<byte> data)
    {
        if (!HasPngSignature(data) || data.Length < SignatureAndIhdrLength)
        {
            return null;
        }

        // The spec requires the first chunk to be IHDR with a 13-byte body.
        if (BinaryPrimitives.ReadUInt32BigEndian(data[8..]) != 13 ||
            !data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return new PngImageInfo(
            width,
            height,
            data[24],
            data[25],
            data[28] != 0,
            HasChunk(data, "tRNS"u8));
    }

    /// <summary>
    ///     Decodes <paramref name="data" /> into a single-mip RGBA texture (r, g, b, a byte order,
    ///     matching the DDS decoders and <c>IndexedBitmap.ToDecodedTexture</c>). Palette indices
    ///     and any <c>tRNS</c> alpha are resolved during the decode, so the result is true colour.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     <paramref name="data" /> is not a PNG, or the decode produced no usable pixels.
    /// </exception>
    public static DecodedTexture Decode(ReadOnlySpan<byte> data)
    {
        var info = ReadInfo(data)
                   ?? throw new InvalidDataException("Not a PNG image (signature or IHDR missing).");

        using var image = new MagickImage(data.ToArray());

        // The texture seam is fixed at four channels, so an image with no alpha still has to
        // produce one. Opaque adds a fully-opaque channel rather than inventing transparency.
        if (!image.HasAlpha)
        {
            image.Alpha(AlphaOption.Opaque);
        }

        using var pixels = image.GetPixels();
        var rgba = pixels.ToByteArray(PixelMapping.RGBA)
                   ?? throw new InvalidDataException(
                       $"PNG decode produced no pixels ({info.Width}x{info.Height}, " +
                       $"{info.ColourTypeName}, depth {info.BitDepth}).");

        var width = (int)image.Width;
        var height = (int)image.Height;
        var expected = (long)width * height * 4;
        if (rgba.LongLength != expected)
        {
            throw new InvalidDataException(
                $"PNG decode returned {rgba.LongLength} bytes for {width}x{height} RGBA, " +
                $"expected {expected}.");
        }

        return DecodedTexture.FromBaseLevel(rgba, width, height, false);
    }

    /// <summary>Walks the chunk list looking for <paramref name="type" />, stopping at IEND.</summary>
    private static bool HasChunk(ReadOnlySpan<byte> data, ReadOnlySpan<byte> type)
    {
        var offset = 8;
        while (offset + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            var chunkType = data.Slice(offset + 4, 4);
            if (chunkType.SequenceEqual(type))
            {
                return true;
            }

            if (chunkType.SequenceEqual("IEND"u8))
            {
                return false;
            }

            // length + type + body + CRC, guarding the cast so a corrupt length cannot wrap.
            if (length > int.MaxValue - 12)
            {
                return false;
            }

            offset += 12 + (int)length;
        }

        return false;
    }
}
