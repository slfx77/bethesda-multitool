namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>
///     Expands the run-length packets of a Targa image type 10, which is what the Van Buren
///     prototype's "compressed" image family turned out to be.
///     <para>
///         ⚑⚑
///         <b>
///             The whole family is plain TGA (measured 2026-09-06), which the earlier reading
///             missed.
///         </b>
///         What this repo recorded as a "format id" is the TGA HEADER: byte 2 is the
///         image type, so <c>0x00020000</c> is type <b>2</b> (uncompressed true-colour) and
///         <c>0x000A0000</c> is type <b>10</b> (RLE true-colour). That also explains three things
///         previously written down as unexplained quirks — the 18-byte header, the BGR channel
///         order, and the "26-byte trailer", which is the standard TGA 2.0 FOOTER.
///     </para>
///     <para>
///         ⚑ Proof is exact consumption on all <b>85</b> compressed payloads: each decodes its
///         width×height pixels exactly, then leaves exactly <see cref="ExtensionAreaLength" /> bytes
///         — the TGA 2.0 extension area, whose offset the footer declares on every one — followed by
///         the 26-byte footer ending <c>TRUEVISION-XFILE.</c>. 85 of 85 carry that footer.
///     </para>
///     <para>
///         ⚠ A packet's count is <b>one less than the run</b>: the high bit marks a run of
///         <c>(c &amp; 0x7F) + 1</c> identical pixels, a clear high bit a literal of <c>c + 1</c>
///         pixels. Reading the count as the length itself decodes a plausible-looking image that is
///         progressively wrong toward the bottom, which is exactly the kind of error that survives a
///         glance at a thumbnail.
///     </para>
/// </summary>
internal static class VanBurenTgaRle
{
    /// <summary>Bytes of the TGA 2.0 extension area, present on every compressed payload.</summary>
    public const int ExtensionAreaLength = 495;

    /// <summary>Bytes of the TGA 2.0 footer.</summary>
    public const int FooterLength = 26;

    /// <summary>The signature the footer ends with.</summary>
    public static ReadOnlySpan<byte> FooterSignature => "TRUEVISION-XFILE."u8;

    /// <summary>Whether the payload carries a TGA 2.0 footer.</summary>
    public static bool HasFooter(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= FooterLength
               && bytes[^18..^1].SequenceEqual(FooterSignature);
    }

    /// <summary>
    ///     Expands <paramref name="pixelCount" /> pixels of <paramref name="bytesPerPixel" /> bytes
    ///     from the packets at <paramref name="source" />, returning false when the stream does not
    ///     produce exactly that many.
    /// </summary>
    public static bool TryExpand(
        ReadOnlySpan<byte> source, int pixelCount, int bytesPerPixel, Span<byte> destination, out int consumed)
    {
        consumed = 0;
        if (pixelCount <= 0 || bytesPerPixel <= 0 || destination.Length < pixelCount * bytesPerPixel)
        {
            return false;
        }

        var at = 0;
        var written = 0;
        while (written < pixelCount)
        {
            if (at >= source.Length)
            {
                return false;
            }

            var control = source[at++];
            var run = (control & 0x7F) + 1;
            if (written + run > pixelCount)
            {
                return false;
            }

            if ((control & 0x80) != 0)
            {
                if (at + bytesPerPixel > source.Length)
                {
                    return false;
                }

                var pixel = source.Slice(at, bytesPerPixel);
                at += bytesPerPixel;
                for (var i = 0; i < run; i++)
                {
                    pixel.CopyTo(destination[((written + i) * bytesPerPixel)..]);
                }
            }
            else
            {
                var span = run * bytesPerPixel;
                if (at + span > source.Length)
                {
                    return false;
                }

                source.Slice(at, span).CopyTo(destination[(written * bytesPerPixel)..]);
                at += span;
            }

            written += run;
        }

        consumed = at;
        return true;
    }
}
