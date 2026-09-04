using System.Buffers.Binary;
using System.Text;
using DDXConv.Compression;

namespace BethesdaMultitool.Core.Recovery;

/// <summary>
///     One XMemCompress stream found inside otherwise-unattributed dump bytes.
/// </summary>
/// <param name="Offset">File offset of the stream's first chunk header.</param>
/// <param name="CompressedBytes">Input bytes the decoder consumed.</param>
/// <param name="InflatedBytes">Bytes produced.</param>
/// <param name="ContentSniff">
///     What the inflated payload looks like — a known magic, "text", or "binary".
/// </param>
/// <param name="LeadingHex">First 16 inflated bytes, for eyeballing an unrecognised hit.</param>
public sealed record LzxProbeHit(
    long Offset,
    int CompressedBytes,
    int InflatedBytes,
    string ContentSniff,
    string LeadingHex);

/// <summary>
///     Scans dump bytes for Xbox 360 <c>XMemCompress</c> (LZX) streams and inflates the ones that
///     decode, using the same decoder the DDX path uses.
///     <para>
///         Motivation (USER, 2026-09-03): the 2026-08 recovery probe tried <c>zlib</c> on every gap
///         candidate and failed corpus-wide with "unsupported compression method" — the console
///         does not use zlib. <c>DDXConv.Compression.LzxDecompressor</c> is our own managed port of
///         the real algorithm, verified byte-for-byte against XnaNative.dll on 3,870 DDX files, and
///         it is already a project reference. This probe is that earlier experiment re-run with the
///         right codec, so "is there compressed data in memory we are blind to?" gets a measured
///         answer instead of an artefact of the wrong decoder.
///     </para>
///     <para>
///         Acceptance is content-based, and it has to be. An XMemCompress chunk header is a bare
///         2-byte big-endian length (or 0xFF plus four length bytes), so nearly every offset in a
///         dump is a syntactically valid header; worse, the decoder inflates random bytes into
///         kilobytes of random output — measured on pseudo-random input while writing this, and
///         pinned by <c>Probe_ReportsNothingForNoiseThatMerelyLooksLikeAChunkHeader</c>. Neither
///         the framing nor the output size discriminates. What does is whether the inflated
///         payload is <i>identifiable</i> — a container magic or readable text — so that is the
///         bar, and <c>requireKnownContent: false</c> only lowers it for deliberate exploration.
///     </para>
///     <para>
///         Consequence worth stating plainly: this probe finds compressed data that begins at a
///         recognisable header and inflates to something nameable. A stream whose first chunk was
///         paged out, or one holding a payload with no magic and no text, is invisible to it — a
///         miss here is not proof of absence.
///     </para>
/// </summary>
public static class LzxGapProbe
{
    /// <summary>Chunk framing constant mirrored from the decoder: max total bytes in one chunk.</summary>
    private const int MaxTotalChunkSize = 0x980A;

    /// <summary>Smallest inflation worth reporting. Below this, coincidence dominates.</summary>
    private const int MinInterestingInflatedBytes = 2048;

    /// <summary>Sniff value for a payload that decoded but resembles nothing in particular.</summary>
    private const string UnidentifiedSniff = "binary";

    /// <summary>Cap for the confirming decode of a hit, so a runaway stream cannot allocate without bound.</summary>
    private const int MaxInflatedBytesPerAttempt = 8 * 1024 * 1024;

    /// <summary>
    ///     Output budget for the first, screening decode of every candidate offset.
    ///     <para>
    ///         This is the difference between a probe that finishes and one that does not. Most
    ///         offsets that decode at all decode into unidentifiable noise, and inflating each of
    ///         those to the full 8 MB cap makes a whole-dump sweep take hours (measured: a sweep of
    ///         xex44 had not reached its first CSV row after ~20 minutes). The sniff only ever reads
    ///         the first 512 bytes, so screening needs a fraction of that budget; the full size is
    ///         re-measured only for the few candidates that pass.
    ///     </para>
    /// </summary>
    private const int ScreeningInflatedByteBudget = 64 * 1024;

    private static readonly (byte[] Magic, string Name)[] KnownMagics =
    [
        ("BSA\0"u8.ToArray(), "bsa"),
        ("DDS "u8.ToArray(), "dds"),
        ("DDX "u8.ToArray(), "ddx"),
        ("RIFF"u8.ToArray(), "riff/xma"),
        ("TES4"u8.ToArray(), "esm-record"),
        ("GRUP"u8.ToArray(), "esm-grup"),
        ("BTDX"u8.ToArray(), "ba2"),
        ("PNG"u8.ToArray(), "png"),
        ("XDBF"u8.ToArray(), "xdbf"),
        ("Gamebryo"u8.ToArray(), "nif"),
        ("NetImmerse"u8.ToArray(), "nif")
    ];

    /// <summary>
    ///     Probe one region. <paramref name="stride" /> trades coverage for time; 4 matches the
    ///     dump's own pointer alignment and is what the caller should use unless it is sweeping a
    ///     whole 230 MB file, where 16 keeps a full pass tractable.
    /// </summary>
    /// <param name="data">Region bytes.</param>
    /// <param name="baseOffset">File offset of <paramref name="data" />[0], for reporting.</param>
    /// <param name="stride">Offset step between attempts.</param>
    /// <param name="maxHits">Stop after this many hits; 0 for no limit.</param>
    /// <param name="requireKnownContent">
    ///     Keep only hits whose payload is identifiable (container magic or text). Leave this on:
    ///     with it off, pseudo-random bytes produce hits, because the decoder will inflate them.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static IReadOnlyList<LzxProbeHit> Probe(
        ReadOnlySpan<byte> data,
        long baseOffset = 0,
        int stride = 4,
        int maxHits = 0,
        bool requireKnownContent = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);

        var hits = new List<LzxProbeHit>();
        if (data.Length < 64)
        {
            return hits;
        }

        var input = new byte[MaxTotalChunkSize * 4];
        var screening = new byte[ScreeningInflatedByteBudget];
        byte[]? confirming = null;

        var position = 0;
        while (position < data.Length - 8)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!LooksLikeChunkHeader(data[position..]))
            {
                position += stride;
                continue;
            }

            var available = Math.Min(input.Length, data.Length - position);
            data.Slice(position, available).CopyTo(input);

            // Stage 1: screen cheaply. Enough output to sniff, no more.
            var screened = TryInflate(input, available, screening, baseOffset + position);
            if (screened is null)
            {
                position += stride;
                continue;
            }

            if (requireKnownContent && screened.ContentSniff == UnidentifiedSniff)
            {
                // Decoded, but into nothing nameable. Advancing by stride rather than by the
                // consumed length matters: a coincidental inflation would otherwise skip over a
                // real stream that starts a few bytes later.
                position += stride;
                continue;
            }

            // Stage 2: this one is worth measuring properly, so re-run it with the full budget.
            // Only survivors pay for this, which is what keeps a whole-dump sweep tractable.
            confirming ??= new byte[MaxInflatedBytesPerAttempt];
            var hit = TryInflate(input, available, confirming, baseOffset + position) ?? screened;

            hits.Add(hit);
            if (maxHits > 0 && hits.Count >= maxHits)
            {
                break;
            }

            // A successful stream is consumed whole — never re-probe inside its own output, which
            // would otherwise report one archive as hundreds of overlapping hits.
            position += Math.Max(stride, hit.CompressedBytes);
        }

        return hits;
    }

    /// <summary>
    ///     Cheap pre-filter. A chunk header is either the 0xFF long form or a 2-byte big-endian
    ///     compressed size that has to fit the framing budget and the bytes actually present.
    ///     This only removes the obviously impossible; the decoder is the real test.
    /// </summary>
    private static bool LooksLikeChunkHeader(ReadOnlySpan<byte> data)
    {
        if (data[0] == 0xFF)
        {
            return data.Length >= 5;
        }

        var compressedSize = BinaryPrimitives.ReadUInt16BigEndian(data);
        return compressedSize >= 16
               && compressedSize + 2 <= MaxTotalChunkSize
               && compressedSize + 2 <= data.Length;
    }

    private static LzxProbeHit? TryInflate(byte[] input, int available, byte[] output, long offset)
    {
        try
        {
            using var decoder = new LzxDecompressor();
            var inputCount = available;
            var outputCount = output.Length;
            decoder.Decompress(input, 0, ref inputCount, output, 0, ref outputCount);

            if (outputCount < MinInterestingInflatedBytes || inputCount <= 0)
            {
                return null;
            }

            return new LzxProbeHit(
                offset,
                inputCount,
                outputCount,
                Sniff(output.AsSpan(0, outputCount)),
                Convert.ToHexString(output.AsSpan(0, Math.Min(16, outputCount))));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Garbage in means a throw or nonsense out; either way this offset is not a stream.
            return null;
        }
    }

    private static string Sniff(ReadOnlySpan<byte> payload)
    {
        foreach (var (magic, name) in KnownMagics)
        {
            if (payload.Length >= magic.Length && payload[..magic.Length].SequenceEqual(magic))
            {
                return name;
            }
        }

        // The payload must READ as text from its first byte, not merely average out that way.
        // A 90%-printable test over a 512-byte window alone passed payloads whose leading bytes
        // were plainly binary (measured on xex44: 2 of 3 reported hits began 00 00 00 00 00 00 …
        // and were still called "text"), which is exactly the false positive this gate exists to
        // stop — a real recovered file starts with its own content, not with a run of nulls.
        var sample = payload[..Math.Min(512, payload.Length)];
        var lead = sample[..Math.Min(16, sample.Length)];
        foreach (var value in lead)
        {
            if (!IsPrintable(value))
            {
                return UnidentifiedSniff;
            }
        }

        var printable = 0;
        foreach (var value in sample)
        {
            if (IsPrintable(value))
            {
                printable++;
            }
        }

        return printable > sample.Length * 9 / 10 ? "text" : UnidentifiedSniff;
    }

    private static bool IsPrintable(byte value)
    {
        return value is >= 0x20 and < 0x7F or (byte)'\n' or (byte)'\r' or (byte)'\t';
    }

    /// <summary>Render hits as CSV, matching the 2026-08 probe artefact's shape.</summary>
    public static string ToCsv(string dumpName, IReadOnlyList<LzxProbeHit> hits)
    {
        var builder = new StringBuilder(
            "dump,offset,compressed_bytes,inflated_bytes,ratio,content_sniff,leading_hex\n");
        foreach (var hit in hits)
        {
            var ratio = hit.CompressedBytes == 0
                ? 0
                : (double)hit.InflatedBytes / hit.CompressedBytes;
            builder.Append(dumpName).Append(',')
                .Append("0x").Append(hit.Offset.ToString("X8")).Append(',')
                .Append(hit.CompressedBytes).Append(',')
                .Append(hit.InflatedBytes).Append(',')
                .Append(ratio.ToString("F2")).Append(',')
                .Append(hit.ContentSniff).Append(',')
                .Append(hit.LeadingHex).Append('\n');
        }

        return builder.ToString();
    }
}
