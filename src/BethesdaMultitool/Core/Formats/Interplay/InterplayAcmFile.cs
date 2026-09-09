// Ported from libacm 1.5 by Marko Kreen (ISC), https://github.com/markokr/libacm
//
// libacm - Interplay ACM audio decoder.
// Copyright (c) 2004-2010, Marko Kreen
//
// Permission to use, copy, modify, and/or distribute this software for any
// purpose with or without fee is hereby granted, provided that the above
// copyright notice and this permission notice appear in all copies.
//
// THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
// WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
// MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
// ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
// WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
// ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
// OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
//
// The structure of decode.c is kept on purpose so a reviewer can diff the two: the bit reader
// (load_bits / get_bits_reload / GET_BITS), the 32-entry filler table with its k/t/linear
// variants and the four amplitude maps, juggle() / juggle_block(), decode_block() and the
// acm_read() loop all map one-to-one. Where libacm returns negative ACM_ERR_* codes this port
// throws: ACM_ERR_CORRUPT and ACM_ERR_UNEXPECTED_EOF inside a filler become
// InvalidDataException; the ACM_EXPECTED_EOF that fill_block()/decode_block() raise when the
// stream ends cleanly between blocks is a private exception caught at the block loop, exactly
// where GET_BITS_EXPECT_EOF converts it in C. util.c's seek/tell helpers are not needed for a
// whole-file decode and are omitted.

namespace BethesdaMultitool.Core.Formats.Interplay;

/// <summary>The PCM an <see cref="InterplayAcmFile" /> decodes to.</summary>
/// <param name="Samples">Interleaved signed 16-bit samples, exactly the header's total value count.</param>
/// <param name="Channels">
///     How many channels the samples are interleaved as — see
///     <see cref="InterplayAcmFile.DeclaredChannels" />.
/// </param>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="PaddingBitsUsed">
///     Bits of libacm's single appended zero byte that the LAST block's fillers consumed: 0 when
///     the file carries the whole final block, 1-8 when the encoder stopped writing bits inside it
///     and the reader's zero padding stood in for the rest.
/// </param>
/// <param name="TrailingBits">Bits of file left unread after the last block: slack the decoder never needed.</param>
/// <param name="Probe">
///     What the decoded values themselves say about the channel count — see
///     <see cref="InterplayAcmChannelProbe" />.
/// </param>
internal readonly record struct InterplayAcmPcm(
    short[] Samples,
    int Channels,
    int SampleRate,
    int PaddingBitsUsed,
    int TrailingBits,
    InterplayAcmChannelProbe Probe)
{
    /// <summary>Frames (samples per channel), i.e. the value count divided by the channel count.</summary>
    public int FrameCount => Channels == 0 ? 0 : Samples.Length / Channels;

    /// <summary>Playback length in seconds.</summary>
    public double DurationSeconds => SampleRate == 0 ? 0 : (double)FrameCount / SampleRate;

    /// <summary>The same PCM re-labelled with another channel count (the values do not change, only how they interleave).</summary>
    public InterplayAcmPcm WithChannels(int channels)
    {
        return channels is < 1 or > 2
            ? throw new ArgumentOutOfRangeException(nameof(channels), channels, "ACM streams are mono or stereo.")
            : this with { Channels = channels };
    }

    /// <summary>The PCM interleaved as the probe says it is — what a player should use.</summary>
    public InterplayAcmPcm AsInferred()
    {
        return WithChannels(Probe.InferredChannels);
    }
}

/// <summary>
///     The channel count the decoded VALUES support, as opposed to the one the header states.
///     <para>
///         ⚑⚑ Measured 2026-09-07 over every ACM in Fallout 1 MASTER.DAT (2,511), Fallout 2 master.dat
///         (2,473) and the 40 loose music files:
///         <b>
///             the header's channel field is wrong on every speech
///             and sound-effect file that says 2.
///         </b>
///         Two oracles outside the file settle it. (1) The
///         speech lines ship a same-stem .TXT transcript: words per second at the header's stereo
///         reading is 5.3 median / 7.6 p90 on 1,538 FO1 and 970 FO2 lines, against 2.65 / 3.8 read
///         as mono — the second is conversational English, the first is not. (2) 1,301 FO1 and 752
///         FO2 files declaring stereo carry an ODD value total, which no interleaved stereo stream
///         can. The music, by contrast, is 3-4 minutes a track read as stereo (the soundtrack's
///         lengths) and 7-8 read as mono.
///     </para>
///     <para>
///         The values carry the answer on their own: a mono signal at 22,050 Hz steps LESS between
///         neighbours than between values two apart (<see cref="Lag1ToLag2Step" /> 0.50-1.60 on all
///         4,944 speech and effect files, header 1 or 2 alike), while two interleaved channels make
///         every neighbour step a cross-channel one (2.44-17.7 on 37 of the 40 music files). The
///         three that are not — MAYBE.ACM (the Ink Spots' mono recording, shipped with L ≈ R, caught
///         by <see cref="WithinToBetweenStep" /> 0.056 against 0.95-1.07 everywhere else) and the two
///         WIND loops (0.507, no pairing: read as mono, ⛔ NOT settled by anything outside the file) —
///         are the ones a threshold has to be honest about.
///     </para>
///     <para>
///         ⚠ <see cref="WithinToBetweenStep" /> CANNOT separate mono from stereo with differing
///         channels: both sit at 1.0 (a cross-channel step is a cross-channel step whichever side of
///         the frame boundary it is on). It only catches paired channels. The rule is therefore:
///         odd total → mono (certain); <see cref="Lag1ToLag2Step" /> ≥ 2 → stereo; pairing ≤ 0.5 →
///         stereo; otherwise mono. What would falsify it: a transcript-bearing line inferring stereo,
///         or a soundtrack cue inferring mono other than the two wind loops — neither occurs.
///     </para>
/// </summary>
/// <param name="InferredChannels">1 or 2: what the values support.</param>
/// <param name="Reason">Which rule decided, for messages.</param>
/// <param name="Lag1ToLag2Step">mean|x[n] − x[n−1]| / mean|x[n] − x[n−2]| over the value stream.</param>
/// <param name="WithinToBetweenStep">
///     mean|x[2i+1] − x[2i]| / mean|x[2i+2] − x[2i+1]|: the step inside a would-be frame
///     against the step across one.
/// </param>
/// <param name="EqualPairFraction">Fraction of would-be frames whose two values are exactly equal.</param>
internal readonly record struct InterplayAcmChannelProbe(
    int InferredChannels,
    string Reason,
    double Lag1ToLag2Step,
    double WithinToBetweenStep,
    double EqualPairFraction)
{
    /// <summary>Neighbour steps this many times the two-apart steps, or more, read as two interleaved channels.</summary>
    public const double StereoStepRatio = 2.0;

    /// <summary>A within-frame step this small a fraction of the across-frame step reads as paired (dual-mono) channels.</summary>
    public const double PairedStepRatio = 0.5;

    /// <summary>Streams shorter than this are labelled as declared; the statistics mean nothing on a handful of values.</summary>
    public const int MinimumValues = 64;

    /// <summary>Measures <paramref name="samples" /> (the interleaved value stream) against the header's channel count.</summary>
    public static InterplayAcmChannelProbe Measure(ReadOnlySpan<short> samples, int declaredChannels)
    {
        long lag1 = 0, lag2 = 0, within = 0, between = 0;
        long pairs = 0, acrossFrames = 0, equalPairs = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            var step = Math.Abs(samples[i] - samples[i - 1]);
            lag1 += step;
            if ((i & 1) == 1)
            {
                // x[2k] -> x[2k+1]: the two values of one would-be frame.
                within += step;
                pairs++;
                if (samples[i] == samples[i - 1])
                {
                    equalPairs++;
                }
            }
            else
            {
                // x[2k+1] -> x[2k+2]: across a would-be frame boundary.
                between += step;
                acrossFrames++;
            }
        }

        for (var i = 2; i < samples.Length; i++)
        {
            lag2 += Math.Abs(samples[i] - samples[i - 2]);
        }

        var lag1Mean = samples.Length > 1 ? (double)lag1 / (samples.Length - 1) : 0;
        var lag2Mean = samples.Length > 2 ? (double)lag2 / (samples.Length - 2) : 0;
        var withinMean = pairs > 0 ? (double)within / pairs : 0;
        var betweenMean = acrossFrames > 0 ? (double)between / acrossFrames : 0;
        var lag1ToLag2 = lag2Mean > 0 ? lag1Mean / lag2Mean : 0;
        var withinToBetween = betweenMean > 0 ? withinMean / betweenMean : 0;
        var equalFraction = pairs > 0 ? (double)equalPairs / pairs : 0;

        int inferred;
        string reason;
        if (samples.Length < MinimumValues)
        {
            inferred = declaredChannels;
            reason = $"fewer than {MinimumValues} values: header taken as declared";
        }
        else if ((samples.Length & 1) == 1)
        {
            inferred = 1;
            reason = "odd value total: two interleaved channels cannot produce one";
        }
        else if (lag2 == 0)
        {
            inferred = declaredChannels;
            reason = "constant stream: header taken as declared";
        }
        else if (lag1ToLag2 >= StereoStepRatio)
        {
            inferred = 2;
            reason = $"neighbour steps {lag1ToLag2:F2}x the two-apart steps: two interleaved channels";
        }
        else if (withinToBetween <= PairedStepRatio)
        {
            inferred = 2;
            reason = $"within-frame step {withinToBetween:F3} of the across-frame step: paired (dual-mono) channels";
        }
        else
        {
            inferred = 1;
            reason =
                $"neighbour steps {lag1ToLag2:F2}x the two-apart steps, no pairing ({withinToBetween:F3}): one channel";
        }

        return new InterplayAcmChannelProbe(inferred, reason, lag1ToLag2, withinToBetween, equalFraction);
    }
}

/// <summary>
///     An Interplay <c>.ACM</c> — the sub-band audio codec Fallout, Fallout 2 and Baldur's Gate
///     ship their speech, music and effects in. Ported from libacm 1.5 (Marko Kreen, ISC).
///     <para>
///         Header, 14 bytes LITTLE-endian: a 3-byte id <c>0x032897</c> (bytes <c>97 28 03</c>) plus
///         a version byte that must be 1, a u32 <b>total value count</b> (samples summed over all
///         channels, NOT frames), u16 channels, u16 sample rate, and a u16 packing word whose LOW 4
///         bits are the <c>level</c> and HIGH 12 bits the <c>rows</c>. libacm reads the header through
///         its LSB-first bit reader, which is why the nibble split lands that way round:
///         <c>cols = 1 &lt;&lt; level</c>, and a block holds <c>rows * cols</c> values. Retail Fallout
///         uses packing 0x0107 everywhere measured: level 7, cols 128, rows 16, block 2,048.
///     </para>
///     <para>
///         Stream: each block opens with a 4-bit power and a 16-bit step that generate a symmetric
///         amplitude table, then one 5-bit selector per column picks a filler that writes one
///         column of the rows x cols block from the bit stream; the block is then run through the
///         sub-band synthesis (<c>juggle</c>) and shifted right by <c>level</c> on output.
///     </para>
///     <para>
///         ⚠⚠ <b>The header's channel count is "often wrong"</b> (libacm's own words) — and on
///         Fallout it is wrong on EVERY speech and effect file that says 2 (measured 2026-09-07, see
///         <see cref="InterplayAcmChannelProbe" /> for the two outside oracles). Decoding does not
///         depend on the count — it only decides how the value stream is interleaved — so
///         <see cref="Decode" /> emits the values labelled as declared (libacm's
///         <c>force_chans == 0</c>) and attaches the probe; a player wants
///         <see cref="InterplayAcmPcm.AsInferred" />.
///     </para>
///     <para>
///         ⚠ A file's LAST block is usually partial: the header total is not a multiple of the
///         block length, and the final block carries only <c>total mod blockLength</c> values, whose
///         fillers may need up to 8 bits past the end of the file — libacm's reader supplies ONE zero
///         byte there, and this port keeps that rule. ⚑ Measured against FFmpeg 8.1.2's
///         <c>interplayacm</c> over all 5,024 retail files: 4,561 agree sample-for-sample over
///         FFmpeg's whole output; on the other 463 FFmpeg deviates in the last rows of the final
///         block, and on a further 321 it errors on that block and drops it. Appending a single
///         zero byte to the file makes FFmpeg reproduce this port's output exactly on all 784, so
///         those are FFmpeg's missing EOF padding, not a decode difference. ⛔ FFmpeg does NOT
///         "stop one block short" as a rule — it emits the declared total on 4,703 files.
///     </para>
/// </summary>
internal sealed class InterplayAcmFile
{
    /// <summary>The 24-bit file id, <c>ACM_ID</c>: bytes <c>97 28 03</c> little-endian.</summary>
    public const int Signature = 0x032897;

    /// <summary>The only version byte libacm accepts.</summary>
    public const int SupportedVersion = 1;

    /// <summary>Bytes of plain ACM header ahead of the bit stream (<c>ACM_HEADER_LEN</c>).</summary>
    public const int HeaderLength = 14;

    /// <summary>Bytes of the optional WAVC wrapper ahead of the ACM header (<c>WAVC_HEADER_LEN</c>).</summary>
    public const int WavcHeaderLength = 28;

    /// <summary>libacm's cap on the header total (<c>MAX_SAMPLES</c>).</summary>
    public const int MaxSamples = 1 << 30;

    /// <summary>libacm's cap on <c>rows * cols</c> (<c>MAX_BLOCK_LEN</c>).</summary>
    public const int MaxBlockLength = 1024 * 1024;

    /// <summary>The 'WAV' id of a WAVC-wrapped file (<c>WAVC_ID</c>).</summary>
    private const int WavcId = 0x564157;

    /// <summary>libacm's smallest accepted rate; anything lower is "not an ACM".</summary>
    private const int MinimumSampleRate = 4096;

    private const int AmplitudeBufferLength = 0x10000;
    private const int AmplitudeBufferMiddle = 0x8000;

    // static const int map_1bit[] / map_2bit_near[] / map_2bit_far[] / map_3bit[]
    private static readonly int[] Map1Bit = [-1, +1];
    private static readonly int[] Map2BitNear = [-2, -1, +1, +2];
    private static readonly int[] Map2BitFar = [-3, -2, +2, +3];
    private static readonly int[] Map3Bit = [-4, -3, -2, -1, +1, +2, +3, +4];

    private readonly byte[] _bytes;

    private InterplayAcmFile(
        string name,
        byte[] bytes,
        bool wavcFile,
        int totalValues,
        int declaredChannels,
        int sampleRate,
        int level,
        int rows)
    {
        Name = name;
        _bytes = bytes;
        IsWavc = wavcFile;
        TotalValues = totalValues;
        DeclaredChannels = declaredChannels;
        SampleRate = sampleRate;
        Level = level;
        Rows = rows;
        Columns = 1 << level;
        BlockLength = rows * Columns;
    }

    /// <summary>Logical file name, for messages.</summary>
    public string Name { get; }

    /// <summary>True when the ACM header sat behind a 28-byte <c>WAVC</c> wrapper.</summary>
    public bool IsWavc { get; }

    /// <summary>Header total: values summed over channels (<c>total_values</c>), not frames.</summary>
    public int TotalValues { get; }

    /// <summary>
    ///     Channel count as the header states it (<c>acm_channels</c>): 1 or 2 on every retail file.
    ///     <para>
    ///         ⛔ An earlier draft of this comment claimed the field agrees with the data on this
    ///         corpus. REFUTED 2026-09-07: every FO1 speech line (1,646) and every FO2 speech line
    ///         (1,111) says 2 and is mono by transcript speaking rate and by value structure; 439 FO2
    ///         effects say 2 and are mono too. Only the music is stereo. Use
    ///         <see cref="InterplayAcmPcm.Probe" />, never this, to interleave for playback.
    ///     </para>
    /// </summary>
    public int DeclaredChannels { get; }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Packing level (<c>acm_level</c>, low nibble of the packing word); <c>cols = 1 &lt;&lt; level</c>.</summary>
    public int Level { get; }

    /// <summary>Rows per block (<c>acm_rows</c>, high 12 bits of the packing word).</summary>
    public int Rows { get; }

    /// <summary>Columns per block (<c>acm_cols</c>).</summary>
    public int Columns { get; }

    /// <summary>Values per block: <c>rows * cols</c> (<c>block_len</c>).</summary>
    public int BlockLength { get; }

    /// <summary>Samples per channel, using the declared channel count (<c>acm_pcm_total</c>).</summary>
    public int SamplesPerChannel => TotalValues / DeclaredChannels;

    /// <summary>Playback length in seconds, using the declared channel count.</summary>
    public double DurationSeconds => (double)SamplesPerChannel / SampleRate;

    /// <summary>Whole blocks plus, when the total is not a multiple, one partial block.</summary>
    public int BlockCount => (TotalValues + BlockLength - 1) / BlockLength;

    /// <summary>Values carried by the final block: <c>total mod blockLength</c>, or a full block when that is 0.</summary>
    public int LastBlockValues => TotalValues % BlockLength == 0 ? BlockLength : TotalValues % BlockLength;

    /// <summary>
    ///     Content probe: the 3-byte id and version 1, or the <c>WAVC</c> wrapper. Cheap enough to
    ///     run on every candidate; the header validation proper happens in <see cref="TryParse" />.
    /// </summary>
    public static bool IsAcm(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= HeaderLength && ReadId(bytes) == Signature && bytes[3] == SupportedVersion)
        {
            return true;
        }

        return bytes.Length >= WavcHeaderLength + HeaderLength
               && ReadId(bytes) == WavcId
               && bytes[3] == (byte)'C'
               && ReadId(bytes[WavcHeaderLength..]) == Signature
               && bytes[WavcHeaderLength + 3] == SupportedVersion;
    }

    /// <summary>Reads the header, throwing when the bytes are not an ACM libacm would open.</summary>
    public static InterplayAcmFile Parse(byte[] bytes, string name)
    {
        if (!TryParse(bytes, name, out var file, out var error))
        {
            throw new InvalidDataException(error);
        }

        return file;
    }

    /// <summary>
    ///     Reads the header (<c>read_header</c> + the validation in <c>acm_open_decoder</c>), reporting why rather than
    ///     throwing.
    /// </summary>
    public static bool TryParse(byte[] bytes, string name, out InterplayAcmFile file, out string error)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        file = null!;
        if (bytes.Length < HeaderLength)
        {
            // The reader's appended zero byte would otherwise let a 13-byte header "parse".
            error = $"{name}: {bytes.Length} bytes is too short for the {HeaderLength}-byte ACM header.";
            return false;
        }

        // read_header() goes through the bit reader; the header is little-endian and byte
        // aligned so that is the same as plain LE field reads, but keeping the reader on it
        // is what makes the level/rows nibble split come out the way libacm derives it.
        var reader = new BitReader(bytes);
        var wavc = false;
        int tmp;
        try
        {
            tmp = reader.GetBits(24);
            if (tmp == WavcId)
            {
                if (reader.GetBits(8) != 'C' || !ReadWavcHeader(reader))
                {
                    error = $"{name}: WAVC wrapper is not the expected 'WAVC' + 'V1.00' + 28 shape.";
                    return false;
                }

                wavc = true;
                tmp = reader.GetBits(24);
            }

            if (tmp != Signature)
            {
                error = $"{name}: id is 0x{tmp:X6}, not the ACM id 0x{Signature:X6}.";
                return false;
            }

            var version = reader.GetBits(8);
            if (version != SupportedVersion)
            {
                error = $"{name}: ACM version {version}; only version {SupportedVersion} is defined.";
                return false;
            }

            var totalValues = reader.GetBits(16);
            totalValues += reader.GetBits(16) << 16;
            if (totalValues == 0 || totalValues > MaxSamples)
            {
                error = $"{name}: header total of {totalValues} values is outside 1..{MaxSamples}.";
                return false;
            }

            var channels = reader.GetBits(16);
            if (channels is < 1 or > 2)
            {
                error = $"{name}: header declares {channels} channels; libacm accepts 1 or 2.";
                return false;
            }

            var rate = reader.GetBits(16);
            if (rate < MinimumSampleRate)
            {
                error = $"{name}: sample rate {rate} Hz is below libacm's {MinimumSampleRate} Hz floor.";
                return false;
            }

            var level = reader.GetBits(4);
            var rows = reader.GetBits(12);
            if (rows == 0)
            {
                error = $"{name}: packing word declares 0 rows.";
                return false;
            }

            // acm_open_decoder(): calculate blocks, validate.
            var cols = 1 << level;
            var wrapbufLen = 2 * cols - 2;
            var blockLen = rows * cols;
            if (wrapbufLen == 0)
            {
                error = $"{name}: level 0 gives a 1-column block, which libacm rejects (no wrap buffer).";
                return false;
            }

            if (blockLen > MaxBlockLength)
            {
                error = $"{name}: block of {rows} x {cols} = {blockLen} values exceeds {MaxBlockLength}.";
                return false;
            }

            file = new InterplayAcmFile(name, bytes, wavc, totalValues, channels, rate, level, rows);
            error = string.Empty;
            return true;
        }
        catch (UnexpectedEofException)
        {
            error = $"{name}: {bytes.Length} bytes is too short for an ACM header.";
            return false;
        }
    }

    /// <summary>
    ///     Decodes the whole stream to interleaved signed 16-bit PCM — <c>acm_read_loop</c> over
    ///     every block, converting with <c>out_s16le</c>'s arithmetic (a right shift by
    ///     <see cref="Level" /> and a wrap to 16 bits, not a clamp).
    /// </summary>
    /// <param name="channels">
    ///     Channel count to label the result with; null uses <see cref="DeclaredChannels" />
    ///     (<c>force_chans == 0</c>). Pass 2 to apply libacm's <c>force_chans == -1</c> quirk. The
    ///     probe is measured either way; <see cref="InterplayAcmPcm.AsInferred" /> re-labels by it.
    /// </param>
    /// <exception cref="InvalidDataException">The stream is corrupt or ends inside a block.</exception>
    public InterplayAcmPcm Decode(int? channels = null)
    {
        var emitChannels = channels ?? DeclaredChannels;
        if (emitChannels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "ACM streams are mono or stereo.");
        }

        var decoder = new Decoder(this);
        var output = new short[TotalValues];
        var streamPos = 0;

        // acm_read(): decode a block, copy out what the header total still allows.
        while (streamPos < TotalValues)
        {
            if (!decoder.DecodeBlock())
            {
                // ACM_EXPECTED_EOF: the stream ended between blocks before the header total.
                throw new InvalidDataException(
                    $"{Name}: stream ended after {streamPos} of {TotalValues} declared values " +
                    $"({streamPos / (double)BlockLength:F1} of {BlockCount} blocks).");
            }

            var numWords = Math.Min(BlockLength, TotalValues - streamPos);
            decoder.OutputS16(output.AsSpan(streamPos, numWords));
            streamPos += numWords;
        }

        var probe = InterplayAcmChannelProbe.Measure(output, DeclaredChannels);
        return new InterplayAcmPcm(output, emitChannels, SampleRate, decoder.PaddingBitsUsed, decoder.TrailingBits,
            probe);
    }

    /// <summary>Little-endian 24-bit read of the id field.</summary>
    private static int ReadId(ReadOnlySpan<byte> bytes)
    {
        return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
    }

    /// <summary>
    ///     <c>read_wavc_header</c>: <c>'V1.0'</c> then raw/compressed sizes and a WAV-shaped tail;
    ///     libacm only insists on the 'V1.0' text and the magic 28 at word 6.
    /// </summary>
    private static bool ReadWavcHeader(BitReader reader)
    {
        Span<int> buf = stackalloc int[12];
        for (var i = 0; i < 12; i++)
        {
            buf[i] = reader.GetBits(16);
        }

        // memcmp(buf, expect, 4): the first two u16 words, 'V1' and '.0'.
        if (buf[0] != 0x3156 || buf[1] != 0x302E)
        {
            return false;
        }

        return buf[6] == 28;
    }

    /// <summary>
    ///     Raised where libacm returns <c>ACM_ERR_UNEXPECTED_EOF</c>. Between blocks it is the
    ///     clean end of the stream (<c>ACM_EXPECTED_EOF</c>); inside one it is an error.
    /// </summary>
    // This private sentinel identifies libacm's EOF result; callers never supply a message or inner exception.
#pragma warning disable RCS1194 // Implement exception constructors
    private sealed class UnexpectedEofException : EndOfStreamException
    {
        public UnexpectedEofException() : base("Unexpected end of ACM stream.")
        {
        }
    }
#pragma warning restore RCS1194

    /// <summary>
    ///     The LSB-first bit reader of decode.c (<c>load_buf</c> / <c>load_bits</c> /
    ///     <c>get_bits_reload</c> / <c>GET_BITS</c>). The whole file is in memory, so the 64 KB
    ///     window collapses to a position; what is kept exactly is libacm's EOF rule — at end of
    ///     file ONE zero byte is appended to the stream, and reading beyond that is
    ///     <c>ACM_ERR_UNEXPECTED_EOF</c>. Bit counts are at most 31 per call, as in C.
    /// </summary>
    private sealed class BitReader
    {
        private readonly byte[] _bytes;
        private int _bitAvail;
        private uint _bitData;
        private bool _fileEof;

        public BitReader(byte[] bytes)
        {
            _bytes = bytes;
        }

        /// <summary>Byte offset of the next unread byte, for messages.</summary>
        public int Position { get; private set; }

        /// <summary>Bits of the appended zero byte already handed out (0 until the file is exhausted).</summary>
        public int PaddingBitsUsed => _fileEof ? Math.Max(0, 8 - _bitAvail) : 0;

        /// <summary>Bits of real file data not yet consumed.</summary>
        public int TrailingBits => (_bytes.Length - Position) * 8 + (_fileEof ? Math.Max(0, _bitAvail - 8) : _bitAvail);

        // GET_BITS: the fast path is inline; the slow path is get_bits_reload().
        public int GetBits(int bits)
        {
            if (_bitAvail >= bits)
            {
                var value = (int)(_bitData & ((1u << bits) - 1));
                _bitData >>= bits;
                _bitAvail -= bits;
                return value;
            }

            return GetBitsReload(bits);
        }

        private int GetBitsReload(int bits)
        {
            var data = _bitData;
            var got = _bitAvail;
            bits -= got;

            uint bData;
            int bAvail;
            if (_bytes.Length - Position >= 4)
            {
                bData = (uint)(_bytes[Position] | (_bytes[Position + 1] << 8) | (_bytes[Position + 2] << 16) |
                               (_bytes[Position + 3] << 24));
                Position += 4;
                bAvail = 32;
            }
            else
            {
                // load_bits(): the 0-3 byte tail, then load_buf() hits EOF and adds a single zero
                // byte, and the fill loop takes that too.
                bData = 0;
                bAvail = 0;
                while (Position < _bytes.Length)
                {
                    bData |= (uint)_bytes[Position++] << bAvail;
                    bAvail += 8;
                }

                if (!_fileEof)
                {
                    _fileEof = true;
                    bAvail += 8;
                }

                if (bAvail < bits)
                {
                    throw new UnexpectedEofException();
                }
            }

            data |= (bData & ((1u << bits) - 1)) << got;
            _bitData = bData >> bits;
            _bitAvail = bAvail - bits;
            return (int)data;
        }
    }

    /// <summary>
    ///     The per-stream state of <c>ACMStream</c> that <c>decode_block</c> works on: the block,
    ///     the wrap buffer carried between blocks, the amplitude table, and the filler dispatch.
    /// </summary>
    private sealed class Decoder
    {
        private readonly int[] _ampBuf;
        private readonly int[] _block;

        private readonly InterplayAcmFile _file;
        private readonly Filler[] _fillerList;
        private readonly BitReader _reader;
        private readonly int[] _wrapBuf;

        public Decoder(InterplayAcmFile file)
        {
            _file = file;
            _reader = new BitReader(file._bytes);

            // acm_open_decoder(): consume the header(s) so the reader sits on the first block.
            var headerBits = 8 * (file.IsWavc ? WavcHeaderLength + HeaderLength : HeaderLength);
            while (headerBits > 0)
            {
                var step = Math.Min(24, headerBits);
                _reader.GetBits(step);
                headerBits -= step;
            }

            _block = new int[file.BlockLength];
            _wrapBuf = new int[2 * file.Columns - 2];
            _ampBuf = new int[AmplitudeBufferLength];

            // static const filler_t filler_list[]
            _fillerList =
            [
                FillZero, FillBad, FillBad, FillLinear, /* 0..3 */
                FillLinear, FillLinear, FillLinear, FillLinear, /* 4..7 */
                FillLinear, FillLinear, FillLinear, FillLinear, /* 8..11 */
                FillLinear, FillLinear, FillLinear, FillLinear, /* 12..15 */
                FillLinear, FillK13, FillK12, FillT15, /* 16..19 */
                FillK24, FillK23, FillT27, FillK35, /* 20..23 */
                FillK34, FillBad, FillK45, FillK44, /* 24..27 */
                FillBad, FillT37, FillBad, FillBad /* 28..31 */
            ];
        }

        /// <summary>See <see cref="InterplayAcmPcm.PaddingBitsUsed" />.</summary>
        public int PaddingBitsUsed => _reader.PaddingBitsUsed;

        /// <summary>See <see cref="InterplayAcmPcm.TrailingBits" />.</summary>
        public int TrailingBits => _reader.TrailingBits;

        /// <summary>
        ///     <c>decode_block</c>: read the 4-bit power and 16-bit step, generate the amplitude
        ///     table, fill every column, juggle. Returns false on <c>ACM_EXPECTED_EOF</c> — the
        ///     stream ended cleanly before this block's header or a column selector.
        /// </summary>
        public bool DecodeBlock()
        {
            int pwr;
            int val;
            try
            {
                pwr = _reader.GetBits(4);
                val = _reader.GetBits(16);
            }
            catch (UnexpectedEofException)
            {
                return false;
            }

            // generate tables
            var count = 1 << pwr;
            var x = 0;
            for (var i = 0; i < count; i++)
            {
                _ampBuf[AmplitudeBufferMiddle + i] = x;
                x += val;
            }

            x = -val;
            for (var i = 1; i <= count; i++)
            {
                _ampBuf[AmplitudeBufferMiddle - i] = x;
                x -= val;
            }

            if (!FillBlock())
            {
                return false;
            }

            JuggleBlock();
            return true;
        }

        /// <summary><c>out_s16le</c>: shift by level, wrap to 16 bits.</summary>
        public void OutputS16(Span<short> dst)
        {
            var level = _file.Level;
            for (var i = 0; i < dst.Length; i++)
            {
                dst[i] = unchecked((short)(_block[i] >> level));
            }
        }

        /// <summary><c>fill_block</c>: one 5-bit selector per column, dispatched through the table.</summary>
        private bool FillBlock()
        {
            for (var i = 0; i < _file.Columns; i++)
            {
                int ind;
                try
                {
                    ind = _reader.GetBits(5);
                }
                catch (UnexpectedEofException)
                {
                    // GET_BITS_EXPECT_EOF
                    return false;
                }

                try
                {
                    _fillerList[ind](ind, i);
                }
                catch (UnexpectedEofException ex)
                {
                    throw new InvalidDataException(
                        $"{_file.Name}: stream ended inside a block (filler {ind}, column {i}, byte {_reader.Position}).",
                        ex);
                }
            }

            return true;
        }

        // #define set_pos(acm, r, c, idx): block[(r << level) + c] = midbuf[idx]
        private void SetPos(int r, int c, int idx)
        {
            _block[(r << _file.Level) + c] = _ampBuf[AmplitudeBufferMiddle + idx];
        }

        /************ Fillers **********/

        private void FillZero(int ind, int col)
        {
            for (var i = 0; i < _file.Rows; i++)
            {
                SetPos(i, col, 0);
            }
        }

        private void FillBad(int ind, int col)
        {
            // corrupt block?
            throw new InvalidDataException(
                $"{_file.Name}: column {col} selects undefined filler {ind} at byte {_reader.Position}.");
        }

        private void FillLinear(int ind, int col)
        {
            var middle = 1 << (ind - 1);
            for (var i = 0; i < _file.Rows; i++)
            {
                var b = _reader.GetBits(ind);
                SetPos(i, col, b - middle);
            }
        }

        private void FillK13(int ind, int col)
        {
            var rows = _file.Rows;
            var i = 0;
            while (i < rows)
            {
                var b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 0 */
                    SetPos(i++, col, 0);
                    if (i >= rows)
                    {
                        break;
                    }

                    SetPos(i++, col, 0);
                    continue;
                }

                b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 1, 0 */
                    SetPos(i++, col, 0);
                    continue;
                }

                /* 1, 1, ? */
                b = _reader.GetBits(1);
                SetPos(i++, col, Map1Bit[b]);
            }
        }

        private void FillK12(int ind, int col)
        {
            var rows = _file.Rows;
            for (var i = 0; i < rows; i++)
            {
                var b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 0 */
                    SetPos(i, col, 0);
                    continue;
                }

                /* 1, ? */
                b = _reader.GetBits(1);
                SetPos(i, col, Map1Bit[b]);
            }
        }

        private void FillK24(int ind, int col)
        {
            var rows = _file.Rows;
            var i = 0;
            while (i < rows)
            {
                var b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 0 */
                    SetPos(i++, col, 0);
                    if (i >= rows)
                    {
                        break;
                    }

                    SetPos(i++, col, 0);
                    continue;
                }

                b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 1, 0 */
                    SetPos(i++, col, 0);
                    continue;
                }

                /* 1, 1, ?, ? */
                b = _reader.GetBits(2);
                SetPos(i++, col, Map2BitNear[b]);
            }
        }

        private void FillK23(int ind, int col)
        {
            var rows = _file.Rows;
            for (var i = 0; i < rows; i++)
            {
                var b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 0 */
                    SetPos(i, col, 0);
                    continue;
                }

                /* 1, ?, ? */
                b = _reader.GetBits(2);
                SetPos(i, col, Map2BitNear[b]);
            }
        }

        private void FillK35(int ind, int col)
        {
            var rows = _file.Rows;
            var i = 0;
            while (i < rows)
            {
                var b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 0 */
                    SetPos(i++, col, 0);
                    if (i >= rows)
                    {
                        break;
                    }

                    SetPos(i++, col, 0);
                    continue;
                }

                b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 1, 0 */
                    SetPos(i++, col, 0);
                    continue;
                }

                b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 1, 1, 0, ? */
                    b = _reader.GetBits(1);
                    SetPos(i++, col, Map1Bit[b]);
                    continue;
                }

                /* 1, 1, 1, ?, ? */
                b = _reader.GetBits(2);
                SetPos(i++, col, Map2BitFar[b]);
            }
        }

        private void FillK34(int ind, int col)
        {
            var rows = _file.Rows;
            for (var i = 0; i < rows; i++)
            {
                var b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 0 */
                    SetPos(i, col, 0);
                    continue;
                }

                b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 1, 0, ? */
                    b = _reader.GetBits(1);
                    SetPos(i, col, Map1Bit[b]);
                    continue;
                }

                /* 1, 1, ?, ? */
                b = _reader.GetBits(2);
                SetPos(i, col, Map2BitFar[b]);
            }
        }

        private void FillK45(int ind, int col)
        {
            var rows = _file.Rows;
            var i = 0;
            while (i < rows)
            {
                var b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 0 */
                    SetPos(i++, col, 0);
                    if (i >= rows)
                    {
                        break;
                    }

                    SetPos(i++, col, 0);
                    continue;
                }

                b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 1, 0 */
                    SetPos(i++, col, 0);
                    continue;
                }

                /* 1, 1, ?, ?, ? */
                b = _reader.GetBits(3);
                SetPos(i++, col, Map3Bit[b]);
            }
        }

        private void FillK44(int ind, int col)
        {
            var rows = _file.Rows;
            for (var i = 0; i < rows; i++)
            {
                var b = _reader.GetBits(1);
                if (b == 0)
                {
                    /* 0 */
                    SetPos(i, col, 0);
                    continue;
                }

                /* 1, ?, ?, ? */
                b = _reader.GetBits(3);
                SetPos(i, col, Map3Bit[b]);
            }
        }

        private void FillT15(int ind, int col)
        {
            var rows = _file.Rows;
            var i = 0;
            while (i < rows)
            {
                /* b = (x1) + (x2 * 3) + (x3 * 9) */
                var b = _reader.GetBits(5);
                if (b >= 3 * 3 * 3)
                {
                    throw new InvalidDataException(
                        $"{_file.Name}: t15 code {b} is out of range at byte {_reader.Position}.");
                }

                var n1 = b % 3 - 1;
                var tmp = b / 3;
                var n2 = tmp % 3 - 1;
                var n3 = tmp / 3 - 1;

                SetPos(i++, col, n1);
                if (i >= rows)
                {
                    break;
                }

                SetPos(i++, col, n2);
                if (i >= rows)
                {
                    break;
                }

                SetPos(i++, col, n3);
            }
        }

        private void FillT27(int ind, int col)
        {
            var rows = _file.Rows;
            var i = 0;
            while (i < rows)
            {
                /* b = (x1) + (x2 * 5) + (x3 * 25) */
                var b = _reader.GetBits(7);
                if (b >= 5 * 5 * 5)
                {
                    throw new InvalidDataException(
                        $"{_file.Name}: t27 code {b} is out of range at byte {_reader.Position}.");
                }

                var n1 = b % 5 - 2;
                var tmp = b / 5;
                var n2 = tmp % 5 - 2;
                var n3 = tmp / 5 - 2;

                SetPos(i++, col, n1);
                if (i >= rows)
                {
                    break;
                }

                SetPos(i++, col, n2);
                if (i >= rows)
                {
                    break;
                }

                SetPos(i++, col, n3);
            }
        }

        private void FillT37(int ind, int col)
        {
            var rows = _file.Rows;
            var i = 0;
            while (i < rows)
            {
                /* b = (x1) + (x2 * 11) */
                var b = _reader.GetBits(7);
                if (b >= 11 * 11)
                {
                    throw new InvalidDataException(
                        $"{_file.Name}: t37 code {b} is out of range at byte {_reader.Position}.");
                }

                var n1 = b % 11 - 5;
                var n2 = b / 11 - 5;

                SetPos(i++, col, n1);
                if (i >= rows)
                {
                    break;
                }

                SetPos(i++, col, n2);
            }
        }

        /**********************************************
         * Decompress code
         **********************************************/

        /// <summary>
        ///     <c>juggle</c>: one butterfly pass over <paramref name="subCount" /> rows of
        ///     <paramref name="subLen" /> values, carrying the last two rows of each column in the
        ///     wrap buffer for the next block. C does this in unsigned arithmetic; the wrapping
        ///     matches unchecked int.
        /// </summary>
        private void Juggle(int wrapP, int blockP, int subLen, int subCount)
        {
            var wrap = _wrapBuf;
            var block = _block;
            for (var i = 0; i < subLen; i++)
            {
                var p = blockP;
                var r0 = wrap[wrapP];
                var r1 = wrap[wrapP + 1];
                for (var j = 0; j < subCount / 2; j++)
                {
                    var r2 = block[p];
                    block[p] = unchecked(r1 * 2 + r0 + r2);
                    p += subLen;
                    var r3 = block[p];
                    block[p] = unchecked(r2 * 2 - (r1 + r3));
                    p += subLen;
                    r0 = r2;
                    r1 = r3;
                }

                wrap[wrapP++] = r0;
                wrap[wrapP++] = r1;
                blockP++;
            }
        }

        /// <summary><c>juggle_block</c>: the sub-band synthesis, applied in strips of <c>step_subcount</c> rows.</summary>
        private void JuggleBlock()
        {
            var level = _file.Level;

            /* juggle only if subblock_len > 1 */
            if (level == 0)
            {
                return;
            }

            /* 2048 / subblock_len */
            var stepSubcount = level > 9 ? 1 : (2048 >> level) - 2;

            /* Apply juggle()  (rows)x(cols)
             * from (step_subcount * 2)            x (subblock_len/2)
             * to   (step_subcount * subblock_len) x (1)
             */
            var todoCount = _file.Rows;
            var blockP = 0;
            while (true)
            {
                var wrapP = 0;
                var subCount = Math.Min(stepSubcount, todoCount);

                var subLen = _file.Columns / 2;
                subCount *= 2;

                Juggle(wrapP, blockP, subLen, subCount);
                wrapP += subLen * 2;

                for (int i = 0, p = blockP; i < subCount; i++)
                {
                    _block[p]++;
                    p += subLen;
                }

                while (subLen > 1)
                {
                    subLen /= 2;
                    subCount *= 2;
                    Juggle(wrapP, blockP, subLen, subCount);
                    wrapP += subLen * 2;
                }

                if (todoCount <= stepSubcount)
                {
                    break;
                }

                todoCount -= stepSubcount;
                blockP += stepSubcount << level;
            }
        }

        private delegate void Filler(int ind, int col);
    }
}
