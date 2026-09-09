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

using System.Buffers.Binary;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Bink;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Bink;

/// <summary>
///     Opt-in (<c>RUN_BUCKET_B=1</c>) checks over the whole shipped Bink corpus this decoder was
///     built against: <b>20 Fallout Tactics movies</b> (<c>core/movie</c> plus the three tutorial
///     clips under <c>core/locale/missions/tutorial2</c>) and
///     <b>
///         39 Fallout: Brotherhood of Steel
///         Xbox movies
///     </b>
///     (<c>extracted/resx</c>) — 59 files, 109,985 frames.
///     <para>
///         ⚑ Every count below was measured on 2026-09-08 by an INDEPENDENT Python walk of the raw
///         bytes (scratchpad <c>cleanroom/bink/corpus.py</c>) and cross-checked against
///         <c>ffprobe</c> on all 59 files, so the expectations do not come from the reader.
///     </para>
///     <para>
///         ⚠ All 59 are <c>BIKi</c> with header flags 0 and exactly ONE keyframe (frame 0). The
///         alpha (0x100000) and grayscale (0x20000) paths and the older BIKf/BIKg/BIKh codings are
///         therefore NOT exercised by any retail file, and remain inferred from the DLL alone.
///     </para>
///     <para>
///         ⚠ The Xbox movies are byte-identical in format to the PC ones — little-endian <c>BIKi</c>
///         — so there is no console-specific variant to detect. Worth stating because one might
///         have expected big-endian on that platform.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class BinkRetailCorpusTests
{
    [Fact]
    public void FalloutTacticsShipsTwentyBikiMoviesOfSixtySixThousandFrames()
    {
        var movies = TacticsMovies();

        Assert.Equal(20, movies.Count);

        var files = movies.Select(path => BinkFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path)))
            .ToList();

        Assert.All(files, f => Assert.Equal("BIKi", f.Magic));
        Assert.All(files, f => Assert.Equal(0u, f.Flags));
        Assert.Equal(66_135, files.Sum(f => f.FrameCount));
        Assert.Equal(17, files.Count(f => f.AudioTracks.Count > 0));

        var geometry = files.GroupBy(f => $"{f.Width}x{f.Height}")
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<string, int> { ["720x486"] = 14, ["800x600"] = 6 }, geometry);
    }

    [Fact]
    public void BrotherhoodOfSteelXboxShipsThirtyNineBikiMoviesOfFortyThreeThousandFrames()
    {
        var movies = BrotherhoodMovies();

        Assert.Equal(39, movies.Count);

        var files = movies.Select(path => BinkFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path)))
            .ToList();

        Assert.All(files, f => Assert.Equal("BIKi", f.Magic));
        Assert.All(files, f => Assert.Equal(0u, f.Flags));
        Assert.Equal(43_850, files.Sum(f => f.FrameCount));
        Assert.Equal(21, files.Count(f => f.AudioTracks.Count > 0));

        var geometry = files.GroupBy(f => $"{f.Width}x{f.Height}")
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new Dictionary<string, int>
            {
                ["640x480"] = 33, ["640x448"] = 4, ["720x480"] = 1, ["432x320"] = 1
            },
            geometry);
    }

    /// <summary>
    ///     The container arithmetic, on every frame of every file — 109,985 frames. Each relation is
    ///     re-derived here from the raw bytes rather than read back off the parser, so a parser that
    ///     silently repaired a file would fail this.
    /// </summary>
    [Fact]
    public void EveryContainerRelationHoldsOnEveryFrameOfEveryMovie()
    {
        var movies = AllMovies();
        Assert.Equal(59, movies.Count);

        var frames = 0;
        foreach (var path in movies)
        {
            var bytes = File.ReadAllBytes(path);
            var name = Path.GetFileName(path);
            Assert.True(BinkFile.IsBink(bytes), $"{name} failed the content probe");

            var file = BinkFile.Parse(bytes, name);
            Assert.Equal((uint)bytes.Length - 8, file.DeclaredSize);
            Assert.Equal(file.FrameCount, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x10)));

            var tableStart = 0x2C + file.AudioTracks.Count * 12;
            var afterTable = tableStart + (file.FrameCount + 1) * 4;
            Assert.Equal((uint)afterTable, file.RawFrameOffsets[0] & ~1u);
            Assert.Equal((uint)bytes.Length, file.RawFrameOffsets[^1]);

            // Exactly one keyframe, and it is frame 0, on all 59 retail files.
            Assert.True(file.IsKeyFrame(0), $"{name} frame 0 is not flagged as a keyframe");

            var previous = 0;
            for (var i = 0; i < file.FrameCount; i++)
            {
                var start = file.FrameStart(i);
                var end = file.FrameEnd(i);
                Assert.True(start >= previous, $"{name} frame {i} offsets are not monotonic");
                Assert.True(end > start, $"{name} frame {i} is empty");
                Assert.True((uint)(end - start) <= file.LargestFrameSize,
                    $"{name} frame {i} exceeds the declared largest frame");
                previous = start;

                // The audio packets tile inside the frame, and the Y length that follows them
                // lands inside it too. VideoStart throws when either fails.
                var video = file.VideoStart(i);
                var lumaLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(video));
                Assert.True(lumaLength >= 4 && video + lumaLength <= end,
                    $"{name} frame {i}: Y length {lumaLength} leaves the frame");
                frames++;
            }
        }

        Assert.Equal(109_985, frames);
    }

    /// <summary>
    ///     Every movie decodes its first frame at its declared geometry, and each of the three
    ///     planes consumes exactly the bits it declares.
    ///     <para>
    ///         The Y-plane landing check is the sharp one: the plane's length prefix is exact, so a
    ///         single mis-read bit anywhere in the plane moves the bit reader's word pointer and the
    ///         decode throws. Pixel equality would not catch a one-bit error in a flat region.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryMovieDecodesItsFirstFrameAtItsDeclaredGeometry()
    {
        var movies = AllMovies();
        Assert.Equal(59, movies.Count);

        foreach (var path in movies)
        {
            var name = Path.GetFileName(path);
            var file = BinkFile.Parse(File.ReadAllBytes(path), name);
            Assert.True(file.IsDecodableVideo, $"{name} is not a decodable BIKi stream");

            var decoder = new BinkVideoDecoder(file);
            var rgba = decoder.DecodeFrameRgba(0);

            Assert.Equal(file.Width * file.Height * 4, rgba.Length);

            // The Y plane's landing is enforced by a throw inside DecodeFrame, so reaching this
            // line already proves it. What is NOT enforced anywhere is where the two chroma planes
            // stop — they carry no length prefix — so this is the assertion that can actually fail
            // on a decode fault. Measured 2026-09-08 over EVERY frame of all 59 files: the U
            // plane lands EXACTLY on the frame end on all 109,985 of them — never short, never
            // past. A single mis-read bit anywhere in either chroma plane moves it.
            Assert.Equal(decoder.LastFrameEnd, decoder.LastChromaEnd);

            // Luma is decoded on the align8 grid, which can be taller than the picture.
            Assert.Equal((file.Width + 7) & ~7, decoder.LumaPlane.Width);
            Assert.Equal((file.Height + 7) & ~7, decoder.LumaPlane.Height);
            Assert.Equal((((file.Width + 1) >> 1) + 7) & ~7, decoder.ChromaVPlane.Width);
            Assert.Equal(decoder.ChromaVPlane.Width, decoder.ChromaUPlane.Width);
        }
    }

    /// <summary>
    ///     The 38 audio-track descriptors as a census. Every count here was measured 2026-09-08 by
    ///     an independent Python walk of the three header arrays, and the rate and channel columns
    ///     were corroborated by <c>ffprobe</c> on all 38 tracks: <c>sample_rate</c> equals bits
    ///     0..15 on 38/38 and <c>channels=1</c> on exactly the three <c>0xC000AC44</c> tracks
    ///     (tut2a/b/c), 2 on the other 35 — so bit 29 is pinned by an oracle, not by the reader.
    ///     <para>
    ///         ⚠⚠ Bits 28 and 31 split the corpus BY GAME and are perfectly anti-correlated: all 21
    ///         Brotherhood of Steel Xbox tracks are <c>0x7000xxxx</c> (bit 28 set, bit 31 clear),
    ///         all 17 Fallout Tactics tracks <c>0xC000/0xE000xxxx</c> (the reverse). The DLL
    ///         reverse-engineered here is Bink 1.0w and opens its audio decoder only on bit 31, so
    ///         what bit 28 means cannot be separated from "has decoder" on this data; the test
    ///         pins the split so a future audio pass notices if a fixture ever breaks it.
    ///     </para>
    /// </summary>
    [Fact]
    public void AudioTrackDescriptorsFormFiveFamiliesOverThirtyEightTracks()
    {
        var tactics = TacticsMovies().ToDictionary(
            path => Path.GetFileName(path),
            path => BinkFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path)).AudioTracks,
            StringComparer.OrdinalIgnoreCase);
        var brotherhood = BrotherhoodMovies().ToDictionary(
            path => Path.GetFileName(path),
            path => BinkFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path)).AudioTracks,
            StringComparer.OrdinalIgnoreCase);
        var byMovie = tactics.Concat(brotherhood).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var tracks = byMovie.Values.SelectMany(t => t).ToList();

        Assert.Equal(38, tracks.Count);
        Assert.All(byMovie.Values, t => Assert.True(t.Count <= 1, "no corpus movie has two tracks"));

        // The split is by game: every Tactics track has bit 31 (the 1.0w decoder gate) and no
        // bit 28; every Brotherhood of Steel track the reverse.
        var tacticsTracks = tactics.Values.SelectMany(t => t).ToList();
        var brotherhoodTracks = brotherhood.Values.SelectMany(t => t).ToList();
        Assert.Equal(17, tacticsTracks.Count);
        Assert.Equal(21, brotherhoodTracks.Count);
        Assert.All(tacticsTracks, t => Assert.True(t.HasDecoder && (t.Descriptor & 0x1000_0000) == 0));
        Assert.All(brotherhoodTracks, t => Assert.True(!t.HasDecoder && (t.Descriptor & 0x1000_0000) != 0));

        var census = tracks.GroupBy(t => t.Descriptor).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new Dictionary<uint, int>
            {
                [0x7000BB80] = 17, [0xE000AC44] = 14, [0xC000AC44] = 3, [0x7000AC44] = 3, [0x70005622] = 1
            },
            census);

        // Rates and channels as ffprobe reports them, per descriptor family.
        Assert.Equal(17, tracks.Count(t => t.SampleRate == 48000));
        Assert.Equal(20, tracks.Count(t => t.SampleRate == 44100));
        Assert.Equal(1, tracks.Count(t => t.SampleRate == 22050));
        Assert.Equal(3, tracks.Count(t => t.Channels == 1));
        Assert.Equal(35, tracks.Count(t => t.Channels == 2));
        Assert.All(tracks, t => Assert.Equal(16, t.BitsPerSample));
        Assert.All(tracks, t => Assert.Equal(0u, t.Id));

        // The mono tracks are the three tutorial clips and nothing else.
        var mono = byMovie.Where(kvp => kvp.Value.Any(t => t.Channels == 1)).Select(kvp => kvp.Key)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(["tut2a.bik", "tut2b.bik", "tut2c.bik"], mono);

        // Bit 31 (the DLL's decoder gate) and bit 28 never agree on any corpus track.
        Assert.Equal(17, tracks.Count(t => t.HasDecoder));
        Assert.All(tracks, t => Assert.NotEqual(t.HasDecoder, (t.Descriptor & 0x1000_0000) != 0));
    }

    /// <summary>
    ///     The AssetBrowser seam on a REAL movie: <c>ClassicVideoClip.TryOpenAnyVideo</c> over a
    ///     session opened on the movie's own folder, one film per game. The frame counts are
    ///     <c>ffprobe</c>'s <c>duration_ts</c>, and the pixel expectations were derived 2026-09-08
    ///     from ffmpeg's <c>yuv420p</c> bytes at that pixel (Interplay f300 (320,240): Y 48 U 119
    ///     V 135; nuked_brahmin f200 (360,243): Y 176 U 90 V 162, inside a flat 3x3 luma
    ///     neighbourhood) pushed through a Python transcription of <c>_YUV_init@4</c>'s tables —
    ///     neither the frame count nor the RGB was read back off this decoder.
    ///     <para>
    ///         ⚠ The classic-only predicate must still REFUSE the file, or the "any video" pair is
    ///         indistinguishable from it and the routing half of this test proves nothing.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("Interplay.bik", 461, 640, 480, 300, 320, 240, 48, 35, 19)]
    [InlineData("nuked_brahmin.BIK", 473, 720, 486, 200, 360, 243, 240, 173, 110)]
    public void TheAnyVideoSeamPlaysARealMovieOutOfItsOwnFolder(
        string movie, int frames, int width, int height, int frame, int x, int y, int red, int green, int blue)
    {
        var path = AllMovies()
            .FirstOrDefault(p => Path.GetFileName(p).Equals(movie, StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage($"the Bink movie {movie}"));

        using var session = AssetBrowseSession.OpenFolder(Path.GetDirectoryName(path)!);
        var node = FindLeaf(session, movie);

        Assert.False(ClassicVideoClip.CanOpen(node));
        Assert.Null(ClassicVideoClip.TryOpen(session, node, TestContext.Current.CancellationToken));
        Assert.True(ClassicVideoClip.CanOpenAnyVideo(node));

        var clip = ClassicVideoClip.TryOpenAnyVideo(session, node, TestContext.Current.CancellationToken);
        Assert.NotNull(clip);
        Assert.IsType<BinkVideoClip>(clip);
        Assert.Equal(width, clip.Width);
        Assert.Equal(height, clip.Height);
        Assert.Equal(frames, clip.FrameCount);

        var texture = clip.GetFrame(frame);
        Assert.Equal(width, texture.Width);
        Assert.Equal(height, texture.Height);
        var at = (y * width + x) * 4;
        Assert.Equal((red, green, blue, 255),
            (texture.Pixels[at], texture.Pixels[at + 1], texture.Pixels[at + 2], texture.Pixels[at + 3]));

        // Seeking BACKWARDS through the seam replays from the keyframe: frame 0 after frame N must
        // be byte-identical to frame 0 from a fresh clip, and asking for N again must be
        // idempotent. Every retail movie has a single keyframe, so this is the replay path.
        var replayed = clip.GetFrame(0).Pixels;
        using var fresh = AssetBrowseSession.OpenFolder(Path.GetDirectoryName(path)!);
        var freshClip = ClassicVideoClip.TryOpenAnyVideo(fresh, FindLeaf(fresh, movie), TestContext.Current.CancellationToken);
        Assert.NotNull(freshClip);
        Assert.True(replayed.AsSpan().SequenceEqual(freshClip.GetFrame(0).Pixels));
        Assert.True(texture.Pixels.AsSpan().SequenceEqual(clip.GetFrame(frame).Pixels));
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
    ///     A deeper run on one movie from each game: 200 consecutive frames, every Y plane landing
    ///     exactly, exercising the inter-coded block types against a real reference frame.
    /// </summary>
    [Theory]
    [InlineData("nuked_brahmin.BIK", 200)]
    [InlineData("Interplay.bik", 200)]
    public void TwoHundredConsecutiveFramesDecodeWithEveryPlaneLandingExactly(string movie, int frames)
    {
        var path = AllMovies()
            .FirstOrDefault(p => Path.GetFileName(p).Equals(movie, StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage($"the Bink movie {movie}"));

        var file = BinkFile.Parse(File.ReadAllBytes(path), movie);
        var decoder = new BinkVideoDecoder(file);

        var count = Math.Min(frames, file.FrameCount);
        var chromaLandedOnFrameEnd = 0;
        for (var i = 0; i < count; i++)
        {
            decoder.DecodeFrame(i);
            if (decoder.LastChromaEnd == decoder.LastFrameEnd)
            {
                chromaLandedOnFrameEnd++;
            }
        }

        // Same reasoning as above: the Y landing is enforced by a throw, the chroma tail is not.
        // Counting it rather than asserting per frame makes a partial failure legible.
        Assert.Equal(count, chromaLandedOnFrameEnd);

        // The inter-coded types have to be reached: a run that only ever produced INTRA or FILL
        // blocks would mean the reference frame is never consulted, and this assertion is what
        // would catch a decoder that quietly restarted from scratch every frame.
        var interCoded = decoder.BlockTypeCounts[0] + decoder.BlockTypeCounts[2]
                                                    + decoder.BlockTypeCounts[4] + decoder.BlockTypeCounts[7];
        Assert.True(interCoded > 0, "no motion-compensated block was decoded");

        // Types 10 and 11 draw nothing and occur zero times in the corpus; producing one means the
        // bitstream desynced.
        Assert.Equal(0L, decoder.BlockTypeCounts[10] + decoder.BlockTypeCounts[11]);
    }

    /// <summary>
    ///     Van Buren's two <c>Movies\*.mve</c> are Bink, not Interplay MVE: <c>BIKi</c>, flags 0,
    ///     640x320, one <c>0x70005622</c> audio track (22050 Hz stereo, the Brotherhood-of-Steel
    ///     descriptor family). Frame counts are ffprobe's <c>duration_ts</c>; measured 2026-09-08,
    ///     every frame of both is byte-exact against the ffmpeg oracle and the chroma tail lands on
    ///     the frame end on 415/415, which this test re-asserts through the AssetBrowser seam.
    /// </summary>
    [Theory]
    [InlineData("BISlogo.mve", 185)]
    [InlineData("IPlogo.mve", 230)]
    public void VanBurensMveMoviesAreBinkAndDecodeEveryFrameThroughTheSeam(string movie, int frames)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Van Buren"));
        var path = Path.Combine(root, "Movies", movie);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage($"the Van Buren movie {movie}"));

        var bytes = File.ReadAllBytes(path);
        Assert.True(BinkFile.IsBink(bytes));

        var file = BinkFile.Parse(bytes, movie);
        Assert.Equal("BIKi", file.Magic);
        Assert.Equal(0u, file.Flags);
        Assert.Equal((640, 320, frames), (file.Width, file.Height, file.FrameCount));
        Assert.Equal(0x70005622u, Assert.Single(file.AudioTracks).Descriptor);

        var decoder = new BinkVideoDecoder(file);
        var chromaLandedOnFrameEnd = 0;
        for (var i = 0; i < file.FrameCount; i++)
        {
            decoder.DecodeFrame(i);
            if (decoder.LastChromaEnd == decoder.LastFrameEnd)
            {
                chromaLandedOnFrameEnd++;
            }
        }

        Assert.Equal(frames, chromaLandedOnFrameEnd);

        // And the seam claims the .mve by extension, then by content.
        using var session = AssetBrowseSession.OpenFolder(Path.GetDirectoryName(path)!);
        var node = FindLeaf(session, movie);
        Assert.False(ClassicVideoClip.CanOpen(node));
        Assert.True(ClassicVideoClip.CanOpenAnyVideo(node));
        var clip = Assert.IsType<BinkVideoClip>(ClassicVideoClip.TryOpenAnyVideo(session, node, TestContext.Current.CancellationToken));
        Assert.Equal(frames, clip.FrameCount);
    }

    private static List<string> TacticsMovies()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var core = RealAssetPaths.Classics.FalloutTactics();
        Assert.SkipWhen(core is null, RealAssetPaths.SkipMessage("Fallout Tactics"));

        var found = BikFilesUnder(core);
        Assert.SkipWhen(found.Count == 0, RealAssetPaths.SkipMessage("Fallout Tactics movies"));
        return found;
    }

    private static List<string> BrotherhoodMovies()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var extracted = RealAssetPaths.Consoles.BrotherhoodOfSteelXboxExtracted();
        Assert.SkipWhen(extracted is null,
            RealAssetPaths.SkipMessage("the extracted Brotherhood of Steel Xbox disc"));

        var found = BikFilesUnder(extracted);
        Assert.SkipWhen(found.Count == 0,
            RealAssetPaths.SkipMessage("Brotherhood of Steel Xbox movies"));
        return found;
    }

    private static List<string> AllMovies()
    {
        var all = new List<string>();
        all.AddRange(TacticsMovies());
        all.AddRange(BrotherhoodMovies());
        return all;
    }

    /// <summary>
    ///     ⚠ The Windows wildcard <c>*.bik</c> also matches longer extensions (the old 8.3 rule), so
    ///     the extension is re-checked in managed code rather than trusted to the pattern.
    /// </summary>
    private static List<string> BikFilesUnder(string root)
    {
        return Directory.GetFiles(root, "*.bik", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".bik", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}