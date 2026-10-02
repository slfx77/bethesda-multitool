using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Xngine.Flic;
using Slfx77.Multitool.Core.Media;
using Slfx77.Multitool.Media.Playback;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Xngine.Flic;

/// <summary>Checks exact independent pixels/times and ownership for the bounded FLC adapter, without native playback.</summary>
public sealed class FlicDecodedMediaDecoderTests
{
    /// <summary>Each hold and palette-only block occupies time; the CEL prefix and loop-back occupy none.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PixelsHoldsPalettesAndClockRemainExact(bool celPrefix)
    {
        var input = FlicMediaFixture.Create(prefix: celPrefix);
        var before = input.ToArray();
        await using var decoder = new FlicDecodedMediaDecoder(input, "same-label.CEL", "source:entry:video:0",
            cancellationToken: TestContext.Current.CancellationToken);
        byte[][] expected =
        [
            [7, 248, 21, 255, 7, 248, 21, 255],
            [7, 248, 21, 255, 7, 248, 21, 255],
            [97, 40, 10, 255, 97, 40, 10, 255],
            [98, 40, 10, 255, 99, 40, 10, 255]
        ];
        for (var index = 0; index < expected.Length; index++)
        {
            var frame = Assert.IsType<VideoFrame>(await decoder.ReadVideoAsync(TestContext.Current.CancellationToken));
            Assert.Equal(expected[index], frame.Image.Pixels.ToArray());
            Assert.Equal(index * 1_420_000L, frame.Timestamp.Ticks);
            Assert.Equal(1_420_000L, frame.Duration.Ticks);
        }
        Assert.Null(await decoder.ReadVideoAsync(TestContext.Current.CancellationToken));
        Assert.Null(await decoder.ReadVideoAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromTicks(5_680_000), decoder.Description.Duration);
        Assert.Equal(DecodedMediaSeekKind.Indexed, decoder.Description.SeekKind);
        var track = Assert.Single(decoder.Description.Tracks);
        Assert.Equal("source:entry:video:0", track.Id);
        Assert.Equal(DecodedMediaKind.Video, track.Kind);
        Assert.Null(track.Video!.PixelAspect);
        Assert.Equal((500, 71), track.Video.NominalFrameRate);
        Assert.Null(track.Platform);
        Assert.Equal(before, input);
    }

    /// <summary>Large unsigned delays remain exact, without converting through floating-point seconds or signed integers.</summary>
    [Theory]
    [InlineData(uint.MaxValue, 200, 858_993_459)]
    [InlineData(4_294_967_291u, 0, 0)]
    public async Task UnsignedMillisecondHeaderKeepsExactTicks(uint milliseconds, int numerator, int denominator)
    {
        await using var decoder = new FlicDecodedMediaDecoder(FlicMediaFixture.Create(milliseconds), "slow.flc", "slow",
            cancellationToken: TestContext.Current.CancellationToken);
        var ticks = (long)milliseconds * TimeSpan.TicksPerMillisecond;
        (int, int)? expectedRate = denominator == 0 ? null : (numerator, denominator);
        Assert.Equal(expectedRate, Assert.Single(decoder.Description.Tracks).Video!.NominalFrameRate);
        Assert.Equal(TimeSpan.FromTicks(ticks * 4), decoder.Description.Duration);
        var seek = await decoder.SeekAsync(new DecodedMediaSeekRequest(TimeSpan.FromTicks(ticks * 3 + 1), decoder.Selection),
            TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromTicks(ticks * 3), seek.Position);
        var frame = Assert.IsType<VideoFrame>(await decoder.ReadVideoAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ticks * 3, frame.Timestamp.Ticks);
        Assert.Equal(ticks, frame.Duration.Ticks);
    }

    /// <summary>Seeking floors to an actual display boundary, preserves palette preroll, reaches EOF, and replays frame zero at timestamp zero.</summary>
    [Fact]
    public async Task IndexedSeekEndAndReplayPreservePaletteState()
    {
        await using var decoder = new FlicDecodedMediaDecoder(FlicMediaFixture.Create(), "a.flc", "entry",
            cancellationToken: TestContext.Current.CancellationToken);
        var actual = await decoder.SeekAsync(new DecodedMediaSeekRequest(TimeSpan.FromMilliseconds(300), decoder.Selection),
            TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromMilliseconds(284), actual.Position);
        Assert.Equal(new byte[] { 97, 40, 10, 255, 97, 40, 10, 255 },
            (await decoder.ReadVideoAsync(TestContext.Current.CancellationToken))!.Image.Pixels.ToArray());
        actual = await decoder.SeekAsync(new DecodedMediaSeekRequest(TimeSpan.MaxValue, decoder.Selection),
            TestContext.Current.CancellationToken);
        Assert.Equal(decoder.Description.Duration, actual.Position);
        Assert.Null(await decoder.ReadVideoAsync(TestContext.Current.CancellationToken));
        await decoder.SeekAsync(new DecodedMediaSeekRequest(TimeSpan.Zero, decoder.Selection), TestContext.Current.CancellationToken);
        // A re-presentation seeks the session back to its origin after EOF and expects frame 0 at timestamp 0.
        var replayed = Assert.IsType<VideoFrame>(await decoder.ReadVideoAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.Zero, replayed.Timestamp);
        Assert.Equal(new byte[] { 7, 248, 21, 255, 7, 248, 21, 255 }, replayed.Image.Pixels.ToArray());
    }

    /// <summary>Preparation detaches pixels/palettes; absent audio neither writes PCM nor consumes a video slot.</summary>
    [Fact]
    public async Task DetachedInputAndAbsentAudioDoNotAlterVideo()
    {
        var input = FlicMediaFixture.Create();
        await using var decoder = new FlicDecodedMediaDecoder(input, "label.flc", "occurrence",
            cancellationToken: TestContext.Current.CancellationToken);
        Array.Fill(input, (byte)0);
        float[] pcm = [4, 5, 6];
        var audio = await decoder.ReadAudioAsync(pcm, TestContext.Current.CancellationToken);
        Assert.Equal(0, audio.FrameCount);
        Assert.Equal(new float[] { 4, 5, 6 }, pcm);
        var frame = Assert.IsType<VideoFrame>(await decoder.ReadVideoAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.Zero, frame.Timestamp);
        Assert.Equal((byte)7, frame.Image.Pixels.Span[0]);
    }

    /// <summary>Preallocation limits charge encoded bytes, canvas, declared/actual blocks and every decoded payload.</summary>
    [Theory]
    [InlineData("encoded")]
    [InlineData("canvas")]
    [InlineData("declared")]
    [InlineData("actual")]
    [InlineData("decoded")]
    public void LimitsRejectBeforeUnboundedMaterialization(string limit)
    {
        var limits = limit switch
        {
            "encoded" => new FlicDecodeLimits { MaximumEncodedBytes = 128 },
            "canvas" => new FlicDecodeLimits { MaximumCanvasPixels = 1 },
            "declared" => new FlicDecodeLimits { MaximumFrameBlocks = 4 },
            "actual" => new FlicDecodeLimits { MaximumFrameBlocks = 2 },
            _ => new FlicDecodeLimits { MaximumDecodedBytes = 1040 }
        };
        var input = FlicMediaFixture.Create(declaredFrames: limit == "actual" ? 1 : 4);
        Assert.Throws<NotSupportedException>(() => new FlicDecodedMediaDecoder(input, "bounded.flc", "one", limits,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>The new strict profile declines unknown semantics while the existing permissive parser remains available.</summary>
    [Theory]
    [InlineData("speed")]
    [InlineData("unknown")]
    [InlineData("partial")]
    [InlineData("frame-extension")]
    [InlineData("frame-extension-tail")]
    public void StrictProfileDeclinesWithoutChangingLegacyRoute(string kind)
    {
        var input = FlicMediaFixture.Create(speed: kind == "speed" ? 0u : 142u,
            extraChunk: kind == "unknown" ? (ushort)99 : (ushort)18,
            partialPalette: kind == "partial", frameExtension: kind == "frame-extension" ? (ushort)10 : (ushort)0);
        if (kind == "frame-extension-tail")
        {
            input[143] = 1;
        }
        Assert.Equal(4, FlicFile.Parse(input, "legacy.flc").Frames.Count);
        Assert.Throws<NotSupportedException>(() => new FlicDecodedMediaDecoder(input, "strict.flc", "one",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Header mismatch and a chunk crossing its own frame cannot silently steal bytes from the next frame.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedFrameBoundariesAndCountsAreRejected(bool crossFrame)
    {
        var input = FlicMediaFixture.Create(declaredFrames: crossFrame ? 4 : 3);
        if (crossFrame)
        {
            var firstChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(144));
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(144), firstChunkLength + 30);
        }
        Assert.Throws<InvalidDataException>(() => new FlicDecodedMediaDecoder(input, "bad.flc", "one",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>The strict decoder rejects clipped runs and missing delta controls that the old reader tolerated.</summary>
    [Theory]
    [InlineData("run")]
    [InlineData("delta-width")]
    [InlineData("delta-control")]
    [InlineData("empty-row-overrun")]
    [InlineData("skip-to-end")]
    public void MalformedPacketsCannotBeSilentlyClipped(string kind)
    {
        var input = kind switch
        {
            "run" => FlicMediaFixture.Create(firstRun: [1, 3, 7]),
            "delta-width" => FlicMediaFixture.Create(deltaBody: [1, 0, 1, 0, 1, 1, 8, 9]),
            "empty-row-overrun" => FlicMediaFixture.Create(deltaBody: [2, 0, 0, 0, 0, 0]),
            "skip-to-end" => FlicMediaFixture.Create(deltaBody: [1, 0, 255, 255, 0, 0]),
            _ => FlicMediaFixture.Create(deltaBody: [1, 0])
        };
        Assert.Equal(4, FlicFile.Parse(input, "legacy.flc").Frames.Count);
        Assert.Throws<InvalidDataException>(() => new FlicDecodedMediaDecoder(input, "bad.flc", "one",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>The inherited final-pixel control may end exactly at the canvas boundary with no following packets.</summary>
    [Fact]
    public async Task FinalPixelControlPreservesItsValidTerminalBoundary()
    {
        await using var decoder = new FlicDecodedMediaDecoder(
            FlicMediaFixture.Create(deltaBody: [1, 0, 9, 128, 0, 0]), "last-pixel.flc", "one",
            cancellationToken: TestContext.Current.CancellationToken);
        await decoder.SeekAsync(new DecodedMediaSeekRequest(TimeSpan.FromMilliseconds(426), decoder.Selection),
            TestContext.Current.CancellationToken);
        var frame = Assert.IsType<VideoFrame>(await decoder.ReadVideoAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 97, 40, 10, 255, 99, 40, 10, 255 }, frame.Image.Pixels.ToArray());
    }

    /// <summary>An extra ordinary picture cannot be discarded merely because it follows the declared display count.</summary>
    [Fact]
    public void RingMustRestoreFirstIndicesAndPalette()
    {
        var input = FlicMediaFixture.Create(ringIndex: 8);
        Assert.Equal(4, FlicFile.Parse(input, "legacy.flc").Frames.Count);
        Assert.Throws<NotSupportedException>(() => new FlicDecodedMediaDecoder(input, "not-a-ring.flc", "one",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Pre-canceled preparation leaves its owner with the caller; canceled read/seek do not consume slots.</summary>
    [Fact]
    public async Task CancellationPreservesCursorAndConstructorOwnership()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var owner = new InputOwner();
        var input = FlicMediaFixture.Create();
        Assert.Throws<OperationCanceledException>(() => new FlicDecodedMediaDecoder(input, "cancel.flc", "one",
            retainedInput: owner, cancellationToken: canceled.Token));
        Assert.Equal(0, owner.Calls);
        await using var decoder = new FlicDecodedMediaDecoder(input, "cancel.flc", "one",
            retainedInput: owner, cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
        {
            var canceledRead = decoder.ReadVideoAsync(canceled.Token);
            return canceledRead.AsTask();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => decoder.SeekAsync(
            new DecodedMediaSeekRequest(TimeSpan.MaxValue, decoder.Selection), canceled.Token).AsTask());
        var firstRead = decoder.ReadVideoAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.Zero, (await firstRead)!.Timestamp);
    }

    /// <summary>Pending retirement rejects calls and reuses its one task until the exact input owner finishes.</summary>
    [Fact]
    public async Task InputRetirementIsAwaitedOnceBeforeCompletion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new InputOwner(completion.Task);
        var decoder = new FlicDecodedMediaDecoder(FlicMediaFixture.Create(), "a.flc", "one", retainedInput: owner,
            cancellationToken: TestContext.Current.CancellationToken);
        var retirementRequest = decoder.DisposeAsync();
        var retirement = retirementRequest.AsTask();
        Assert.False(retirement.IsCompleted);
        var repeatedRequest = decoder.DisposeAsync();
        Assert.Same(retirement, repeatedRequest.AsTask());
        Assert.Equal(1, owner.Calls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => decoder.ReadVideoAsync(TestContext.Current.CancellationToken).AsTask());
        completion.SetResult();
        await retirement;
        var completedRequest = decoder.DisposeAsync();
        await completedRequest;
        Assert.Equal(1, owner.Calls);
    }

    /// <summary>A failed source retirement remains the same original failure and never retries cleanup implicitly.</summary>
    [Fact]
    public async Task FailedInputRetirementRemainsFailedWithoutRetry()
    {
        var failure = new IOException("retained input failure");
        var owner = new InputOwner(Task.FromException(failure));
        var decoder = new FlicDecodedMediaDecoder(FlicMediaFixture.Create(), "a.flc", "one", retainedInput: owner,
            cancellationToken: TestContext.Current.CancellationToken);
        var retirementRequest = decoder.DisposeAsync();
        var retirement = retirementRequest.AsTask();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => retirement));
        var repeatedRequest = decoder.DisposeAsync();
        Assert.Same(retirement, repeatedRequest.AsTask());
        Assert.Equal(1, owner.Calls);
    }

    /// <summary>Shared copies the borrowed conversion buffer and keeps the input alive until retained samples are released.</summary>
    [Fact]
    public async Task SharedSessionOwnsSamplesAndDelaysInputRetirement()
    {
        var owner = new InputOwner();
        var decoder = new FlicDecodedMediaDecoder(FlicMediaFixture.Create(), "a.flc", "one", retainedInput: owner,
            cancellationToken: TestContext.Current.CancellationToken);
        var session = new DecodedMediaSession(decoder);
        var first = Assert.IsType<DecodedVideoSample>(await session.ReadVideoAsync(TestContext.Current.CancellationToken));
        await session.SeekAsync(new DecodedMediaSeekRequest(TimeSpan.FromMilliseconds(426), session.Selection),
            TestContext.Current.CancellationToken);
        var last = Assert.IsType<DecodedVideoSample>(await session.ReadVideoAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 7, 248, 21, 255, 7, 248, 21, 255 }, first.Image.Pixels.ToArray());
        Assert.Equal(new byte[] { 98, 40, 10, 255, 99, 40, 10, 255 }, last.Image.Pixels.ToArray());
        var retirement = session.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        Assert.Equal(0, owner.Calls);
        await first.DisposeAsync();
        await last.DisposeAsync();
        await retirement;
        Assert.Equal(1, owner.Calls);
    }

    /// <summary>Exposes one deterministic retained-input disposal boundary without file or timer dependencies.</summary>
    private sealed class InputOwner(Task? retirement = null) : IAsyncDisposable
    {
        /// <summary>The actual number of input-retirement invocations.</summary>
        internal int Calls { get; private set; }
        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Calls++;
            return new ValueTask(retirement ?? Task.CompletedTask);
        }
    }
}
