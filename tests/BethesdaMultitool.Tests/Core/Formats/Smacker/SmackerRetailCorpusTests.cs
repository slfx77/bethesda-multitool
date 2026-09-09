// ORIGINAL CLEAN-ROOM TESTS for the ORIGINAL CLEAN-ROOM Smacker decoder under
// src/BethesdaMultitool/Core/Formats/Smacker/. Every expectation here comes from our own reverse
// engineering of RAD Game Tools' Smacker decoder as statically linked into Redguard's RG.EXE
// (3.2b) and Battlespire's GAME.EXE (3.0k), as written up in the specification document
//   scratchpad .../gap2/smacker/SPEC.md  ("Smacker Video (SMK2) - Format Specification"),
// or from an independent measurement of the retail bitstreams.
//
// NO FFmpeg- or libav-derived code, and no other third-party Smacker implementation, was consulted,
// read, copied or paraphrased. ffmpeg was used only as a sealed black box, run as an executable to
// compare output pixels and samples; the hashes pinned below are of ITS output.

using System.Security.Cryptography;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Smacker;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Smacker;

/// <summary>
///     Opt-in (<c>RUN_BUCKET_B=1</c>) checks over the whole shipped Smacker corpus this decoder was
///     built against: <b>11 Redguard Disc 2 cutscenes</b> (26,028 frames) and <b>6 Battlespire CD
///     movies</b> (6,180 frames) — 17 files, 32,208 frames, every one <c>SMK2</c>.
///     <para>
///         ⚑ Every count below was measured on 2026-09-08 by an INDEPENDENT Python walk of the raw
///         bytes (scratchpad <c>gap2/smacker/smkref.py</c>) and cross-checked against
///         <c>ffprobe</c>; the frame and audio hashes are MD5s of <c>ffmpeg</c>'s own
///         <c>rgb24</c> / <c>pcm_u8</c> output (<c>compare.py</c>, <c>audio_md5.py</c>), which
///         the reference decoder matched byte for byte on every frame and sample of every file.
///     </para>
///     <para>
///         ⚠ No retail frame carries the keyframe flag, no file the ring-frame flag, no track is
///         16-bit or uncompressed: those paths are inferred from the game code alone and nothing
///         here exercises them.
///     </para>
///     <para>
///         ⚠ Windows globbing is case-insensitive: <c>*.smk</c> and <c>*.SMK</c> list the same 17
///         files twice, and a first census counted 34. Count once.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class SmackerRetailCorpusTests
{
    /// <summary>File name -> (frames, stored height, header flags, track-0 descriptor).</summary>
    private static readonly Dictionary<string, (int Frames, int Height, uint Flags, uint Audio)> Census =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["A_ruins1.smk"] = (41, 240, 2, 0xD0002B11),
            ["A_ruins2.smk"] = (41, 240, 2, 0xD0002B11),
            ["B_ruins1.smk"] = (91, 240, 2, 0xD0002B11),
            ["B_ruins2.smk"] = (110, 240, 2, 0xD0002B11),
            ["C_ruins1.smk"] = (111, 240, 2, 0xD0002B11),
            ["C_ruins2.smk"] = (111, 240, 2, 0xD0002B11),
            ["Intro.smk"] = (6895, 240, 2, 0xD0005622),
            ["SCENE3.SMK"] = (2472, 240, 2, 0xD0005622),
            ["Scene7.smk"] = (5970, 240, 2, 0xD0005622),
            ["Scene10.smk"] = (6805, 240, 2, 0xD0005622),
            ["WIN.SMK"] = (3381, 240, 2, 0xD0005622),
            ["ANCHORS.SMK"] = (101, 480, 0, 0xC0007D00),
            ["JUMP.SMK"] = (46, 480, 0, 0),
            ["chop2.smk"] = (80, 240, 2, 0xC0007D00),
            ["female.smk"] = (1791, 240, 4, 0xC0002B11),
            ["male.smk"] = (1791, 240, 4, 0xC0002B11),
            ["story.smk"] = (2371, 240, 4, 0xC0002B11)
        };

    public static TheoryData<string> AllMovies => [.. Census.Keys];

    /// <summary>ffmpeg's rgb24 frames (stored 640x240 / 640x480), MD5 over the raw bytes: first, middle, last frame.</summary>
    public static TheoryData<string, int, string> OracleFrames => new()
    {
        { "A_ruins1.smk", 0, "197da89efff8358975b3cfa8132b9629" },
        { "A_ruins1.smk", 20, "0efd9388c2ce6a03a1cae5706b5f3aa9" },
        { "A_ruins1.smk", 40, "36f9a6a67ec97908daf087530b0d32e8" },
        { "A_ruins2.smk", 0, "f6e5fb30a08f90dc10d91ce4ee1528b7" },
        { "A_ruins2.smk", 20, "9e0cdb364b0fa12fceadad486eb902b4" },
        { "A_ruins2.smk", 40, "093aaa2b011cae599af695a6a9602f6c" },
        { "B_ruins1.smk", 0, "dc648a2c4e4a71f8bf0ec0f5992bd480" },
        { "B_ruins1.smk", 45, "cc6db8f53f32a4b774d385515f667223" },
        { "B_ruins1.smk", 90, "e76dd2771d3310b63eab66dbfcdc8bb1" },
        { "B_ruins2.smk", 0, "9cd007b5f9aa87cf6b00acb7b4093485" },
        { "B_ruins2.smk", 55, "18ff83824498e0ae7572c8df71c7e26a" },
        { "B_ruins2.smk", 109, "4df9f4125a9e708ba9d3831ac7b5e3b8" },
        { "C_ruins1.smk", 0, "91807f1b1aa1152063b1ab872afc0fbd" },
        { "C_ruins1.smk", 55, "9d41f32eee3e893f912f65e1d026e617" },
        { "C_ruins1.smk", 110, "e5729314bdd7bf1fddfa4e1721a0c464" },
        { "C_ruins2.smk", 0, "9c736edaaf621d6235041034558a3d7d" },
        { "C_ruins2.smk", 55, "e901628d57ebd491fc2a187c001aa8a8" },
        { "C_ruins2.smk", 110, "a91800f736122790c6f60ed0ca53a7ff" },
        // PENDING-LARGE-REDGUARD
        { "ANCHORS.SMK", 0, "6fa1a956d13ef11eb81093d58da75fdb" },
        { "ANCHORS.SMK", 50, "4c4592a98e3cb25551370ef3c5013e3b" },
        { "ANCHORS.SMK", 100, "970a56c0649a0b1083539c7fef088f74" },
        { "JUMP.SMK", 0, "e2a2961e55150725f8fa1b914b28033d" },
        { "JUMP.SMK", 23, "a2d5742d8a1e7dbdd50efb9a7ca22db6" },
        { "JUMP.SMK", 45, "cc0e47180dca4d987f80ce1bf9d7e690" },
        { "chop2.smk", 0, "b683cf696ac5c9f659552832aafd0acf" },
        { "chop2.smk", 40, "8481a082b4a19b6f179ce94dca21c52b" },
        { "chop2.smk", 79, "9034a3315ac0d4546f69fb5cae818c2d" },
        { "female.smk", 0, "7984a345501cfc28e3025343f6a2bf05" },
        { "female.smk", 895, "cc75c2291041d448dff8d7c4bff7b84d" },
        { "female.smk", 1790, "6995eeaf683aa97d1555e134c521a9d8" },
        { "male.smk", 0, "b84054832b66f39d4b1b520bcd58b35f" },
        { "male.smk", 895, "960db32be2be63c372a5e5c06435944f" },
        { "male.smk", 1790, "6995eeaf683aa97d1555e134c521a9d8" },
        { "story.smk", 0, "1eb71c086849e82cff0567e62abb5242" },
        { "story.smk", 1185, "36fdc291946e60fff2fe5164166aedef" },
        { "story.smk", 2370, "62931891dfdaeec6b88c4df513b1ab9e" }
    };

    /// <summary>ffmpeg's pcm_u8 output for track 0: byte count and MD5. JUMP.SMK has no audio.</summary>
    public static TheoryData<string, int, string> OracleAudio => new()
    {
        { "A_ruins1.smk", 60_262, "9764a172efa4f30ff857214cab63d619" },
        { "A_ruins2.smk", 60_262, "563921c6b1b92e188b6782b56d199171" },
        { "B_ruins1.smk", 133_756, "692228fade6fa2aec419bf90cda2a029" },
        { "B_ruins2.smk", 161_670, "3a2e3d554cd79c972b3c17c09a5aa9fa" },
        { "C_ruins1.smk", 163_148, "e1174af704d205c7158bea3bf36bf685" },
        { "C_ruins2.smk", 163_148, "275bd3f9e119200f0512b9b9d36be7c8" },
        { "Intro.smk", 20_269_242, "5d556b0ec892f7e6e0e885667c094cf4" },
        { "SCENE3.SMK", 7_266_930, "c0a8360319a0ff608574700b4cdd421d" },
        { "Scene7.smk", 17_550_036, "9c7db227c4bccf4757fefc69782e0f6f" },
        { "Scene10.smk", 20_004_686, "a9385622f2163dfaaee1553e3c30f752" },
        { "WIN.SMK", 9_939_126, "8220204303527d4d9ced9167d45fd084" },
        { "ANCHORS.SMK", 107_712, "ddb2a391a092d6c65f75b37d51c134ca" },
        { "chop2.smk", 170_624, "70aa4e6248f30321fa50537e1c03b6d3" },
        // female.smk and male.smk carry the SAME audio track (identical bytes) under different video.
        { "female.smk", 1_316_252, "d8e8958261e3063accc551618f9aa0d8" },
        { "male.smk", 1_316_252, "d8e8958261e3063accc551618f9aa0d8" },
        { "story.smk", 1_742_501, "401e6c307557808c0a63ef9cf2c33bf6" }
    };

    private static string Resolve(string fileName)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var directory = fileName.Contains("ruins", StringComparison.OrdinalIgnoreCase)
                        || fileName.StartsWith("Intro", StringComparison.OrdinalIgnoreCase)
                        || fileName.StartsWith("Scene", StringComparison.OrdinalIgnoreCase)
                        || fileName.StartsWith("WIN", StringComparison.OrdinalIgnoreCase)
            ? RealAssetPaths.Classics.RedguardDiscTwoMovies()
            : RealAssetPaths.Classics.BattlespireCdMovies();
        Assert.SkipWhen(directory is null, RealAssetPaths.SkipMessage("The Redguard / Battlespire CD movies"));
        var path = Path.Combine(directory, fileName);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(fileName));
        return path;
    }

    private static List<string> ListMovies(string? directory)
    {
        Assert.SkipWhen(directory is null, RealAssetPaths.SkipMessage("The Redguard / Battlespire CD movies"));
        return Directory.EnumerateFiles(directory, "*.smk")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [Fact]
    public void RedguardDiscTwoShipsElevenSmk2CutscenesOfTwentySixThousandFrames()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var names = ListMovies(RealAssetPaths.Classics.RedguardDiscTwoMovies());

        Assert.Equal(11, names.Count);
        var files = names.Select(n => SmackerFile.Parse(File.ReadAllBytes(Resolve(n)), n)).ToList();

        Assert.All(files, f => Assert.Equal("SMK2", f.Magic));
        Assert.All(files, f => Assert.Equal(640, f.Width));
        Assert.All(files, f => Assert.Equal(240, f.Height));
        Assert.All(files, f => Assert.Equal(480, f.DisplayHeight));
        Assert.All(files, f => Assert.True(f.IsYDoubled));
        Assert.All(files, f => Assert.Equal(-6666, f.FrameRateField));
        Assert.All(files, f => Assert.False(f.HasRingFrame));
        Assert.All(files, f => Assert.True(f.AudioTracks[0].IsPresent && f.AudioTracks[0].IsStereo
                                           && f.AudioTracks[0].IsCompressed && !f.AudioTracks[0].Is16Bit));
        Assert.Equal(26_028, files.Sum(f => f.FrameCount));
        foreach (var f in files)
        {
            Assert.Equal(Census[f.Name].Frames, f.FrameCount);
            Assert.Equal(Census[f.Name].Audio, f.AudioTracks[0].Descriptor);
        }
    }

    [Fact]
    public void BattlespireCdShipsSixSmk2MoviesOfSixThousandFrames()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var names = ListMovies(RealAssetPaths.Classics.BattlespireCdMovies());

        Assert.Equal(6, names.Count);
        var files = names.Select(n => SmackerFile.Parse(File.ReadAllBytes(Resolve(n)), n)).ToList();

        Assert.All(files, f => Assert.Equal("SMK2", f.Magic));
        Assert.All(files, f => Assert.Equal(640, f.Width));
        Assert.Equal(6_180, files.Sum(f => f.FrameCount));
        Assert.Equal(3, files.Count(f => f.IsYInterlaced));
        Assert.Equal(1, files.Count(f => f.IsYDoubled));
        Assert.Equal(2, files.Count(f => f.Height == 480));
        Assert.Single(files, f => !f.AudioTracks[0].IsPresent);
        Assert.Equal(-3333, files.Single(f => f.Name.Equals("ANCHORS.SMK", StringComparison.OrdinalIgnoreCase)).FrameRateField);
        foreach (var f in files)
        {
            Assert.Equal(Census[f.Name].Frames, f.FrameCount);
            Assert.Equal(Census[f.Name].Height, f.Height);
            Assert.Equal(Census[f.Name].Flags, f.Flags);
            Assert.Equal(Census[f.Name].Audio, f.AudioTracks[0].Descriptor);
        }
    }

    [Theory]
    [MemberData(nameof(AllMovies))]
    public void EveryFrameDecodesAndItsBlockStreamEndsInsideItsChunk(string fileName)
    {
        var file = SmackerFile.Parse(File.ReadAllBytes(Resolve(fileName)), fileName);
        var blocksPerFrame = ((file.Width + 3) / 4) * ((file.Height + 3) / 4);
        var decoder = new SmackerVideoDecoder(file);
        var keyFrames = 0;
        var paletteChunks = 0;
        var maxSlack = 0L;

        for (var i = 0; i < file.FrameCount; i++)
        {
            keyFrames += file.IsKeyFrame(i) ? 1 : 0;
            paletteChunks += file.HasPalette(i) ? 1 : 0;
            var layout = file.GetFrameLayout(i);
            decoder.DecodeNext();

            // The block stream must consume its chunk to within one 32-bit word — a reader that
            // loses sync either runs past the end (used > chunk bits) or stops early.
            var chunkBits = (long)layout.Video.Length * 8;
            Assert.True(decoder.LastVideoBitsUsed <= chunkBits, $"{fileName} frame {i} read past its video chunk");
            maxSlack = Math.Max(maxSlack, chunkBits - decoder.LastVideoBitsUsed);
            var coded = decoder.LastBlockCounts.Sum();
            Assert.InRange(coded, blocksPerFrame, blocksPerFrame + 2047);

            if (file.AudioTracks[0].IsPresent && file.HasAudio(i, 0))
            {
                var chunk = SmackerAudioDecoder.Decode(file.Bytes, layout.Audio[0]);
                Assert.True(chunk.BitsUsed <= (long)(layout.Audio[0].Length - 8) * 8, $"{fileName} frame {i} audio read past its chunk");
            }
        }

        Assert.Equal(file.FrameCount, decoder.NextFrame);
        Assert.Equal(0, keyFrames);
        Assert.Equal(fileName.Equals("JUMP.SMK", StringComparison.OrdinalIgnoreCase) ? 6 : 1, paletteChunks);
        Assert.True(maxSlack < 64, $"{fileName}: a frame left {maxSlack} unread bits in its video chunk");
    }

    [Theory]
    [MemberData(nameof(OracleFrames))]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA5351",
        Justification = "The independent FFmpeg fixture oracle supplies MD5 frame digests.")]
    public void NamedFramesMatchTheFfmpegOracle(string fileName, int frame, string expectedMd5)
    {
        var file = SmackerFile.Parse(File.ReadAllBytes(Resolve(fileName)), fileName);
        var decoder = new SmackerVideoDecoder(file);
        decoder.DecodeFrame(frame);

        var indices = decoder.GetFrameIndices();
        var palette = decoder.PaletteRgb;
        var rgb = new byte[indices.Length * 3];
        for (var i = 0; i < indices.Length; i++)
        {
            palette.Slice(indices[i] * 3, 3).CopyTo(rgb.AsSpan(i * 3));
        }

        Assert.Equal(expectedMd5, Convert.ToHexStringLower(MD5.HashData(rgb)));
    }

    [Theory]
    [MemberData(nameof(OracleAudio))]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA5351",
        Justification = "The independent FFmpeg fixture oracle supplies MD5 audio digests.")]
    public void AudioTracksMatchTheFfmpegOracle(string fileName, int expectedBytes, string expectedMd5)
    {
        var file = SmackerFile.Parse(File.ReadAllBytes(Resolve(fileName)), fileName);
        var track = SmackerAudioDecoder.DecodeTrack(file, 0);

        Assert.Equal(8, track.BitsPerSample);
        Assert.Equal(file.AudioTracks[0].Channels, track.Channels);
        Assert.Equal(expectedBytes, track.Pcm.Length);
        Assert.Equal(expectedMd5, Convert.ToHexStringLower(MD5.HashData(track.Pcm)));
    }

    [Fact]
    public void JumpHasNoAudioTrack()
    {
        var file = SmackerFile.Parse(File.ReadAllBytes(Resolve("JUMP.SMK")), "JUMP.SMK");

        Assert.All(file.AudioTracks, t => Assert.False(t.IsPresent));
        Assert.Empty(SmackerAudioDecoder.DecodeTrack(file, 0).Pcm);
    }

    [Fact]
    public void SeekingBackwardsReplaysToTheSamePixels()
    {
        var file = SmackerFile.Parse(File.ReadAllBytes(Resolve("A_ruins1.smk")), "A_ruins1.smk");
        var decoder = new SmackerVideoDecoder(file);

        decoder.DecodeFrame(30);
        var forward = decoder.GetFrameIndices();
        decoder.DecodeFrame(40);
        decoder.DecodeFrame(30);
        Assert.Equal(forward, decoder.GetFrameIndices());
        Assert.Equal(31, decoder.NextFrame);
    }

    [Fact]
    public void AssetBrowserOpensTheDiscMoviesThroughTheVideoSeam()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var directory = RealAssetPaths.Classics.RedguardDiscTwoMovies();
        Assert.SkipWhen(directory is null, RealAssetPaths.SkipMessage("The Redguard Disc 2 movies"));

        using var session = AssetBrowseSession.OpenFolder(directory);
        var videos = session.Root.Children.Where(n => n.Kind == AssetNodeKind.Video).ToList();
        Assert.Equal(11, videos.Count);
        Assert.All(videos, v => Assert.True(ClassicVideoClip.CanOpenAnyVideo(v)));

        var node = videos.Single(v => v.Name.Equals("A_ruins1.smk", StringComparison.OrdinalIgnoreCase));
        var clip = ClassicVideoClip.TryOpenAnyVideo(session, node, TestContext.Current.CancellationToken);
        var smk = Assert.IsType<SmackerVideoClip>(clip);
        Assert.Equal(41, smk.FrameCount);
        Assert.Equal(640, smk.Width);
        Assert.Equal(480, smk.Height);
        Assert.Equal(100000.0 / 6666, 1 / smk.SecondsPerFrame, 6);

        var texture = smk.GetFrame(40);
        Assert.Equal(640 * 480 * 4, texture.Pixels.Length);
        // The doubled display repeats every stored row.
        Assert.Equal(texture.Pixels[..(640 * 4)], texture.Pixels[(640 * 4)..(640 * 8)]);
    }
}
