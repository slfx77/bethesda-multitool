// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' shipped decoder binkw32.dll
// (Fallout Tactics, md5 ecbd8213e89f8afde368f8eb05ff5a9c), via our Ghidra decompilation at
// tools/GhidraProject/ClassicRE/binkw32.dll.decompiled.txt and our own capstone disassembly of the
// same file, as written up in the specification document
//   scratchpad .../cleanroom/bink/SPEC.md  ("Bink Video (BIKi) - Format Specification"), section 7
//   (FUN_3001CE50 coefficients, FUN_3001D820 residuals, FUN_3001C1A0 / FUN_3001CA30 the transform).
//
// NO FFmpeg- or libav-derived code, and no other third-party Bink implementation, was consulted,
// read, copied or paraphrased. ffmpeg was used only as a sealed black box, comparing output pixels.

namespace BethesdaMultitool.Core.Formats.Bink;

/// <summary>
///     The block-level coefficient machinery: Bink's two bit-plane readers (DCT coefficients and
///     8-bit residuals) and the fused dequantise-plus-inverse-DCT that turns coefficients into
///     pixels. Instances hold the scratch buffers so a plane decode allocates nothing per block.
/// </summary>
internal sealed class BinkBlockTransform
{
    // Butterfly constants. ⚠ These are NOT literals in the DLL - not one of the four appears as an
    // immediate in FUN_3001C1A0 / FUN_3001C5C0 / FUN_3001CA30, and the bytes of 3784 (0x0EC8) do
    // not occur as an imm16 anywhere in the image. The transform FORMS each one with a shift/lea
    // chain, and these are those chains evaluated:
    //   0x3001C301  x16, -x, x4, -x, +x*8   -> 473x, then shl 3 at 0x3001C327 ->  3784x
    //   0x3001C310  x8, -x, shl 5, -x, neg, x3, then shl 3 at 0x3001C32A     -> -5352x
    //   0x3001C343  x5, x9, +x*4            ->  181x, then shl 4 at 0x3001C36A ->  2896x
    //   0x3001C34D  x5, +x*8, x9, +x*2, x3                                    ->  2217x
    // Every one is followed by `sar reg, 0xb`, which is the `>> 11` the code below spells out.
    private const int C2896 = 0x0B50;
    private const int C3784 = 0x0EC8;
    private const int CNeg5352 = -0x14E8;
    private const int C2217 = 0x08A9;

    // The work list is a deque: tag-3 entries are pushed at the FRONT (below the scan cursor, so
    // they are only seen on the NEXT pass) and tag-1 splits append at the BACK (above the cursor, so
    // they ARE seen in the same pass). 64 front pushes plus 6 initial plus 9 appended is the bound.
    private const int ListOrigin = 96;
    private const int ListCapacity = 192;

    private readonly int[] _coefficients = new int[64];
    private readonly byte[] _list = new byte[ListCapacity];
    private readonly sbyte[] _residual = new sbyte[64];
    private readonly int[] _significant = new int[64];
    private readonly int[] _temp = new int[64];

    private int _listBack;
    private int _listFront;

    /// <summary>
    ///     Reads one block's DCT coefficients (spec §7.2) and permutes them into natural order.
    ///     <paramref name="dc" /> comes from a bundle and always lands at natural index 0.
    /// </summary>
    internal void ReadCoefficients(BinkBitReader reader, int dc, Span<int> natural)
    {
        ArgumentNullException.ThrowIfNull(reader);

        Array.Clear(_coefficients);
        var bits = (int)reader.Read(4);
        if (bits == 0)
        {
            natural.Clear();
            natural[0] = dc;
            return;
        }

        // (4,tag0) (24,tag0) (44,tag0) (1,tag3) (2,tag3) (3,tag3)
        InitialiseList(0x10, 0x60, 0xB0, 0x07, 0x0B, 0x0F);

        var mask = 1 << (bits - 1);
        var level = bits;
        for (var pass = 0; pass < bits - 1; pass++)
        {
            level--;
            ScanCoefficientList(reader, level, mask, false);
            mask >>= 1;
        }

        ScanCoefficientList(reader, 0, 0, true);

        var permutation = BinkTables.CoefficientPermutation;
        natural[0] = dc;
        for (var n = 1; n < 64; n++)
        {
            natural[n] = _coefficients[permutation[n]];
        }
    }

    /// <summary>
    ///     First rung of the residue step ladder for a 3-bit <paramref name="level" />.
    ///     <para>
    ///         ⚠⚠ 8-BIT, so <c>level == 7</c> gives <c>(sbyte)0x80 == -128</c>, NOT +128. That is
    ///         the DLL's <c>mov bl,1 / shl bl,cl</c> at 0x3001D8B0 writing a byte slot that is read
    ///         back with <c>movsx</c>. Named rather than inlined so the one place this decoder
    ///         disagrees with ffmpeg is a single symbol a test can pin — see
    ///         <see cref="ReadResidual" /> for the measured extent of that disagreement.
    ///     </para>
    /// </summary>
    internal static sbyte InitialResidualStep(int level)
    {
        return (sbyte)(1 << level);
    }

    /// <summary>
    ///     Next rung down. ⚠⚠ An 8-bit ARITHMETIC shift, per <c>sar cl,1</c> at 0x3001DD15, so a
    ///     ladder that started at -128 stays negative all the way down (-64, -32, … -2, -1) where a
    ///     32-bit ladder would stay positive.
    ///     <para>
    ///         ⚠ A negative rung NEVER reaches 0: <c>sar</c> of 0xFF is 0xFF, so -1 halves to -1
    ///         for ever, where the positive ladder does end (1 &gt;&gt; 1 == 0). Neither tail is ever
    ///         consumed — <see cref="ReadResidual" /> runs exactly <c>level + 1</c> passes and
    ///         halves AFTER each, so from level 7 the eight rungs used are -128 … -1 and the ninth
    ///         is computed but unread. A test that pinned the ninth rung as 0 encoded a 32-bit
    ///         expectation against an 8-bit reader and failed; the bytes decide it, and the bytes
    ///         say -1.
    ///     </para>
    /// </summary>
    internal static sbyte HalveResidualStep(sbyte step)
    {
        return (sbyte)(step >> 1);
    }

    /// <summary>
    ///     Reads one block's 8-bit residual (spec §7.4) and permutes it into natural order.
    ///     <paramref name="maxCount" /> is the 7-bit budget read by block type 4; it is compared
    ///     BEFORE the increment, so a budget of <c>n</c> allows <c>n + 1</c> significance events.
    ///     <para>
    ///         ⚠⚠ The step ladder is 8-BIT SIGNED, not int. In the DLL it is built as a BYTE
    ///         (<c>mov bl,1 / shl bl,cl</c> at 0x3001D8B0, <c>cl</c> = level), stored in the byte
    ///         slot <c>[esp+0x13]</c>, read back SIGN-EXTENDED (<c>movsx</c> at 0x3001D926 and
    ///         0x3001DCC6) and halved with an 8-bit ARITHMETIC shift (<c>sar cl,1</c> at
    ///         0x3001DD15). So for <c>level == 7</c> the ladder runs −128, −64, −32… where a 32-bit
    ///         ladder would run +128, +64, +32…: the first rung is the same byte either way
    ///         (±128 are congruent mod 256) and every rung AFTER it has the opposite sign. We model
    ///         the DLL, because the DLL is the decoder that actually played these files.
    ///     </para>
    ///     <para>
    ///         ⚠⚠ THIS IS THE ONLY THING WE DISAGREE WITH ffmpeg ABOUT, and it is bigger than an
    ///         earlier sampled measurement made it look. Measured 2026-09-08 by comparing EVERY
    ///         frame of all 59 retail files against the ffmpeg oracle — 109,985 frames,
    ///         55,140,064,800 bytes of yuv420p, not a sample:
    ///         <list type="bullet">
    ///             <item><b>47 of 59 files byte-exact</b>; 12 files differ somewhere.</item>
    ///             <item><b>3,173 of 109,985 frames differ (2.885%)</b>.</item>
    ///             <item>
    ///                 <b>3,844 differing bytes (0.0000070%)</b> — 2,990 of those frames carry
    ///                 exactly one, the worst carries 19.
    ///             </item>
    ///             <item>ALL 3,844 are in the Y plane. ZERO in either chroma plane.</item>
    ///             <item>
    ///                 ALL 3,844 have <c>(ours − ffmpeg) mod 256 == 128</c> — every single one,
    ///                 with no other delta anywhere. That signature is the ladder sign and nothing
    ///                 else; a general decode fault could not produce it.
    ///             </item>
    ///         </list>
    ///         Per file: tut2b 1,619 frames of 1,621 and tut2c 1,055 of 1,322 (in those two a
    ///         SINGLE pixel is decided early — tut2b's at frame 2, Y(169,555), tut2c's at frame 1,
    ///         Y(137,513): ours 208 vs 80 there, then 128 vs 0 — and then carried at that same
    ///         position to frame 1620 / 1055 by motion compensation, which is why nearly every
    ///         frame "differs" while only one pixel ever does), Bonus_Environment 155, end_movie 129,
    ///         Bonus_Characters 122, SkinLab 26, New_Bonus_SkinLab 25, intro.BIK 25, FO_Trailer 12,
    ///         Bonus_PinUpArt 3, chap3 1, intro.bik 1.
    ///     </para>
    ///     <para>
    ///         ⛔ DO NOT measure this by sampling frames. Two frames per file reports "57 of 59
    ///         byte-exact, 2 differing bytes" and is WRONG: it misses ten of the twelve affected
    ///         files outright, FO_Trailer and end_movie among them, because their divergences are
    ///         isolated frames in the middle of the clip. Only a per-frame sweep of the whole
    ///         corpus finds them.
    ///     </para>
    ///     <para>
    ///         ⛔ Which reading the ENCODER meant is NOT settled, and a picture-plausibility control
    ///         CANNOT settle it. Measured 2026-09-08 over ALL 3,844 disputed bytes (not a sample):
    ///         3,286 are isolated (all eight neighbours identical in both decodes) and 558 are not.
    ///         Against the 8-neighbour median, ffmpeg's value is closer on 2,863 (74.5%) and ours on
    ///         976 (25.4%), mean distance 54.3 vs 78.8 — but 2,674 of ffmpeg's wins are the two
    ///         tutorial clips, i.e. TWO pixels carried by motion, counted once per frame. Over the
    ///         1,170 bytes in the other ten files the SAME control reverses: ours closer on 976,
    ///         ffmpeg's on 189 (end_movie 406:91, Bonus_Environment 365:72, Bonus_Characters 130:4,
    ///         intro.BIK 27:2, FO_Trailer 11:1, SkinLab 19:8, New_Bonus_SkinLab 15:9, PinUpArt 1:2,
    ///         chap3 1:0, intro.bik 1:0). A control that flips with how the events are weighted
    ///         discriminates nothing. What IS hard evidence is the disassembly above. Switching to
    ///         the int model means changing <see cref="InitialResidualStep" /> and
    ///         <see cref="HalveResidualStep" /> to work in <c>int</c>, and moves exactly these
    ///         bytes and no others.
    ///     </para>
    /// </summary>
    internal void ReadResidual(BinkBitReader reader, int maxCount, Span<sbyte> natural)
    {
        ArgumentNullException.ThrowIfNull(reader);

        Array.Clear(_residual);
        var level = (int)reader.Read(3);

        // (4,tag0) (24,tag0) (44,tag0) (0,tag2)
        InitialiseList(0x10, 0x60, 0xB0, 0x02);

        var step = InitialResidualStep(level);
        var significantCount = 0;
        var previouslySignificant = 0;
        var count = 0;
        var finished = false;

        for (var pass = 0; pass <= level && !finished; pass++)
        {
            for (var k = 0; k < previouslySignificant; k++)
            {
                if (!reader.ReadFlag())
                {
                    continue;
                }

                var position = _significant[k];
                var current = _residual[position];
                _residual[position] = (sbyte)(current + (current < 0 ? -step : step));
                if (count == maxCount)
                {
                    finished = true;
                    break;
                }

                count++;
            }

            if (finished)
            {
                break;
            }

            var i = _listFront;
            var end = _listBack;
            while (i < end)
            {
                var entry = _list[i];
                if (entry == 0)
                {
                    i++;
                    continue;
                }

                if (!reader.ReadFlag())
                {
                    i++;
                    continue;
                }

                var tag = entry & 3;
                var position = entry >> 2;
                if (tag == 1)
                {
                    _list[i] = (byte)((position << 2) | 2);
                    Append((byte)(((position + 4) << 2) | 2));
                    Append((byte)(((position + 8) << 2) | 2));
                    Append((byte)(((position + 12) << 2) | 2));
                    end = _listBack;
                    continue;
                }

                if (tag == 3)
                {
                    _significant[significantCount++] = position;
                    _residual[position] = reader.ReadFlag() ? (sbyte)-step : step;
                    if (count == maxCount)
                    {
                        finished = true;
                        break;
                    }

                    count++;
                    _list[i] = 0;
                    i++;
                    continue;
                }

                if (tag == 0)
                {
                    // Slot i is NOT advanced: it is re-read, as a tag 1, later in this same pass.
                    _list[i] = (byte)(((position + 4) << 2) | 1);
                }
                else
                {
                    _list[i] = 0;
                    i++;
                }

                for (var j = 0; j < 4; j++)
                {
                    // ⚠ Polarity is INVERTED relative to the outer scan: inside the group a 0 bit
                    // means "becomes significant now", a 1 means "defer as an individual entry".
                    if (!reader.ReadFlag())
                    {
                        _significant[significantCount++] = position + j;
                        _residual[position + j] = reader.ReadFlag() ? (sbyte)-step : step;
                        if (count == maxCount)
                        {
                            finished = true;
                            break;
                        }

                        count++;
                    }
                    else
                    {
                        PushFront((byte)(((position + j) << 2) | 3));
                    }
                }

                if (finished)
                {
                    break;
                }
            }

            previouslySignificant = significantCount;
            step = HalveResidualStep(step);
        }

        var permutation = BinkTables.CoefficientPermutation;
        for (var n = 0; n < 64; n++)
        {
            natural[n] = _residual[permutation[n]];
        }
    }

    /// <summary>
    ///     The fused dequantise + inverse DCT (spec §7.3). <paramref name="output" /> receives the
    ///     64 values <c>(v + 127) &gt;&gt; 8</c> in natural raster order; the caller truncates them
    ///     to 8 bits, WITHOUT saturating — that is what the DLL's <c>mov [ecx],bl</c> does, and
    ///     saturating here would change real pixels.
    /// </summary>
    internal void InverseDct(ReadOnlySpan<int> natural, ReadOnlySpan<int> dequant, Span<int> output)
    {
        unchecked
        {
            for (var c = 0; c < 8; c++)
            {
                if ((natural[c + 8] | natural[c + 16] | natural[c + 24] | natural[c + 32]
                     | natural[c + 40] | natural[c + 48] | natural[c + 56]) == 0)
                {
                    var flat = (natural[c] * dequant[c]) >> 11;
                    for (var r = 0; r < 8; r++)
                    {
                        _temp[r * 8 + c] = flat;
                    }

                    continue;
                }

                var a0 = (natural[c] * dequant[c]) >> 11;
                var a1 = (natural[c + 8] * dequant[c + 8]) >> 11;
                var a2 = (natural[c + 16] * dequant[c + 16]) >> 11;
                var a3 = (natural[c + 24] * dequant[c + 24]) >> 11;
                var a4 = (natural[c + 32] * dequant[c + 32]) >> 11;
                var a5 = (natural[c + 40] * dequant[c + 40]) >> 11;
                var a6 = (natural[c + 48] * dequant[c + 48]) >> 11;
                var a7 = (natural[c + 56] * dequant[c + 56]) >> 11;

                var t0 = a4 + a0;
                var t1 = a0 - a4;
                var t2 = a6 + a2;
                var o0 = t2 + t0;
                var t3 = t0 - t2;
                var t4 = (((a2 - a6) * C2896) >> 11) - t2;
                var t5 = t4 + t1;
                var t6 = t1 - t4;

                var t7 = a3 + a5;
                var t8 = a5 - a3;
                var t9 = a7 + a1;
                var t10 = a1 - a7;
                var t11 = t9 + t7;
                var t12 = ((t10 + t8) * C3784) >> 11;
                var t13 = ((t8 * CNeg5352) >> 11) - t11 + t12;
                var t14 = (((t9 - t7) * C2896) >> 11) - t13;
                var t15 = ((t10 * C2217) >> 11) - t12 + t14;

                _temp[c] = o0 + t11;
                _temp[56 + c] = o0 - t11;
                _temp[8 + c] = t13 + t5;
                _temp[48 + c] = t5 - t13;
                _temp[16 + c] = t14 + t6;
                _temp[40 + c] = t6 - t14;
                _temp[32 + c] = t15 + t3;
                _temp[24 + c] = t3 - t15;
            }

            for (var r = 0; r < 8; r++)
            {
                var b = r * 8;
                var x0 = _temp[b];
                var x1 = _temp[b + 1];
                var x2 = _temp[b + 2];
                var x3 = _temp[b + 3];
                var x4 = _temp[b + 4];
                var x5 = _temp[b + 5];
                var x6 = _temp[b + 6];
                var x7 = _temp[b + 7];

                var t0 = x0 + x4;
                var t1 = x0 - x4;
                var t2 = x2 + x6;
                var t3 = t2 + t0;
                var t4 = t0 - t2;
                var t5 = (((x2 - x6) * C2896) >> 11) - t2;
                var t6 = t5 + t1;
                var t7 = t1 - t5;

                var t8 = x3 + x5;
                var t9 = x5 - x3;
                var t10 = x7 + x1;
                var t11 = x1 - x7;
                var t12 = t10 + t8;
                var t13 = ((t11 + t9) * C3784) >> 11;
                var t14 = ((t9 * CNeg5352) >> 11) - t12 + t13;
                var t15 = (((t10 - t8) * C2896) >> 11) - t14;
                var t16 = ((t11 * C2217) >> 11) - t13 + t15;

                output[b] = (t12 + t3 + 127) >> 8;
                output[b + 7] = (t3 - t12 + 127) >> 8;
                output[b + 1] = (t14 + t6 + 127) >> 8;
                output[b + 6] = (t6 - t14 + 127) >> 8;
                output[b + 2] = (t15 + t7 + 127) >> 8;
                output[b + 5] = (t7 - t15 + 127) >> 8;
                output[b + 4] = (t4 + t16 + 127) >> 8;
                output[b + 3] = (t4 - t16 + 127) >> 8;
            }
        }
    }

    private void ScanCoefficientList(BinkBitReader reader, int level, int mask, bool finalPass)
    {
        var i = _listFront;
        var end = _listBack;
        while (i < end)
        {
            var entry = _list[i];
            if (entry == 0)
            {
                i++;
                continue;
            }

            if (!reader.ReadFlag())
            {
                i++;
                continue;
            }

            var tag = entry & 3;
            var position = entry >> 2;
            if (tag == 1)
            {
                _list[i] = (byte)((position << 2) | 2);
                Append((byte)(((position + 4) << 2) | 2));
                Append((byte)(((position + 8) << 2) | 2));
                Append((byte)(((position + 12) << 2) | 2));
                end = _listBack;
                continue;
            }

            if (tag == 3)
            {
                _coefficients[position] = ReadCoefficientValue(reader, level, mask, finalPass);
                _list[i] = 0;
                i++;
                continue;
            }

            if (tag == 0)
            {
                // Slot i is NOT advanced: it is re-read, as a tag 1, later in this same pass.
                _list[i] = (byte)(((position + 4) << 2) | 1);
            }
            else
            {
                _list[i] = 0;
                i++;
            }

            for (var j = 0; j < 4; j++)
            {
                // ⚠ Inverted polarity inside the group: 0 means "becomes significant now".
                if (!reader.ReadFlag())
                {
                    _coefficients[position + j] = ReadCoefficientValue(reader, level, mask, finalPass);
                }
                else
                {
                    PushFront((byte)(((position + j) << 2) | 3));
                }
            }
        }
    }

    private static int ReadCoefficientValue(BinkBitReader reader, int level, int mask, bool finalPass)
    {
        if (finalPass)
        {
            // Bit-plane 0: the magnitude is exactly 1, so only a sign bit is read.
            return reader.ReadFlag() ? -1 : 1;
        }

        var value = (int)reader.Read(level) | mask;
        if (reader.ReadFlag())
        {
            value = -value;
        }

        return (short)value;
    }

    private void InitialiseList(params byte[] entries)
    {
        Array.Clear(_list);
        _listFront = ListOrigin;
        _listBack = ListOrigin;
        foreach (var entry in entries)
        {
            _list[_listBack++] = entry;
        }
    }

    private void Append(byte entry)
    {
        if (_listBack >= ListCapacity)
        {
            // ⚠ Divergence 7 of the list on BinkVideoDecoder: the DLL has no bound here.
            throw new InvalidDataException("Bink coefficient work list overflowed — the block has desynced.");
        }

        _list[_listBack++] = entry;
    }

    private void PushFront(byte entry)
    {
        if (_listFront == 0)
        {
            // ⚠ Divergence 8 of the list on BinkVideoDecoder: the DLL has no bound here.
            throw new InvalidDataException("Bink coefficient work list underflowed — the block has desynced.");
        }

        _list[--_listFront] = entry;
    }
}
