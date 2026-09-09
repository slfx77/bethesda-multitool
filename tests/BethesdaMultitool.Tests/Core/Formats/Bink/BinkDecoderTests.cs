// ORIGINAL CLEAN-ROOM TESTS for the ORIGINAL CLEAN-ROOM Bink decoder under
// src/BethesdaMultitool/Core/Formats/Bink/. Every expectation here comes from our own reverse
// engineering of RAD Game Tools' shipped decoder binkw32.dll (Fallout Tactics, md5
// ecbd8213e89f8afde368f8eb05ff5a9c) as written up in the specification document
//   scratchpad .../cleanroom/bink/SPEC.md  ("Bink Video (BIKi) - Format Specification"),
// or from an independent measurement of the retail bitstreams.
//
// NO FFmpeg- or libav-derived code, and no other third-party Bink implementation, was consulted,
// read, copied or paraphrased. ffmpeg was used only as a sealed black box, run as an executable to
// compare output pixels.

using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Bink;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Bink;

/// <summary>
///     Synthetic checks over the clean-room Bink decoder: the bit reader, the tables dumped out of
///     <c>binkw32.dll</c>, the container probe, and a complete one-block movie built here BY HAND
///     from the specification (<c>scratchpad .../cleanroom/bink/SPEC.md</c>) rather than captured
///     from the decoder's own output.
///     <para>
///         The hand-built frame is the load-bearing test: it exercises the bit reader, all 23 tree
///         headers, the bundle length-field widths, the zero-length retirement path, a FILL block
///         and the YV12 plane order, and its expected pixels are computed from the documented
///         BT.601 limited-range coefficients (255/219 luma scale), not from the code under test.
///     </para>
/// </summary>
public sealed class BinkDecoderTests
{
    [Fact]
    public void BitReaderReturnsBitsLeastSignificantFirst()
    {
        // 0xE5 = 1110 0101; read LSB-first the bits are 1,0,1,0,0,1,1,1.
        var reader = new BinkBitReader([0xE5, 0x1A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], 0);

        Assert.Equal(5u, reader.Read(3)); // bits 0..2 = 1,0,1
        Assert.Equal(28u, reader.Read(5)); // bits 3..7 = 0,0,1,1,1
        Assert.Equal(0x1Au, reader.Read(8));
    }

    [Fact]
    public void BitReaderCrossesAWordBoundaryWithoutLosingBits()
    {
        // Bits 30..37 are bits 6..7 of 0x44 (1,0) then bits 0..5 of 0x55 (1,0,1,0,1,0) = 0x55.
        var reader = new BinkBitReader([0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88], 0);

        Assert.Equal(0x04332211u, reader.Read(30));
        Assert.Equal(0x55u, reader.Read(8));
    }

    [Fact]
    public void BitReaderFetchesWholeWordsOnly()
    {
        var reader = new BinkBitReader([1, 2, 3, 4, 5, 6, 7, 8], 0);

        Assert.Equal(0, reader.WordPointer);
        reader.Read(1);
        Assert.Equal(4, reader.WordPointer);
        reader.Read(31);
        Assert.Equal(4, reader.WordPointer);
        reader.Read(1);
        Assert.Equal(8, reader.WordPointer);
    }

    [Fact]
    public void ResettingToAWordBoundaryDiscardsTheCachedBits()
    {
        var reader = new BinkBitReader([0xFF, 0xFF, 0xFF, 0xFF, 0x2A, 0x00, 0x00, 0x00], 0);

        reader.Read(1);
        reader.ResetToWordBoundary();

        // Up to 31 leftover bits are thrown away, so the next read starts at byte 4.
        Assert.Equal(0x2Au, reader.Read(8));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(511, 9)]
    [InlineData(512, 10)]
    [InlineData(575, 10)]
    [InlineData(5631, 13)]
    public void BundleLengthFieldWidthIsTheBitLengthOfCapacityMinusOne(int value, int expected)
    {
        Assert.Equal(expected, BinkBundle.BitLength(value));
    }

    [Fact]
    public void BundleCapacitiesFollowTheWidthScaledFormula()
    {
        // A 640-wide luma plane: bundle 0 is 0x200 + 80 = 592 (10-bit count), bundle 2 is
        // 0x200 + 80*64 = 5632 (13-bit count). Worked example from the specification, §4.2.
        var bundles = BinkBundle.CreateForPlane(640);

        Assert.Equal(592, bundles[(int)BinkBundleKind.BlockTypes].Capacity);
        Assert.Equal(10, bundles[(int)BinkBundleKind.BlockTypes].LengthBits);
        Assert.Equal(5632, bundles[(int)BinkBundleKind.Colours].Capacity);
        Assert.Equal(13, bundles[(int)BinkBundleKind.Colours].LengthBits);

        // Bundle 1 is the only one sized from half the width.
        Assert.Equal(0x200 + (320 >> 3), bundles[(int)BinkBundleKind.SubBlockTypes].Capacity);
    }

    [Fact]
    public void EveryCodeBookIsACompletePrefixCodeOverSixteenSlots()
    {
        for (var book = 0; book < 16; book++)
        {
            var maxBits = BinkTables.CodeBookMaxBits[book];
            var table = BinkTables.CodeBooks[book];
            Assert.Equal(1 << maxBits, table.Length);

            var lengths = new Dictionary<int, int>();
            var occurrences = new Dictionary<int, int>();
            foreach (var entry in table)
            {
                var slot = entry & 0x0F;
                var length = entry >> 4;
                if (lengths.TryGetValue(slot, out var seen))
                {
                    Assert.Equal(seen, length);
                }
                else
                {
                    lengths[slot] = length;
                }

                occurrences[slot] = occurrences.GetValueOrDefault(slot) + 1;
            }

            Assert.Equal(16, lengths.Count);
            foreach (var (slot, length) in lengths)
            {
                Assert.Equal(1 << (maxBits - length), occurrences[slot]);
            }
        }
    }

    [Fact]
    public void ScanOrdersAreSixteenPermutationsOfAllSixtyFourPositions()
    {
        Assert.Equal(16, BinkTables.ScanOrders.Length);
        foreach (var scan in BinkTables.ScanOrders)
        {
            Assert.Equal(64, scan.Length);
            Assert.Equal(Enumerable.Range(0, 64), scan.Select(v => (int)v).OrderBy(v => v));
        }
    }

    [Fact]
    public void CoefficientPermutationIsAPermutationAndIsNotAZigZag()
    {
        var permutation = BinkTables.CoefficientPermutation.ToArray().Select(v => (int)v).ToArray();

        Assert.Equal(64, permutation.Length);
        Assert.Equal(Enumerable.Range(0, 64), permutation.OrderBy(v => v));

        // The DC always lands at natural index 0, and position 44 lands on natural row 2 column 2 —
        // the entry that proves this is not a zig-zag.
        Assert.Equal(0, permutation[0]);
        Assert.Equal(44, permutation[2 * 8 + 2]);
    }

    [Fact]
    public void DequantiserTablesAreSixteenSixtyFourEntryTablesInSixteenSixteenFixedPoint()
    {
        Assert.Equal(16, BinkTables.IntraDequantisers.Length);
        Assert.Equal(16, BinkTables.InterDequantisers.Length);
        Assert.All(BinkTables.IntraDequantisers, t => Assert.Equal(64, t.Length));
        Assert.All(BinkTables.InterDequantisers, t => Assert.Equal(64, t.Length));

        // Entry 0 of q=0 is 1.0 in 16.16. The encoder's reciprocal table at 0x30034FEC starts at
        // 524288 instead, so this value is what separates the dequantiser from its neighbour.
        Assert.Equal(65536, BinkTables.IntraDequantisers[0][0]);
        Assert.Equal(65536, BinkTables.InterDequantisers[0][0]);
    }

    /// <summary>
    ///     The descriptor fields, pinned to what the DLL itself extracts. The expectations are NOT
    ///     the spec's bit table read back: they are the arguments <c>_BinkOpen@8</c> hands its
    ///     audio-decoder-open callback — <c>rate &amp; 0xffff</c>, <c>(x &gt;&gt; 0x1e &amp; 1) * 8 + 8</c>
    ///     bits, <c>(x &gt;&gt; 0x1d &amp; 1) + 1</c> channels — at decompile lines 2679-2687 and
    ///     4490-4492, evaluated by hand on four descriptors that occur in the retail corpus. The
    ///     mono/stereo split is corroborated by ffprobe (channels=1 on exactly the three
    ///     <c>0xC000AC44</c> tracks), see <c>BinkRetailCorpusTests</c>.
    /// </summary>
    [Theory]
    [InlineData(0xE000AC44u, 44100, 2, 16)] // 14 tracks: 1110 0000 ... -> bits 31,30,29 set
    [InlineData(0xC000AC44u, 44100, 1, 16)] // 3 tracks (tut2a/b/c): bit 29 clear -> mono
    [InlineData(0x7000BB80u, 48000, 2, 16)] // 17 tracks: bit 28 set, bit 31 clear... see below
    [InlineData(0x70005622u, 22050, 2, 16)] // 1 track (fo1_intro.bik)
    public void AudioTrackDescriptorsDecodeRateChannelsAndDepth(
        uint descriptor, int rate, int channels, int bits)
    {
        var track = new BinkAudioTrack(0, descriptor, 0);

        Assert.Equal(rate, track.SampleRate);
        Assert.Equal(channels, track.Channels);
        Assert.Equal(bits, track.BitsPerSample);
    }

    [Fact]
    public void AudioTrackHasDecoderIsBitThirtyOneAlone()
    {
        // The DLL gates its decoder-open on `(desc & 0x80000000) != 0` (lines 2678, 4478). The
        // 0x7000xxxx corpus descriptors have bit 31 CLEAR: bits 30, 29 and 28 make the 7.
        Assert.True(new BinkAudioTrack(0, 0xE000AC44, 0).HasDecoder);
        Assert.True(new BinkAudioTrack(0, 0x8000AC44, 0).HasDecoder);
        Assert.False(new BinkAudioTrack(0, 0x7000BB80, 0).HasDecoder);
        Assert.False(new BinkAudioTrack(0, 0x0000AC44, 0).HasDecoder);
    }

    [Fact]
    public void IsBinkRejectsFilesThatOnlyLookLikeBink()
    {
        Assert.False(BinkFile.IsBink([]));
        Assert.False(BinkFile.IsBink("BIKi"u8.ToArray()));

        // Right magic, wrong arithmetic: the size field, the repeated frame count and the offset
        // table all have to agree, so a four-byte magic alone never claims a file.
        var movie = BuildFillMovie(0x50, 0x80, 0x80);
        Assert.True(BinkFile.IsBink(movie));

        var brokenSize = (byte[])movie.Clone();
        brokenSize[4]++;
        Assert.False(BinkFile.IsBink(brokenSize));

        var brokenRepeat = (byte[])movie.Clone();
        brokenRepeat[0x10]++;
        Assert.False(BinkFile.IsBink(brokenRepeat));

        var brokenMagic = (byte[])movie.Clone();
        brokenMagic[3] = (byte)'x';
        Assert.False(BinkFile.IsBink(brokenMagic));
    }

    [Fact]
    public void ContainerParsesTheHeaderTheFrameTableAndTheKeyframeFlag()
    {
        var file = BinkFile.Parse(BuildFillMovie(0x50, 0x80, 0x80), "synthetic.bik");

        Assert.Equal("BIKi", file.Magic);
        Assert.Equal(8, file.Width);
        Assert.Equal(8, file.Height);
        Assert.Equal(1, file.FrameCount);
        Assert.Equal(30.0, file.FramesPerSecond);
        Assert.Empty(file.AudioTracks);
        Assert.True(file.IsKeyFrame(0));
        Assert.True(file.IsDecodableVideo);

        // With no audio tracks the video starts where the frame does, and the frame table's last
        // entry is the file length.
        Assert.Equal(file.FrameStart(0), file.VideoStart(0));
        Assert.Equal((uint)file.Bytes.Length, file.RawFrameOffsets[^1]);
    }

    [Fact]
    public void VideoStartSkipsOneLengthPrefixedPacketPerAudioTrack()
    {
        // Two tracks whose packets are 8 and 12 bytes of payload: the video starts 4+8+4+12 = 28
        // bytes into the frame, because each length field counts only the bytes that follow it.
        var file = BinkFile.Parse(BuildFillMovie(0x50, 0x80, 0x80, [8, 12]), "audio.bik");

        Assert.Equal(2, file.AudioTracks.Count);
        Assert.Equal(file.FrameStart(0) + 28, file.VideoStart(0));
    }

    [Fact]
    public void AHandBuiltFillFrameDecodesToFlatPlanesInYvuOrder()
    {
        // Luma 0x50, V (Cr) 0x30, U (Cb) 0xC0 — three DIFFERENT values, so a decoder that swapped
        // the two chroma planes fails this test. A movie whose chroma is flat 128 could not.
        var file = BinkFile.Parse(BuildFillMovie(0x50, 0x30, 0xC0), "fill.bik");
        var decoder = new BinkVideoDecoder(file);

        decoder.DecodeFrame(0);

        // ⚠ Do NOT assert on YPlanesLandedExactly here: DecodeFrame THROWS on a mismatch, so the
        // counter only ever counts successes and asserting on it says no more than "did not
        // throw". The sharp check is the chroma tail, which nothing enforces — the two chroma
        // planes carry no length prefix, so a mis-read bit in either one silently moves where the
        // bit reader stops and only this comparison notices.
        Assert.Equal(decoder.LastFrameEnd, decoder.LastChromaEnd);
        AssertPlaneIsFlat(decoder.LumaPlane, 0x50);
        AssertPlaneIsFlat(decoder.ChromaVPlane, 0x30);
        AssertPlaneIsFlat(decoder.ChromaUPlane, 0xC0);

        // One FILL block per plane and nothing else: three blocks of type 6 over the three planes.
        Assert.Equal(3L, decoder.BlockTypeCounts[6]);
        Assert.Equal(0L, decoder.BlockTypeCounts.Where((_, i) => i != 6).Sum());
    }

    [Theory]
    // Expected RGB computed by hand from the documented BT.601 limited-range tables: the luma ramp
    // is trunc((Y-16) * 255/219) and the chroma coefficients are 1.596 / -0.813 / -0.392 / 2.017
    // in the DLL's 15-bit fixed point. Neutral chroma is 128.
    [InlineData(0x50, 0x80, 0x80, 74, 74, 74)]
    [InlineData(128, 200, 128, 244, 72, 130)]
    [InlineData(128, 128, 200, 130, 102, 255)]
    [InlineData(16, 0x80, 0x80, 0, 0, 0)]
    [InlineData(235, 0x80, 0x80, 254, 254, 254)]
    [InlineData(255, 0x80, 0x80, 254, 254, 254)]
    public void ColourConversionMatchesTheDllsLimitedRangeBt601Tables(
        int luma, int chromaV, int chromaU, int red, int green, int blue)
    {
        var file = BinkFile.Parse(
            BuildFillMovie((byte)luma, (byte)chromaV, (byte)chromaU), "colour.bik");
        var decoder = new BinkVideoDecoder(file);

        var rgba = decoder.DecodeFrameRgba(0);

        Assert.Equal(8 * 8 * 4, rgba.Length);
        for (var pixel = 0; pixel < 64; pixel++)
        {
            Assert.Equal(red, rgba[pixel * 4]);
            Assert.Equal(green, rgba[pixel * 4 + 1]);
            Assert.Equal(blue, rgba[pixel * 4 + 2]);
            Assert.Equal(255, rgba[pixel * 4 + 3]);
        }
    }

    [Fact]
    public void ADesyncedTreeHeaderIsReportedRatherThanDecodedIntoGarbage()
    {
        // The explicit-symbol branch: index 1, "explicit list" bit set, count 1, then the SAME
        // symbol twice. That overflows the DLL's 16-entry map; we refuse it instead.
        var writer = new BitWriter();
        writer.Write(1, 4);
        writer.Write(1, 1);
        writer.Write(1, 3);
        writer.Write(7, 4);
        writer.Write(7, 4);

        var reader = new BinkBitReader(writer.ToWordAlignedArray(), 0);

        Assert.Throws<InvalidDataException>(() => BinkHuffmanTree.Read(reader));
    }

    [Fact]
    public void TheVideoFrameSourceSeamOpensABikAndHandsBackRgbaFrames()
    {
        Assert.True(BinkVideoClip.CanOpen("movie.bik"));
        Assert.True(BinkVideoClip.CanOpen("MOVIE.BIK"));
        Assert.False(BinkVideoClip.CanOpen("movie.flc"));

        // Not a Bink file: the seam declines rather than throwing, so a mis-named asset is skipped.
        Assert.Null(BinkVideoClip.TryOpen("not a movie at all"u8.ToArray(), "fake.bik"));

        var clip = BinkVideoClip.TryOpen(BuildFillMovie(0x50, 0x80, 0x80), "fill.bik");
        Assert.NotNull(clip);
        Assert.Equal(8, clip.Width);
        Assert.Equal(8, clip.Height);
        Assert.Equal(1, clip.FrameCount);
        Assert.Equal(1.0 / 30.0, clip.SecondsPerFrame, 9);

        var texture = clip.GetFrame(0);
        Assert.Equal(8, texture.Width);
        Assert.Equal(8, texture.Height);
        Assert.Equal(74, texture.Pixels[0]);
        Assert.Equal(74, texture.Pixels[1]);
        Assert.Equal(74, texture.Pixels[2]);
        Assert.Equal(255, texture.Pixels[3]);
    }

    private static void AssertPlaneIsFlat(BinkPlane plane, byte expected)
    {
        for (var y = 0; y < plane.Height; y++)
        {
            for (var x = 0; x < plane.Width; x++)
            {
                Assert.Equal(expected, plane.Pixels[plane.OffsetOf(x, y)]);
            }
        }
    }

    // ----------------------------------------------------------------------------------------
    // The residue step ladder: the ONE place this decoder deliberately disagrees with ffmpeg.
    // The expected values below are read off the DLL's instructions, not off our decoder:
    //   0x3001D8B0  mov bl, 1 / shl bl, cl        -- an 8-BIT shift, so level 7 makes 0x80
    //   0x3001D926  movsx ecx, byte ptr [esp+0x13] -- read back SIGN-EXTENDED, so 0x80 is -128
    //   0x3001DD15  sar cl, 1                      -- halved with an 8-bit ARITHMETIC shift
    // A 32-bit ladder would give +128, +64, +32...; this one gives -128, -64, -32...  Pinning it
    // means the choice cannot be flipped by accident, only on purpose.
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(6, 64)]
    // level 7 is the whole argument: 1 << 7 in a BYTE is 0x80, which sign-extends to -128.
    [InlineData(7, -128)]
    public void TheResidueLadderStartsWhereTheDllsByteShiftPutsIt(int level, int expected)
    {
        Assert.Equal((sbyte)expected, BinkBlockTransform.InitialResidualStep(level));
    }

    [Fact]
    public void TheResidueLadderFromLevelSevenStaysNegativeAllTheWayDown()
    {
        // sar on a byte: 0x80 -> 0xC0 -> 0xE0 -> ... -> 0xFE -> 0xFF -> 0xFF. An ARITHMETIC shift
        // of 0xFF is 0xFF, so the negative ladder never reaches 0 — the ninth rung is -1 again, not
        // 0. (A previous revision of this test expected 0 there; that was a 32-bit expectation
        // written against an 8-bit reader, and the bytes `D0 F9` = `sar cl,1` at 0x3001DD15 decide
        // it.) Only eight rungs are ever consumed for level 7 — ReadResidual runs level+1 passes —
        // so the ninth is pinned purely to fix the shift's semantics.
        //
        // An int ladder would run +128, +64, +32 ... and is what ffmpeg models; if this test ever
        // reads that sequence the decoder has been switched over and the corpus comparison in
        // BinkBlockTransform's summary no longer describes it.
        var ladder = new List<int>();
        var step = BinkBlockTransform.InitialResidualStep(7);
        for (var i = 0; i < 9; i++)
        {
            ladder.Add(step);
            step = BinkBlockTransform.HalveResidualStep(step);
        }

        Assert.Equal([-128, -64, -32, -16, -8, -4, -2, -1, -1], ladder);
    }

    [Fact]
    public void TheResidueLadderFromLevelSixIsPositiveAndHalvesToZero()
    {
        // The control: every level BELOW 7 is unremarkable and identical under both readings, which
        // is why only level 7 can produce a disagreement at all.
        var ladder = new List<int>();
        var step = BinkBlockTransform.InitialResidualStep(6);
        for (var i = 0; i < 8; i++)
        {
            ladder.Add(step);
            step = BinkBlockTransform.HalveResidualStep(step);
        }

        Assert.Equal([64, 32, 16, 8, 4, 2, 1, 0], ladder);
    }

    // ----------------------------------------------------------------------------------------
    // The AssetBrowser seam. ClassicVideoClip.CanOpenAnyVideo / TryOpenAnyVideo exist ONLY so the
    // GUI's video preview can play a .bik alongside the classic indexed formats, so they are tested
    // here rather than left to the App project (which the net10.0 test TFM cannot reach at all).
    // These run on a real AssetBrowseSession over a temp folder, not a stub, because the thing that
    // could break is the routing through the session's file system.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void BinkRoutesThroughTheAnyVideoSeamThatTheClassicPredicateRejects()
    {
        using var scratch = new ScratchFolder();
        // Neutral chroma here: the YV12 plane order is discriminated by
        // AHandBuiltFillFrameDecodesToFlatPlanesInYvuOrder, and what THIS test is about is the
        // routing, so it uses the one triple whose RGB is already pinned independently below by
        // ColourConversionMatchesTheDllsLimitedRangeBt601Tables — Y 0x50 -> 74,74,74.
        scratch.Write("fill.bik", BuildFillMovie(0x50, 0x80, 0x80));
        using var session = AssetBrowseSession.OpenFolder(scratch.Path);
        var node = FindLeaf(session, "fill.bik");

        // The DISCRIMINATING half: the old classic-only predicate must still say no, or the new
        // method would be indistinguishable from it and this test would prove nothing.
        Assert.False(ClassicVideoClip.CanOpen(node));
        Assert.Null(ClassicVideoClip.TryOpen(session, node, TestContext.Current.CancellationToken));

        Assert.True(ClassicVideoClip.CanOpenAnyVideo(node));
        var clip = ClassicVideoClip.TryOpenAnyVideo(session, node, TestContext.Current.CancellationToken);

        Assert.NotNull(clip);
        Assert.IsType<BinkVideoClip>(clip);
        Assert.Equal(8, clip.Width);
        Assert.Equal(8, clip.Height);
        Assert.Equal(1, clip.FrameCount);

        var texture = clip.GetFrame(0);
        Assert.Equal(8 * 8 * 4, texture.Pixels.Length);
        for (var pixel = 0; pixel < 64; pixel++)
        {
            Assert.Equal(74, texture.Pixels[pixel * 4]);
            Assert.Equal(74, texture.Pixels[pixel * 4 + 1]);
            Assert.Equal(74, texture.Pixels[pixel * 4 + 2]);
            Assert.Equal(255, texture.Pixels[pixel * 4 + 3]);
        }
    }

    [Fact]
    public void TheAnyVideoSeamStillOpensTheClassicFormatsAndRefusesNonVideo()
    {
        using var scratch = new ScratchFolder();
        scratch.Write("notes.txt", "not a movie"u8.ToArray());
        using var session = AssetBrowseSession.OpenFolder(scratch.Path);

        var text = FindLeaf(session, "notes.txt");
        Assert.False(ClassicVideoClip.CanOpenAnyVideo(text));
        Assert.Null(ClassicVideoClip.TryOpenAnyVideo(session, text, TestContext.Current.CancellationToken));

        // The classic extensions the seam must keep claiming — .flc, .cel and .vid all still route
        // to the indexed path, so widening the predicate did not narrow it.
        foreach (var name in new[] { "a.flc", "b.cel", "c.vid" })
        {
            var node = new AssetNode(name, name, AssetNodeKind.Video, 0);
            Assert.True(ClassicVideoClip.CanOpen(node), name);
            Assert.True(ClassicVideoClip.CanOpenAnyVideo(node), name);
        }

        // ...and .bik is claimed by the wide predicate only — as is .mve, because Van Buren's two
        // movies are BIKi under that extension (the content probe then sorts a real Interplay MVE
        // out at open time).
        foreach (var name in new[] { "d.bik", "e.mve" })
        {
            var node = new AssetNode(name, name, AssetNodeKind.Video, 0);
            Assert.False(ClassicVideoClip.CanOpen(node), name);
            Assert.True(ClassicVideoClip.CanOpenAnyVideo(node), name);
        }
    }

    [Fact]
    public void TheAnyVideoSeamReturnsNullForAnMveThatIsNotBink()
    {
        using var scratch = new ScratchFolder();
        // An Interplay MVE's own signature, which is what Fallout's movies carry under .mve.
        scratch.Write("real.mve", "Interplay MVE File\0"u8.ToArray().Concat(new byte[64]).ToArray());
        using var session = AssetBrowseSession.OpenFolder(scratch.Path);
        var node = FindLeaf(session, "real.mve");

        Assert.True(ClassicVideoClip.CanOpenAnyVideo(node));
        Assert.Null(ClassicVideoClip.TryOpenAnyVideo(session, node, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheAnyVideoSeamReturnsNullForAFileThatOnlyLooksLikeBink()
    {
        using var scratch = new ScratchFolder();
        scratch.Write("broken.bik", new byte[] { 0x42, 0x49, 0x4B, 0x69, 1, 2, 3, 4 });
        using var session = AssetBrowseSession.OpenFolder(scratch.Path);
        var node = FindLeaf(session, "broken.bik");

        // The extension predicate still claims it; opening it must fail softly rather than throw,
        // because the GUI calls this on whatever the user clicked.
        Assert.True(ClassicVideoClip.CanOpenAnyVideo(node));
        Assert.Null(ClassicVideoClip.TryOpenAnyVideo(session, node, TestContext.Current.CancellationToken));
    }

    private static AssetNode FindLeaf(AssetBrowseSession session, string name)
    {
        var found = Descend(session.Root)
            .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(found);
        return found;

        static IEnumerable<AssetNode> Descend(AssetNode node)
        {
            yield return node;
            foreach (var child in node.Children)
            {
                foreach (var descendant in Descend(child))
                {
                    yield return descendant;
                }
            }
        }
    }

    /// <summary>
    ///     Builds an 8x8 single-frame <c>BIKi</c> movie whose three planes are each one FILL block,
    ///     written bit by bit from the specification. Optional audio packet payload lengths are
    ///     emitted before the video, one length-prefixed packet per track.
    /// </summary>
    private static byte[] BuildFillMovie(
        byte luma, byte chromaV, byte chromaU, int[]? audioPayloadLengths = null)
    {
        var tracks = audioPayloadLengths ?? [];
        var y = BuildFillPlaneBits(luma);
        var v = BuildFillPlaneBits(chromaV);
        var u = BuildFillPlaneBits(chromaU);

        var frame = new List<byte>();
        foreach (var payload in tracks)
        {
            frame.AddRange(BitConverter.GetBytes((uint)payload));
            frame.AddRange(new byte[payload]);
        }

        // The Y length counts from the length field itself, so it is 4 + the plane's own bytes.
        frame.AddRange(BitConverter.GetBytes((uint)(4 + y.Length)));
        frame.AddRange(y);
        frame.AddRange(v);
        frame.AddRange(u);

        var tableStart = 0x2C + tracks.Length * 12;
        var frameStart = tableStart + 8;
        var length = frameStart + frame.Count;

        var file = new List<byte>();
        file.AddRange("BIKi"u8.ToArray());
        file.AddRange(BitConverter.GetBytes((uint)(length - 8)));
        file.AddRange(BitConverter.GetBytes(1u)); // frame count
        file.AddRange(BitConverter.GetBytes((uint)frame.Count)); // largest frame
        file.AddRange(BitConverter.GetBytes(1u)); // frame count, repeated
        file.AddRange(BitConverter.GetBytes(8u)); // width
        file.AddRange(BitConverter.GetBytes(8u)); // height
        file.AddRange(BitConverter.GetBytes(30u)); // fps dividend
        file.AddRange(BitConverter.GetBytes(1u)); // fps divisor
        file.AddRange(BitConverter.GetBytes(0u)); // flags
        file.AddRange(BitConverter.GetBytes((uint)tracks.Length));

        foreach (var _ in tracks)
        {
            file.AddRange(BitConverter.GetBytes(4096u)); // max decoded buffer
        }

        foreach (var _ in tracks)
        {
            file.AddRange(BitConverter.GetBytes(0xE000AC44u)); // descriptor
        }

        foreach (var _ in tracks)
        {
            file.AddRange(BitConverter.GetBytes(0u)); // id
        }

        file.AddRange(BitConverter.GetBytes((uint)frameStart | 1u)); // frame 0, keyframe
        file.AddRange(BitConverter.GetBytes((uint)length));
        file.AddRange(frame);
        return file.ToArray();
    }

    /// <summary>
    ///     One 8x8 plane coded as a single FILL block. The plane is 8 pixels wide, so bundle 0's
    ///     count field is 10 bits, bundle 1's is 9 (it is sized from half the width) and the rest
    ///     are 10; every bundle but block types and colours is retired with a zero count.
    /// </summary>
    private static byte[] BuildFillPlaneBits(byte colour)
    {
        var writer = new BitWriter();

        // 23 tree headers, all "code book 0", which is an identity map and so a plain 4-bit read.
        for (var i = 0; i < 23; i++)
        {
            writer.Write(0, 4);
        }

        // Bundle 0 (block types): one element, "all the same" mode, value 6 = FILL.
        writer.Write(1, 10);
        writer.Write(1, 1);
        writer.Write(6, 4);

        // Bundle 1 (sub-block types): retired.
        writer.Write(0, 9);

        // Bundle 2 (colours): one element, "all the same", high nibble then low nibble.
        writer.Write(1, 10);
        writer.Write(1, 1);
        writer.Write((uint)(colour >> 4), 4);
        writer.Write((uint)(colour & 0x0F), 4);

        // Bundles 3, 4, 5, 6, 7 and 8: retired with a zero count.
        for (var i = 0; i < 6; i++)
        {
            writer.Write(0, 10);
        }

        return writer.ToWordAlignedArray();
    }

    /// <summary>A throwaway directory for the seam tests, removed even when a test fails.</summary>
    private sealed class ScratchFolder : IDisposable
    {
        internal ScratchFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "bink-seam-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }

        internal void Write(string name, byte[] bytes)
        {
            File.WriteAllBytes(System.IO.Path.Combine(Path, name), bytes);
        }
    }

    /// <summary>An LSB-first bit writer — the exact inverse of the format's reader.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bitCount;

        internal void Write(uint value, int bits)
        {
            for (var i = 0; i < bits; i++)
            {
                var index = _bitCount >> 3;
                if (index >= _bytes.Count)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[index] |= (byte)(1 << (_bitCount & 7));
                }

                _bitCount++;
            }
        }

        /// <summary>Pads to a whole 32-bit word, which is all the reader ever fetches.</summary>
        internal byte[] ToWordAlignedArray()
        {
            while ((_bitCount & 31) != 0)
            {
                Write(0, 1);
            }

            return _bytes.ToArray();
        }
    }
}