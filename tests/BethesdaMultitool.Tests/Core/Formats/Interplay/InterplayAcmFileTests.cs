using BethesdaMultitool.Core.Formats.Interplay;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Interplay;

/// <summary>
///     Synthetic vectors for the libacm port. Every expectation here was derived BY HAND from
///     decode.c (libacm 1.5) — the header nibble split from <c>read_header</c>'s GET_BITS order,
///     the PCM from walking <c>decode_block</c> / <c>juggle</c> on paper — never by running the
///     C# code. The two smallest streams are pinned as literal bytes packed by hand; the larger
///     filler vectors use <see cref="BitWriter" /> to pack the same LSB-first stream, because
///     LSB-first packing is not the code under test and the expected samples still come from
///     the paper walk.
///     <para>
///         The paper walk for a 2-column block (level 1) with column 1 zero-filled: juggle runs
///         one pass with <c>sub_len = 1</c>, <c>r1</c> stays 0, so <c>block[2j] = a[j-1] + a[j]</c>
///         (with <c>a[-1] = wrap = 0</c>) and <c>block[2j+1] = 2 * a[j]</c>; every value is then
///         incremented and shifted right by 1. The odd outputs are therefore the column-0
///         amplitudes exactly, which is what lets the map tables be pinned as literals.
///     </para>
/// </summary>
public sealed class InterplayAcmFileTests
{
    private static byte[] Header(uint totalValues, ushort channels, ushort rate, ushort packing)
    {
        var b = new List<byte> { 0x97, 0x28, 0x03, 0x01 };
        b.AddRange(BitConverter.GetBytes(totalValues));
        b.AddRange(BitConverter.GetBytes(channels));
        b.AddRange(BitConverter.GetBytes(rate));
        b.AddRange(BitConverter.GetBytes(packing));
        return [.. b];
    }

    private static byte[] Concat(params byte[][] parts)
    {
        return parts.SelectMany(p => p).ToArray();
    }

    // ---------------------------------------------------------------- header

    [Theory]
    [InlineData((ushort)0x0107, 7, 16, 128, 2048)] // Fallout's usual packing word
    [InlineData((ushort)0x0108, 8, 16, 256, 4096)] // Fallout's other one
    [InlineData((ushort)0x2A53, 3, 677, 8, 5416)] // level = low nibble, rows = high 12 bits
    [InlineData((ushort)0x0011, 1, 1, 2, 2)]
    public void Parse_SplitsThePackingWordTheWayLibacmReadsIt(ushort packing, int level, int rows, int cols,
        int blockLength)
    {
        // read_header(): GET_BITS(level, 4) then GET_BITS(rows, 12) from an LSB-first reader,
        // so the LOW nibble is the level and the HIGH 12 bits the rows.
        var acm = InterplayAcmFile.Parse(Header(1, 1, 22050, packing), "x.acm");

        Assert.Equal(level, acm.Level);
        Assert.Equal(rows, acm.Rows);
        Assert.Equal(cols, acm.Columns);
        Assert.Equal(blockLength, acm.BlockLength);
    }

    [Fact]
    public void Parse_ReadsTheLittleEndianHeaderFields()
    {
        // total 0x00A3C1F2 = 10,732,018 values; 2 channels; 22,050 Hz.
        var bytes = new byte[] { 0x97, 0x28, 0x03, 0x01, 0xF2, 0xC1, 0xA3, 0x00, 0x02, 0x00, 0x22, 0x56, 0x07, 0x01 };
        var acm = InterplayAcmFile.Parse(bytes, "01HUB.ACM");

        Assert.Equal(10_732_018, acm.TotalValues);
        Assert.Equal(2, acm.DeclaredChannels);
        Assert.Equal(22050, acm.SampleRate);
        Assert.Equal(5_366_009, acm.SamplesPerChannel);
        Assert.Equal(5_366_009 / 22050.0, acm.DurationSeconds, 6);
        Assert.False(acm.IsWavc);

        // 10,732,018 = 5,240 * 2,048 + 498, so 5,241 blocks with 498 values in the last one.
        Assert.Equal(5241, acm.BlockCount);
        Assert.Equal(498, acm.LastBlockValues);
    }

    [Fact]
    public void Parse_AWholeNumberOfBlocksHasAFullLastBlock()
    {
        var acm = InterplayAcmFile.Parse(Header(4096, 1, 22050, 0x0107), "x.acm");
        Assert.Equal(2, acm.BlockCount);
        Assert.Equal(2048, acm.LastBlockValues);
    }

    [Theory]
    [InlineData(new byte[] { 0x98, 0x28, 0x03, 0x01, 1, 0, 0, 0, 1, 0, 0x22, 0x56, 0x07, 0x01 }, "id")] // wrong id
    [InlineData(new byte[] { 0x97, 0x28, 0x03, 0x02, 1, 0, 0, 0, 1, 0, 0x22, 0x56, 0x07, 0x01 },
        "version")] // version 2
    [InlineData(new byte[] { 0x97, 0x28, 0x03, 0x01, 0, 0, 0, 0, 1, 0, 0x22, 0x56, 0x07, 0x01 }, "total")] // zero total
    [InlineData(new byte[] { 0x97, 0x28, 0x03, 0x01, 1, 0, 0, 0, 3, 0, 0x22, 0x56, 0x07, 0x01 },
        "channels")] // 3 channels
    [InlineData(new byte[] { 0x97, 0x28, 0x03, 0x01, 1, 0, 0, 0, 1, 0, 0xFF, 0x0F, 0x07, 0x01 }, "rate")] // 4095 Hz
    [InlineData(new byte[] { 0x97, 0x28, 0x03, 0x01, 1, 0, 0, 0, 1, 0, 0x22, 0x56, 0x07, 0x00 }, "rows")] // 0 rows
    [InlineData(new byte[] { 0x97, 0x28, 0x03, 0x01, 1, 0, 0, 0, 1, 0, 0x22, 0x56, 0x10, 0x00 },
        "level 0")] // level 0: no wrap buffer
    [InlineData(new byte[] { 0x97, 0x28, 0x03, 0x01, 1, 0, 0, 0, 1, 0, 0x22, 0x56, 0x1F, 0x02 },
        "exceeds")] // 33 x 32768 > 1 MiB
    [InlineData(new byte[] { 0x97, 0x28, 0x03, 0x01, 1, 0, 0, 0, 1, 0, 0x22, 0x56, 0x07 }, "too short")] // 13 bytes
    public void Parse_RejectsWhatLibacmRejects(byte[] bytes, string reason)
    {
        var ok = InterplayAcmFile.TryParse(bytes, "bad.acm", out _, out var error);

        Assert.False(ok);
        Assert.Contains(reason, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_AcceptsABlockOfExactlyOneMebibyte()
    {
        // MAX_BLOCK_LEN is checked with '>', so 32 rows x 32,768 cols = 1,048,576 passes.
        var acm = InterplayAcmFile.Parse(Header(1, 1, 22050, 0x020F), "x.acm");
        Assert.Equal(1 << 20, acm.BlockLength);
    }

    [Fact]
    public void IsAcm_ChecksTheIdAndVersionOrTheWavcWrapper()
    {
        Assert.True(InterplayAcmFile.IsAcm(Header(1, 1, 22050, 0x0107)));
        Assert.False(InterplayAcmFile.IsAcm(new byte[]
            { 0x97, 0x28, 0x03, 0x02, 1, 0, 0, 0, 1, 0, 0x22, 0x56, 0x07, 0x01 }));
        Assert.False(InterplayAcmFile.IsAcm("RIFF....WAVE"u8.ToArray()));
        Assert.False(InterplayAcmFile.IsAcm(Header(1, 1, 22050, 0x0107).AsSpan(0, 13)));

        // 'WAVC' + 'V1.00' + raw + compressed + 12 bytes, then the ACM header.
        var wavc = new List<byte>("WAVCV1.00"u8.ToArray());
        wavc.AddRange(new byte[19]);
        Assert.Equal(InterplayAcmFile.WavcHeaderLength, wavc.Count);
        wavc.AddRange(Header(1, 1, 22050, 0x0107));
        Assert.True(InterplayAcmFile.IsAcm(wavc.ToArray()));
    }

    // ---------------------------------------------------------------- hand-packed streams

    [Fact]
    public void Decode_OneBlockPackedByHand()
    {
        // level 1 (2 cols), 1 row, 2 values. Stream after the header, LSB-first:
        //   pwr = 2 (4 bits)  -> midbuf[-4..3] = -400,-300,-200,-100,0,100,200,300
        //   val = 100 (16 bits)
        //   col 0: ind 3 = linear 3-bit, code 6 -> midbuf[6 - 4] = 200
        //   col 1: ind 18 = k12, bits 1,1 -> map_1bit[1] = +1 -> midbuf[1] = 100
        // juggle (sub_len 1, sub_count 2, wrap 0,0): block = (200, 2*200 - 100) then +1 each
        //   = (201, 301); >> 1 = (100, 150).
        // Packed by hand: 42 06 30 2C 07.
        var stream = new byte[] { 0x42, 0x06, 0x30, 0x2C, 0x07 };
        var acm = InterplayAcmFile.Parse(Concat(Header(2, 1, 22050, 0x0011), stream), "hand1.acm");

        var pcm = acm.Decode();

        Assert.Equal(new short[] { 100, 150 }, pcm.Samples);
        Assert.Equal(1, pcm.Channels);
        Assert.Equal(22050, pcm.SampleRate);
        Assert.Equal(2, pcm.FrameCount);
    }

    [Fact]
    public void Decode_TwoBlocksPackedByHandCarryTheWrapBuffer()
    {
        // level 1, 2 rows, total 8 = two blocks of 4.
        // Block 1: pwr 2, val 10 -> midbuf[k] = 10k.
        //   col 0: ind 17 = k13, one 0 bit -> rows 0 and 1 both 0
        //   col 1: ind 3 = linear, codes 7 and 0 -> midbuf[3] = 30, midbuf[-4] = -40
        //   block = (0, 30, 0, -40); juggle: (0, -30, 60, 10), wrap = (0, -40); +1 -> (1, -29, 61, 11)
        //   >> 1 (arithmetic, so -29 >> 1 = -15) -> 0, -15, 30, 5
        // Block 2: pwr 2, val 10, both columns ind 0 (zero filler) -> block all 0.
        //   juggle with wrap (0, -40): block[0] = 2*(-40) + 0 = -80, block[1] = 0 - (-40) = 40,
        //   rest 0; +1 -> (-79, 41, 1, 1); >> 1 -> -40, 20, 0, 0.
        // Packed by hand: A2 00 10 8D 43 14 00 00 00.
        var stream = new byte[] { 0xA2, 0x00, 0x10, 0x8D, 0x43, 0x14, 0x00, 0x00, 0x00 };
        var acm = InterplayAcmFile.Parse(Concat(Header(8, 1, 22050, 0x0021), stream), "hand2.acm");

        var pcm = acm.Decode();

        Assert.Equal(new short[] { 0, -15, 30, 5, -40, 20, 0, 0 }, pcm.Samples);
    }

    [Fact]
    public void Decode_StopsAtTheHeaderTotalInsideTheLastBlock()
    {
        // Same first block as above but the header promises only 3 of its 4 values.
        var stream = new byte[] { 0xA2, 0x00, 0x10, 0x8D, 0x03 };
        var acm = InterplayAcmFile.Parse(Concat(Header(3, 1, 22050, 0x0021), stream), "hand3.acm");

        Assert.Equal(new short[] { 0, -15, 30 }, acm.Decode().Samples);
    }

    [Fact]
    public void Decode_ReportsAStreamThatEndsBeforeTheHeaderTotal()
    {
        // One block of 4 values, header claims 8: the second block header hits libacm's EOF
        // (3 spare bits + the single appended zero byte cannot supply a 16-bit step).
        var stream = new byte[] { 0xA2, 0x00, 0x10, 0x8D, 0x03 };
        var acm = InterplayAcmFile.Parse(Concat(Header(8, 1, 22050, 0x0021), stream), "short.acm");

        var error = Assert.Throws<InvalidDataException>(() => acm.Decode());
        Assert.Contains("after 4 of 8", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- amplitude maps from decode.c

    /// <summary>
    ///     One level-1 block whose column 0 is filled by <paramref name="fill" /> and column 1 by
    ///     the zero filler, with pwr 3 and val 1 so <c>midbuf[k] == k</c> and the column values are
    ///     the map entries themselves.
    /// </summary>
    private static InterplayAcmFile TwoColumnBlock(int rows, Action<BitWriter> fill)
    {
        var w = new BitWriter().Add(3, 4).Add(1, 16);
        fill(w);
        w.Add(0, 5); // column 1: f_zero
        var packing = (ushort)((rows << 4) | 1);
        return InterplayAcmFile.Parse(Concat(Header((uint)(rows * 2), 1, 22050, packing), w.ToArray()), "map.acm");
    }

    /// <summary>Odd outputs are the column-0 amplitudes; even ones are <c>(a[j-1] + a[j] + 1) >> 1</c>.</summary>
    private static short[] Expected(params int[] a)
    {
        var result = new short[a.Length * 2];
        var previous = 0;
        for (var j = 0; j < a.Length; j++)
        {
            result[2 * j] = (short)((previous + a[j] + 1) >> 1);
            result[2 * j + 1] = (short)a[j];
            previous = a[j];
        }

        return result;
    }

    [Fact]
    public void Decode_K44UsesMap3Bit()
    {
        // static const int map_3bit[] = { -4, -3, -2, -1, +1, +2, +3, +4 };  (decode.c)
        var acm = TwoColumnBlock(8, w =>
        {
            w.Add(27, 5); // f_k44: "1, ?, ?, ?" per row
            for (var code = 0; code < 8; code++)
            {
                w.Add(1, 1).Add(code, 3);
            }
        });

        // Paper walk, written out: even outputs are (a[j-1] + a[j] + 1) >> 1, odd ones a[j].
        var samples = acm.Decode().Samples;
        Assert.Equal(new short[] { -2, -4, -3, -3, -2, -2, -1, -1, 0, +1, +2, +2, +3, +3, +4, +4 }, samples);
        Assert.Equal(Expected(-4, -3, -2, -1, +1, +2, +3, +4), samples);
    }

    [Fact]
    public void Decode_K23UsesMap2BitNear()
    {
        // static const int map_2bit_near[] = { -2, -1, +1, +2 };
        var acm = TwoColumnBlock(4, w =>
        {
            w.Add(21, 5); // f_k23: "1, ?, ?" per row
            for (var code = 0; code < 4; code++)
            {
                w.Add(1, 1).Add(code, 2);
            }
        });

        Assert.Equal(Expected(-2, -1, +1, +2), acm.Decode().Samples);
    }

    [Fact]
    public void Decode_K34UsesMap2BitFar()
    {
        // static const int map_2bit_far[] = { -3, -2, +2, +3 };
        var acm = TwoColumnBlock(4, w =>
        {
            w.Add(24, 5); // f_k34: "1, 1, ?, ?" per row
            for (var code = 0; code < 4; code++)
            {
                w.Add(1, 1).Add(1, 1).Add(code, 2);
            }
        });

        Assert.Equal(Expected(-3, -2, +2, +3), acm.Decode().Samples);
    }

    [Fact]
    public void Decode_K12UsesMap1Bit()
    {
        // static const int map_1bit[] = { -1, +1 };
        var acm = TwoColumnBlock(2, w =>
        {
            w.Add(18, 5); // f_k12: "1, ?" per row
            w.Add(1, 1).Add(0, 1);
            w.Add(1, 1).Add(1, 1);
        });

        Assert.Equal(Expected(-1, +1), acm.Decode().Samples);
    }

    [Fact]
    public void Decode_K35TakesBothMapsAndTheDoubleZero()
    {
        // f_k35 rows: "0" writes TWO zero rows; "1,0" one zero; "1,1,0,?" map_1bit; "1,1,1,?,?" map_2bit_far.
        var acm = TwoColumnBlock(6, w =>
        {
            w.Add(23, 5);
            w.Add(0, 1); // rows 0,1 = 0, 0
            w.Add(1, 1).Add(0, 1); // row 2 = 0
            w.Add(1, 1).Add(1, 1).Add(0, 1).Add(1, 1); // row 3 = map_1bit[1] = +1
            w.Add(1, 1).Add(1, 1).Add(1, 1).Add(3, 2); // row 4 = map_2bit_far[3] = +3
            w.Add(1, 1).Add(1, 1).Add(1, 1).Add(0, 2); // row 5 = map_2bit_far[0] = -3
        });

        Assert.Equal(Expected(0, 0, 0, +1, +3, -3), acm.Decode().Samples);
    }

    [Fact]
    public void Decode_T37UnpacksTwoBase11Digits()
    {
        // f_t37: b = (x1) + (x2 * 11), each digit - 5. Code 110 = 0 + 10 * 11 -> (-5, +5).
        var acm = TwoColumnBlock(2, w => w.Add(29, 5).Add(110, 7));

        Assert.Equal(Expected(-5, +5), acm.Decode().Samples);
    }

    [Fact]
    public void Decode_T15UnpacksThreeBase3Digits()
    {
        // f_t15: b = (x1) + (x2 * 3) + (x3 * 9), each digit - 1. Code 11 = 2 + 0 * 3 + 1 * 9 -> (+1, -1, 0).
        var acm = TwoColumnBlock(3, w => w.Add(19, 5).Add(11, 5));

        Assert.Equal(Expected(+1, -1, 0), acm.Decode().Samples);
    }

    [Fact]
    public void Decode_T27UnpacksThreeBase5Digits()
    {
        // f_t27: b = (x1) + (x2 * 5) + (x3 * 25), each digit - 2. Code 95 = 0 + 4 * 5 + 3 * 25 -> (-2, +2, +1).
        var acm = TwoColumnBlock(3, w => w.Add(22, 5).Add(95, 7));

        Assert.Equal(Expected(-2, +2, +1), acm.Decode().Samples);
    }

    [Fact]
    public void Decode_LinearSixteenBitReachesTheTableEdges()
    {
        // ind 16 = f_linear with 16-bit codes, middle 32768: code 0 -> midbuf[-32768], code 65535 -> midbuf[32767].
        // pwr 15 fills the whole table with val 1, so those are -32768 and 32767.
        var w = new BitWriter().Add(15, 4).Add(1, 16);
        w.Add(16, 5).Add(0, 16).Add(65535, 16); // column 0, rows 0 and 1
        w.Add(0, 5); // column 1
        var acm = InterplayAcmFile.Parse(Concat(Header(4, 1, 22050, 0x0021), w.ToArray()), "edge.acm");

        // (0 - 32768 + 1) >> 1 = -16384; 2 * -32768 + 1 = -65535 -> >> 1 = -32768;
        // (-32768 + 32767 + 1) >> 1 = 0; 2 * 32767 + 1 = 65535 -> >> 1 = 32767.
        Assert.Equal(new short[] { -16384, -32768, 0, 32767 }, acm.Decode().Samples);
    }

    [Fact]
    public void Decode_RejectsAnUndefinedFillerIndex()
    {
        // filler_list[1] is f_bad.
        var acm = TwoColumnBlock(1, w => w.Add(1, 5));
        Assert.Throws<InvalidDataException>(() => acm.Decode());
    }

    [Fact]
    public void Decode_RejectsAnOutOfRangeT15Code()
    {
        // f_t15 refuses b >= 27.
        var acm = TwoColumnBlock(1, w => w.Add(19, 5).Add(27, 5));
        var error = Assert.Throws<InvalidDataException>(() => acm.Decode());
        Assert.Contains("t15", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsAStreamThatEndsInsideAFiller()
    {
        // Linear 16-bit codes for 8 rows need 128 bits; the stream carries the selector and nothing more.
        var w = new BitWriter().Add(3, 4).Add(1, 16).Add(16, 5);
        var acm = InterplayAcmFile.Parse(Concat(Header(16, 1, 22050, 0x0081), w.ToArray()), "cut.acm");

        var error = Assert.Throws<InvalidDataException>(() => acm.Decode());
        Assert.Contains("inside a block", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_ReportsTheRequestedChannelCount()
    {
        var stream = new byte[] { 0x42, 0x06, 0x30, 0x2C, 0x07 };
        var acm = InterplayAcmFile.Parse(Concat(Header(2, 1, 22050, 0x0011), stream), "hand1.acm");

        var stereo = acm.Decode(2);

        Assert.Equal(2, stereo.Channels);
        Assert.Equal(1, stereo.FrameCount);
        Assert.Equal(new short[] { 100, 150 }, stereo.Samples);
        Assert.Throws<ArgumentOutOfRangeException>(() => acm.Decode(3));

        // Two values are far below the probe's floor, so the label follows the header.
        Assert.Equal(1, stereo.Probe.InferredChannels);
        Assert.Equal(1, stereo.AsInferred().Channels);
        Assert.Throws<ArgumentOutOfRangeException>(() => stereo.WithChannels(3));
    }

    // ---------------------------------------------------------------- channel probe

    /// <summary>A 440 Hz sine at 22,050 Hz, amplitude 10,000: the "smooth mono" signal.</summary>
    private static short[] Sine(int count, double phase = 0)
    {
        var s = new short[count];
        for (var i = 0; i < count; i++)
        {
            s[i] = (short)Math.Round(10_000 * Math.Sin(2 * Math.PI * 440 * i / 22050.0 + phase));
        }

        return s;
    }

    private static short[] Interleave(short[] left, short[] right)
    {
        var s = new short[left.Length * 2];
        for (var i = 0; i < left.Length; i++)
        {
            s[2 * i] = left[i];
            s[2 * i + 1] = right[i];
        }

        return s;
    }

    [Fact]
    public void Probe_ASmoothSignalReadsAsOneChannelWhateverTheHeaderSays()
    {
        // 50 samples per cycle: the step two apart is about twice the neighbour step (ratio ~0.5),
        // and nothing pairs (within-frame and across-frame steps are the same population).
        var probe = InterplayAcmChannelProbe.Measure(Sine(4096), 2);

        Assert.Equal(1, probe.InferredChannels);
        Assert.InRange(probe.Lag1ToLag2Step, 0.45, 0.55);
        Assert.InRange(probe.WithinToBetweenStep, 0.9, 1.1);
        Assert.Contains("one channel", probe.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_TwoDifferentInterleavedChannelsReadAsStereo()
    {
        // Right is the left channel inverted: every neighbour step is about 2 * |L|, every
        // two-apart step is one sine step — the ratio is far above 2.
        var left = Sine(2048);
        var right = left.Select(v => (short)-v).ToArray();
        var probe = InterplayAcmChannelProbe.Measure(Interleave(left, right), 1);

        Assert.Equal(2, probe.InferredChannels);
        Assert.True(probe.Lag1ToLag2Step > 10, probe.Lag1ToLag2Step.ToString("F2"));
        Assert.Contains("two interleaved channels", probe.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_DualMonoReadsAsStereoByPairing()
    {
        // L == R exactly: the within-frame step is 0, every pair is equal, and the neighbour step
        // is HALF the two-apart step (it is 0 on every other step) — the step rule says mono, the
        // pairing rule says stereo and wins.
        var mono = Sine(2048);
        var probe = InterplayAcmChannelProbe.Measure(Interleave(mono, mono), 2);

        Assert.Equal(2, probe.InferredChannels);
        Assert.Equal(0.0, probe.WithinToBetweenStep);
        Assert.Equal(1.0, probe.EqualPairFraction);
        Assert.InRange(probe.Lag1ToLag2Step, 0.45, 0.55);
        Assert.Contains("paired", probe.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_AnOddTotalIsMonoBeforeAnyStatisticIsConsulted()
    {
        // The inverted-channel stream from above minus its last value: stereo by every statistic,
        // mono by arithmetic — an odd count cannot be frames of two.
        var left = Sine(2048);
        var right = left.Select(v => (short)-v).ToArray();
        var probe = InterplayAcmChannelProbe.Measure(Interleave(left, right).AsSpan(0, 4095), 2);

        Assert.Equal(1, probe.InferredChannels);
        Assert.Contains("odd value total", probe.Reason, StringComparison.Ordinal);
        Assert.True(probe.Lag1ToLag2Step > 10);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Probe_ATinyOrConstantStreamFollowsTheHeader(int declared)
    {
        Assert.Equal(declared,
            InterplayAcmChannelProbe.Measure(Sine(InterplayAcmChannelProbe.MinimumValues - 1), declared)
                .InferredChannels);
        Assert.Equal(declared, InterplayAcmChannelProbe.Measure(new short[4096], declared).InferredChannels);
    }

    /// <summary>LSB-first bit packer, the inverse of libacm's reader.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bitCount;

        public BitWriter Add(int value, int bits)
        {
            for (var i = 0; i < bits; i++)
            {
                if (_bitCount % 8 == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(1 << (_bitCount % 8));
                }

                _bitCount++;
            }

            return this;
        }

        public byte[] ToArray()
        {
            return [.. _bytes];
        }
    }
}