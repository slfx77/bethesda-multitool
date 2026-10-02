using System.Buffers.Binary;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Xngine.Flic;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Media;
using Slfx77.Multitool.Media.Playback;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Xngine.Flic;

/// <summary>Compares the strict decoded-media route with a separately captured, verified prior implementation.</summary>
/// <remarks>The full-frame hashes are legacy output parity, not an independent engine or codec oracle.
/// Both rows together read 4,177,252 original bytes, including the final source rechecks; no payload is written.</remarks>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FlicDecodedMediaDecoderRetailTests
{
    private const long MaximumOriginalReadBytes = 4L * 1024 * 1024;
    private const string OriginalBuild = "The Elder Scrolls - Arena (1994-10-18, PC - Final)";

    // Prior implementation receipt: TestOutput/flc-decoded-adapter-20260920/prior-legacy-baseline.json
    // SHA256 cba89af048b8fd6099030e25bba47756b2ca685eb89f2d27ced506bf00eeda2d; 105 displayed RGBA frames, legacy BMT output rather than an independent codec oracle.
    private static readonly string[] MageFrameHashes =
    [
        "31ac74ee14a37ef411b8e5a105dec25babdf1256b5bd1c1d756decb6783a9c50",
        "91a3a344b8aac62ea3d91186dbfe3e6e032f6d816f441881cc5b813398109aa6",
        "54bcf2b906a39ccf97aa49cb9d303f25acc72898ff085a1766a1f7ac14082bf9",
        "0f3b9e712ff0926adb040e49de02c709529199e8f834eb3293f1291b792dff1b",
        "e41b20028677ea7136fa56e3e1cd000c14a2de0a6cf7254941ce7144ddd59335",
        "7cbd8c2349028b6fe2d9a0a2ffc475452bdc6845140c0c81a49d7f2a002920c2",
        "cba0375437a855179c4114821c529790ef9fad03cdb293191a2c54f750451dcb",
        "d4c71dd7c32e70214ef72c72fff8157df6c43725756c0e68cec27db8978e6a2d",
        "9c971e013e03a269b62273dc5e46cbaca8b00607254e155392f2ee2881b9a3c4",
        "a60ae4581e92cabe883a07a74e306f86eeb4ed9f05dde7e49c55573ed11a7fad",
        "5f692fa965028bb5fe20e965c907f954142c8fe53ef6d17217c8f1362deb2209",
        "d25fc20ad73862b3f747d6a11a69ee117f337e99047afba4998a38aa653bfbd6",
        "9387eb751a1a7cf7c49d9a1e8f11467878e204ddce2b28e7634d6020897ceb76",
        "dce7d679057a00bf9ead6cdc0dc176bd0a9d09c20bf6bc40dc09f5d04f2c2999",
        "7fd6fb3f0cf05d82e9a50e59f6677a9c787cc774fc69ad947440ef90972849fe"
    ];
    private static readonly string[] KingFrameHashes =
    [
        "bb3f25b4cb5f06bd199ecb0a291f7c6a40faeba188705e583982c97e062c27cd",
        "bb3f25b4cb5f06bd199ecb0a291f7c6a40faeba188705e583982c97e062c27cd",
        "bb3f25b4cb5f06bd199ecb0a291f7c6a40faeba188705e583982c97e062c27cd",
        "bb3f25b4cb5f06bd199ecb0a291f7c6a40faeba188705e583982c97e062c27cd",
        "bb3f25b4cb5f06bd199ecb0a291f7c6a40faeba188705e583982c97e062c27cd",
        "bb3f25b4cb5f06bd199ecb0a291f7c6a40faeba188705e583982c97e062c27cd",
        "bb3f25b4cb5f06bd199ecb0a291f7c6a40faeba188705e583982c97e062c27cd",
        "bb3f25b4cb5f06bd199ecb0a291f7c6a40faeba188705e583982c97e062c27cd",
        "bb3f25b4cb5f06bd199ecb0a291f7c6a40faeba188705e583982c97e062c27cd",
        "8d7ddaf29d3509d26f8a3bffa4ed0a31c3f7ca0274025dbe3b7140a99943b872",
        "06f031a865b7df15d7225b121bb9caf96115e29349d975bbd8f67c5c34776fc7",
        "6a2ace4664bc2f9c7d062afeb71b803c539164d671dd2eeffa52a6d840a04236",
        "5a648382b9b7eb7c151b8a1ca9cbe8e51c9ddc1458489e9ecdb4fdf497c54903",
        "011934b7d11b065dfb5858c99277ef850a7b77879fee383207105b864c5d04e5",
        "7017678d0e119eb6a9a0d5b089f5218b2400d8db51edcd42e84dc691e48c4e06",
        "81ca9ac69ee975ef9c2de59ac70d2ec9a3be32d37b34e313619ed268381d4903",
        "e5ba1d7e226f9c7dc20c0e0329242a85d8f194b0a3a5582846af6ee6731595e1",
        "78755a92b93fad6ebd17d23077febeaf155aa4d95dde5f79812a205b470d6676",
        "4746f4531cbdcda9310fa8c2c8752c7331ab846ef06b1c7917ba54fdb010e35e",
        "c5009632b07bd092cf8013ea09e87a20bcd78d71a67db4a0cd70f21de34e4d7d",
        "c8075fefcbd8fc1f94d59d2362a5028978ddfea86063ce01d3bbc5e3ec6991f9",
        "47cb4f7ed658cf2162cf368aeabd7fcc2d921ae0360a8666348ba4ed16b2990f",
        "c9d27675637fb0bbaf783a8ab52f095489b6629bcd268bc224771f2b7300bc0b",
        "858f785eb374c84fd178703d7acbbc75dab9ef1d41cddd1f26747ba3900099f1",
        "24b1eac5cea83ef6f5f48cfeaa20284686defafebfce705d0a8171c9fb0e5b30",
        "2ef589853b5dcd06eeba2cbc8071619668f0471a77e31a2f61cdc3fc97beaf32",
        "ef510d3c1d6ee2e124f43136085c71934b9a3b514f05654503634398e9adc565",
        "e9b20fecf1d1c6428ed1655e948190bd1d4d478a5f77d2ae45014f22fa9d8ed3",
        "085c7e76510f6e229ec19fcf628d35839345b027150a821fde513e1fac75e9fe",
        "18468f7b1cf8b5f8d4e3ae8335b080a8b0a6298b9f116d406ee45eb3a5a23335",
        "a4783be80b98366294cb291668e1d90821dfc0a5c57e77e0f628dd413a087fa4",
        "db0ca39a26a7f35a6ee1a781897beda0bd8fd08439daf3ef3a591501ea80f2ca",
        "7cf705a9ac1bb9809c5fb1bc2c4dac6e805edac9ddd433135f846984391b3e12",
        "0f505380ab86b93c96c4164f415e6c604f1f8a792d95522d63c356d2075cb0a6",
        "27c88a192bd9b5ebaacde0598452cf527b3763450bf4bf3c1a5f70047be230d8",
        "27c82ace40a02308a21333a9569d63481ebbec6a49ec8c0d751eeaa5f14c83b6",
        "f2243e25b4f58c16e3675f4683935662461370f9bcc49c1e92e631f1d69f9790",
        "8bd52a17508c98052f300386f7119b546570f795ca8f2f0e6611f736a9adf346",
        "8f68f5eeda8a4a8a7429642e12e4665b235c6684acd1bff64ffe182fe79411d5",
        "4bf8976e77cc92dca93a30dc7a5250c34528d1c8e2eb4c237526ec541d4bd585",
        "265c1041b60c3b72d052fe55a09ba65af12f27010bece3f1d62f6f4240e1314b",
        "b52680bb2ab5f81f2533fc177e59bb758c249ef6cc644da9bab41767f6e95d23",
        "e034f04bc2d4efa5ba834e4d9bb02e6297b88639e8daee54425d29d8c4de20c7",
        "2030f9117a8c54b5655bad76e128e68d08df151ed6481b848972c03d881a8704",
        "723ca887e9e61b0ff5182e055a7d1156910c147bc3a2d35fc985cb2b41beca7c",
        "268bef9c462ad21ec4603ede180feefa59a55b200c967ececa8af95aea9223c1",
        "5a429d1e961246fcd3482174c345eca5080281d0082317ff1ef29eb54b1cecb2",
        "570ced697c2bbcd1e054c07f9f2a4c53519d6f8e474a82b1e7950b31317365c2",
        "94c7dc6ea9eeae0880682d0534a43353168d1dbf04841d72ae3415995c96f4a1",
        "ef95c16c067c0fb2163a0948b2b7817145b540548cde3dc4641bb087164f5846",
        "fa77cb9475b8aa821a7d143fabf1d1e63b07b178847d13e7d3c4af3ce5fea96c",
        "2e407b69870fa98f29ab98feccb69bdc0d94e4c9daec8bd2859d391dc018d611",
        "6998c85fb7e34c69359d775f102881f7ae3c6698147c0aaa707b18fb45dc08bc",
        "655aa28617a32f647f7d49cc585f65a02e0c6bb76a7fafec488ecf75e1d9d190",
        "f774360d98b6e417b5ade8a43988245110880ea1dd45f93afc7980eb6178e162",
        "f836e1c4c5806d7dd8de750dc9b316e2f631de7fc0b9d443ba17c2c4d0dec30e",
        "357771263c199a48bace66b7336d3009f7afc1a5c8db0c54b3741fc85e3fb624",
        "e2340e1374cf3536126243ceade72e740ce78801fd705d63e215c164e4c73993",
        "ccef54aa7bf44104387e77dad96d1beb0c1f99eff1b3c35b274902558320a9c3",
        "c0f10ccdb4b0df2269cd1254b8339cef9bcfc8a5ce961c8b21391a37c397aed0",
        "a180fcee5137358126a453e6a46c4318d6faa602d1b38804e38c350fcd00ae3c",
        "34d520bd57eb489223c3f4f2153f1ca49aeeeec7072e5fc5aa890e99f5d6c22a",
        "0a0b4c170036af3a6841f14b0d57e1a1e72752fe24373c14b1828d77d922a4fd",
        "34e5880f664b7a6daeeb2511834e1ba404626463c6e669a4ba775cc620c8e29c",
        "370da72ec01e354dc8d5e0ab9618d74aaf4a95a06971a03798c4eeb53dc2a764",
        "73c803b48fd08c39757a1ed138dcf79b3799241925164b244d0e5b89497cae68",
        "3ad86bb57a0e054c4728f012ce91f2f18e64f5c8e576b3815a294850568df1d4",
        "ab0fec2f103fd1e0294ffce4cd925087aa51948db73668f6025efe1afbaac560",
        "7ae99ea12402af76f7ed3f6f44dc797fca1537fea2d8e4520a28954c84318e99",
        "bce59d97c88ce3e836d17f6f6f39cb292ad450b0f269ba0d3760684464b250f6",
        "6b51fad10babefc3c8b670cc15412b71a75382d5dc8ac225ad6fb6c5b2022cb9",
        "c5fffaf0e472cef872b3ca2fbc5b86b721bd0780b5aaf215b002bf9080582910",
        "383e5d52c7b5c48fac73ff0b937b9b7042e61ba5f23117ec053bbde8189a1529",
        "a865a9d551e78edc1df2572a3345bcd5e9d933fd8999a9252f2a902c33d4dac1",
        "706a40e280285c9e236f682141385c547031eea0bb54a9c1efe5f0357837da93",
        "92af9826a558784edd8adc2d4f1fae8f7a1b6b63f863940218784dcb41860ea5",
        "32004ed1031f4923450c3144245e0d5b40bd6817d4c09e9530a4029701e08a64",
        "54d801eff5a21649549e1b10d4c44df6b4e52c62a9e03de006f68b976b85e9d0",
        "1a68624701189fad493966687f427be54a29a3c281ccfe077d7b6dccde635af6",
        "e68b4fb6f904a6d36c49d15053007d7ebd2a8a36819fc5574e452e5228463c8e",
        "b0ca6bac162d9b6d9422570393606ecd2ff02f99e16279405084b61e7441ad7c",
        "943a157b0cd3ccf602f596aff135f6e812e082cbb7a3f250771ac030b22bb113",
        "277d0113dafcd2201bb5d4bfdd9016c061b58ebd52ab6318b32cd093854589d6",
        "d815aac6c51725bf5b10c712c850ba2573e7b4ea7cb5b4fc0b404d1ea43cd5c2",
        "2a46b59c0e40bef3ef9309f78eb80ea3da8029935bc99b384037f5593492a465",
        "95c69d8bb138e51605913db398353c5c93f336c031ec17ff5d85025aea00035f",
        "cff213a6e65c4f90cab210c322820b379d1659108f1d7babfd5f9984302d194d",
        "b306b268c613494a7727410c0e8103bcd65929c0d4dd342cd7a7cd156b89cb37",
        "1420254d6c83cf18902cf1c76be190b6d40738e5fae8971c282096aafd7d5ce9",
        "fe691a7a2597888f357975ec1505a08dde857433004134a56cfe59723d7e1d54"
    ];

    private static readonly (string Name, int Bytes, string SourceHash, int Width, int Height,
        int Frames, int Milliseconds, string[] FrameHashes)[] Fixtures =
    [
        ("MAGE.CEL", 27206, "47314ef76a02f0ed9461098b02807eedef5f4d9e1f7963e21dcb48a4f3898e90",
            110, 119, 15, 71, MageFrameHashes),
        ("KING.FLC", 2061420, "849ef103ca47ed7fe935eb1a1870dbaf860c1dd84ec695aea668bca2889bf524",
            320, 200, 90, 114, KingFrameHashes)
    ];

    /// <summary>Checks every display frame, exact integer clock, reverse indexed seeking, EOF, replay and unchanged originals.</summary>
    /// <param name="fileName">One exact original fixture whose source identity and legacy frame sequence are pinned.</param>
    [Theory]
    [InlineData("MAGE.CEL")]
    [InlineData("KING.FLC")]
    public async Task OriginalAnimationMatchesPriorImplementationWithoutChangingSource(string fileName)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var expected = Assert.Single(Fixtures, fixture => fixture.Name == fileName);
        Assert.Equal(expected.Frames, expected.FrameHashes.Length);
        Assert.InRange(2L * Fixtures.Sum(static fixture => fixture.Bytes), 1, MaximumOriginalReadBytes);
        var path = RealAssetPaths.SampleFile(Path.Combine("Builds", OriginalBuild, fileName));
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage($"Arena 1994-10-18 {fileName}"));
        Assert.NotNull(path);
        var token = TestContext.Current.CancellationToken;
        await using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Assert.Equal(expected.Bytes, original.Length);
        var bytes = new byte[expected.Bytes];
        await original.ReadExactlyAsync(bytes.AsMemory(), token);
        Assert.Equal(expected.Bytes, original.Length);
        Assert.Equal(expected.SourceHash, Hash(bytes));
        AssertHeader(bytes, expected.Bytes, expected.Width, expected.Height, expected.Frames, expected.Milliseconds);

        var trackId = $"Arena:1994-10-18:{fileName}:video:0";
        var frameTicks = (long)expected.Milliseconds * TimeSpan.TicksPerMillisecond;
        var durationTicks = frameTicks * expected.Frames;
        await using (var session = await CreateSessionAsync(bytes, fileName, trackId, token))
        {
            Assert.Equal(TimeSpan.Zero, session.Description.TimelineOrigin);
            Assert.Equal(TimeSpan.FromTicks(durationTicks), session.Description.Duration);
            Assert.Equal(DecodedMediaSeekKind.Indexed, session.Description.SeekKind);
            var track = Assert.Single(session.Description.Tracks);
            Assert.Equal(trackId, track.Id);
            Assert.Equal(DecodedMediaKind.Video, track.Kind);
            Assert.Null(track.Audio);
            Assert.NotNull(track.Video);
            Assert.Equal((expected.Width, expected.Height, expected.Width, expected.Height),
                (track.Video.StoredWidth, track.Video.StoredHeight, track.Video.DisplayWidth, track.Video.DisplayHeight));
            Assert.Null(track.Video.PixelAspect);
            Assert.Equal(new DecodedMediaTrackSelection(trackId, null), session.Selection);
            Assert.Null(await session.ReadAudioAsync(1, token));

            await AssertSequenceAsync(session, expected.FrameHashes, expected.Width, expected.Height, frameTicks, token);
            Assert.Null(await session.ReadVideoAsync(token));
            Assert.Null(await session.ReadVideoAsync(token));

            // Reverse order proves each independently selected slot retains its original palette and exact floor boundary.
            for (var index = expected.Frames - 1; index >= 0; index--)
            {
                var previousGeneration = session.Generation;
                var seek = await session.SeekAsync(new DecodedMediaSeekRequest(
                    TimeSpan.FromTicks(index * frameTicks + frameTicks / 2), session.Selection), token);
                Assert.Equal(index * frameTicks, seek.Position.Ticks);
                Assert.Equal(session.Selection, seek.Selection);
                Assert.True(seek.Generation > previousGeneration);
                Assert.Equal(session.Generation, seek.Generation);
                await AssertFrameAsync(session, expected.FrameHashes[index], expected.Width, expected.Height,
                    index * frameTicks, frameTicks, token);
            }

            var endpoint = await session.SeekAsync(new DecodedMediaSeekRequest(TimeSpan.MaxValue, session.Selection), token);
            Assert.Equal(durationTicks, endpoint.Position.Ticks);
            Assert.Null(await session.ReadVideoAsync(token));
            var replay = await session.SeekAsync(new DecodedMediaSeekRequest(TimeSpan.Zero, session.Selection), token);
            Assert.Equal(TimeSpan.Zero, replay.Position);
            await AssertSequenceAsync(session, expected.FrameHashes, expected.Width, expected.Height, frameTicks, token);
            Assert.Null(await session.ReadVideoAsync(token));
        }

        Assert.Equal(expected.SourceHash, Hash(bytes));
        original.Position = 0;
        await original.ReadExactlyAsync(bytes.AsMemory(), token);
        Assert.Equal(expected.Bytes, original.Length);
        Assert.Equal(expected.SourceHash, Hash(bytes));
    }

    /// <summary>Checks raw source metadata without using either implementation to derive expected timing or geometry.</summary>
    /// <param name="bytes">The exact bounded original bytes.</param>
    /// <param name="length">Pinned encoded length.</param>
    /// <param name="width">Pinned stored width.</param>
    /// <param name="height">Pinned stored height.</param>
    /// <param name="frames">Pinned displayed-frame count, excluding the loop-back block.</param>
    /// <param name="milliseconds">Pinned unsigned header delay.</param>
    private static void AssertHeader(ReadOnlySpan<byte> bytes, int length, int width, int height, int frames, int milliseconds)
    {
        Assert.Equal((uint)length, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal((ushort)0xAF12, BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]));
        Assert.Equal(frames, BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]));
        Assert.Equal(width, BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
        Assert.Equal(height, BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]));
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]));
        Assert.Equal((uint)milliseconds, BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
    }

    /// <summary>Transfers a strictly bounded adapter to Shared, releasing it if session construction refuses ownership.</summary>
    /// <param name="bytes">Borrowed original bytes, retained unchanged by the test.</param>
    /// <param name="name">The exact original filename.</param>
    /// <param name="trackId">The exact selected occurrence identity.</param>
    /// <param name="cancellationToken">Current test lifetime.</param>
    /// <returns>The owning decoded session, with at most one retained video sample.</returns>
    private static async Task<DecodedMediaSession> CreateSessionAsync(byte[] bytes, string name, string trackId,
        CancellationToken cancellationToken)
    {
        var decoder = new FlicDecodedMediaDecoder(bytes, name, trackId, new FlicDecodeLimits
        {
            MaximumEncodedBytes = 2 * 1024 * 1024,
            MaximumCanvasPixels = 320 * 200,
            MaximumFrameBlocks = 91,
            MaximumDecodedBytes = 8L * 1024 * 1024
        }, cancellationToken: cancellationToken);
        try
        {
            return new DecodedMediaSession(decoder, new DecodedMediaSessionLimits
            {
                MaximumRetainedVideoSamples = 1,
                MaximumVideoSampleBytes = 320 * 200 * 4
            });
        }
        catch
        {
            await decoder.DisposeAsync();
            throw;
        }
    }

    /// <summary>Checks the complete forward display sequence without retaining prior decoded samples.</summary>
    /// <param name="session">The owning decoded session aligned to its beginning.</param>
    /// <param name="hashes">Pinned prior-implementation RGBA digests in displayed order.</param>
    /// <param name="width">Pinned canvas width.</param>
    /// <param name="height">Pinned canvas height.</param>
    /// <param name="frameTicks">Exact unsigned header milliseconds converted to integer ticks.</param>
    /// <param name="cancellationToken">Current test lifetime.</param>
    /// <returns>Completion after every sample has been checked and released.</returns>
    private static async Task AssertSequenceAsync(DecodedMediaSession session, string[] hashes, int width, int height,
        long frameTicks, CancellationToken cancellationToken)
    {
        for (var index = 0; index < hashes.Length; index++)
        {
            await AssertFrameAsync(session, hashes[index], width, height, index * frameTicks, frameTicks, cancellationToken);
        }
    }

    /// <summary>Checks all RGBA bytes and exact timing before returning Shared's retained sample capacity.</summary>
    /// <param name="session">The owning decoded session aligned to the required frame.</param>
    /// <param name="expectedHash">The verified prior implementation's full RGBA digest.</param>
    /// <param name="width">Pinned canvas width.</param>
    /// <param name="height">Pinned canvas height.</param>
    /// <param name="timestampTicks">Exact expected frame timestamp.</param>
    /// <param name="durationTicks">Exact expected display duration.</param>
    /// <param name="cancellationToken">Current test lifetime.</param>
    /// <returns>Completion after assertions and unconditional sample retirement.</returns>
    private static async Task AssertFrameAsync(DecodedMediaSession session, string expectedHash, int width, int height,
        long timestampTicks, long durationTicks, CancellationToken cancellationToken)
    {
        await using var sample = Assert.IsType<DecodedVideoSample>(await session.ReadVideoAsync(cancellationToken));
        Assert.Equal((width, height), (sample.Image.Width, sample.Image.Height));
        Assert.Equal(width * height * 4, sample.Image.Pixels.Length);
        Assert.Equal(expectedHash, Hash(sample.Image.Pixels.Span));
        Assert.Equal(timestampTicks, sample.Timestamp.Ticks);
        Assert.Equal(durationTicks, sample.Duration.Ticks);
        Assert.Equal(session.Generation, sample.Generation);
    }

    /// <summary>Identifies complete source or decoded bytes without making another payload copy.</summary>
    /// <param name="bytes">The borrowed bytes to hash.</param>
    /// <returns>The lowercase SHA256 digest used by the accepted legacy baseline receipt.</returns>
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
