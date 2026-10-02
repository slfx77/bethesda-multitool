using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Core.Formats.Xngine.Flic;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.Core.Media;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Exercises exact browser-source admission and retained decoder ownership with tiny FLC inputs.</summary>
public sealed class FlicPreviewPreparationTests
{
    private const string VideoPath = "movies\\same.flc";
    private static readonly byte[] FirstPixels = [7, 248, 21, 255, 7, 248, 21, 255];
    private static readonly byte[] LastPixels = [98, 40, 10, 255, 99, 40, 10, 255];

    /// <summary>The actual readable layer supplies both identity and the only encoded byte read.</summary>
    [Fact]
    public async Task LayeredReadUsesActualWinningBytesAndProvenance()
    {
        var upper = new MemoryFileSystem(FlicMediaFixture.Create(speed: 71), "unreadable-upper") { Unreadable = true };
        var bytes = FlicMediaFixture.Create(speed: 142);
        var lower = new MemoryFileSystem(bytes, "readable-lower");
        await using var mounted = await MountAsync(new LayeredGameFileSystem([upper, lower]));
        Assert.Equal("unreadable-upper", mounted.FileSystem.TryStat(VideoPath)!.Source);

        await using var prepared = await OpenAsync(mounted);

        Assert.Same(mounted.Snapshot, prepared.Snapshot);
        Assert.Same(mounted.Node, prepared.Node);
        Assert.Equal(new GameFileEntry(VideoPath, bytes.Length, "readable-lower"), prepared.Provenance);
        Assert.Equal(Hash(bytes), prepared.EncodedSha256, ignoreCase: true);
        Assert.Equal(1, upper.ReadCount);
        Assert.Equal(1, lower.ReadCount);
        Assert.Equal(int.MaxValue, lower.LastMaximumBytes);
        Assert.Null(prepared.LegacyClip);
        Assert.Null(prepared.StrictDeclineReason);
        var session = Assert.IsAssignableFrom<IDecodedMediaSession>(prepared.DecodedSession);
        using var identity = JsonDocument.Parse(Assert.Single(session.Description.Tracks).Id);
        Assert.Equal(mounted.Snapshot.Source.Id, identity.RootElement.GetProperty("source").GetString());
        Assert.Equal(mounted.Snapshot.Generation, identity.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal(VideoPath, identity.RootElement.GetProperty("path").GetString());
        Assert.Equal(prepared.EncodedSha256, identity.RootElement.GetProperty("sha256").GetString());
        var provenance = identity.RootElement.GetProperty("provenance");
        Assert.Equal(VideoPath, provenance.GetProperty("Path").GetString());
        Assert.Equal(bytes.Length, provenance.GetProperty("Size").GetInt64());
        Assert.Equal("readable-lower", provenance.GetProperty("Source").GetString());
        await using var sample = Assert.IsType<DecodedVideoSample>(
            await session.ReadVideoAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromMilliseconds(142), sample.Duration);
        Assert.Equal(FirstPixels, sample.Image.Pixels.ToArray());
    }

    /// <summary>Strict rejection uses the exact already-read bytes for detached legacy fallback.</summary>
    [Fact]
    public async Task StrictDeclineKeepsSameLayerLegacyFramesWithoutSecondRead()
    {
        var bytes = FlicMediaFixture.Create(partialPalette: true);
        var filesystem = new MemoryFileSystem(bytes, "actual-layer");
        await using var mounted = await MountAsync(filesystem);
        await using var prepared = await OpenAsync(mounted);
        Assert.Null(prepared.DecodedSession);
        Assert.False(string.IsNullOrWhiteSpace(prepared.StrictDeclineReason));
        var clip = Assert.IsType<ClassicVideoClip>(prepared.LegacyClip);
        Assert.Equal(4, clip.FrameCount);
        Assert.Equal(FirstPixels, clip.GetFrame(0).Pixels);
        Assert.Equal(Hash(bytes), prepared.EncodedSha256, ignoreCase: true);
        Assert.Equal("actual-layer", prepared.Provenance.Source);
        Assert.Equal(1, filesystem.ReadCount);
        await mounted.Browser.DisposeAsync();
        Assert.Equal(1, filesystem.DisposalCount);
        Assert.Equal(LastPixels, clip.GetFrame(3).Pixels);
    }

    /// <summary>A same-path node from another opening cannot borrow the current source's authority.</summary>
    [Fact]
    public async Task ForeignNodeIsRejectedBeforeSourceRead()
    {
        var firstFs = new MemoryFileSystem(FlicMediaFixture.Create());
        var secondFs = new MemoryFileSystem(FlicMediaFixture.Create());
        await using var first = await MountAsync(firstFs);
        await using var second = await MountAsync(secondFs);
        await Assert.ThrowsAsync<ArgumentException>(() => FlicPreviewPreparation.OpenAsync(
            second.Snapshot, first.Node, TestContext.Current.CancellationToken));
        Assert.Equal(0, firstFs.ReadCount);
        Assert.Equal(0, secondFs.ReadCount);
    }

    /// <summary>A VFS result for another path is never labeled as the selected movie.</summary>
    [Fact]
    public async Task MismatchedReturnedPathCannotPublishPreparation()
    {
        var filesystem = new MemoryFileSystem(FlicMediaFixture.Create()) { ReturnedPath = "movies\\other.flc" };
        await using var mounted = await MountAsync(filesystem);
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(mounted));
        Assert.Equal(1, filesystem.ReadCount);
        await mounted.Browser.DisposeAsync();
        Assert.Equal(1, filesystem.DisposalCount);
    }

    /// <summary>Cancellation before preparation acquires no payload and leaves the live browser usable.</summary>
    [Fact]
    public async Task PreCanceledPreparationReadsNothing()
    {
        var filesystem = new MemoryFileSystem(FlicMediaFixture.Create());
        await using var mounted = await MountAsync(filesystem);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FlicPreviewPreparation.OpenAsync(
            mounted.Snapshot, mounted.Node, cancellation.Token));
        Assert.Equal(0, filesystem.ReadCount);
        Assert.Equal(0, filesystem.DisposalCount);
    }

    /// <summary>Cancellation after the sole source read returns no decoded or fallback owner.</summary>
    [Fact]
    public async Task PostReadCancellationReleasesItsSourceLease()
    {
        var filesystem = new MemoryFileSystem(FlicMediaFixture.Create());
        await using var mounted = await MountAsync(filesystem);
        using var cancellation = new CancellationTokenSource();
        filesystem.AfterRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FlicPreviewPreparation.OpenAsync(
            mounted.Snapshot, mounted.Node, cancellation.Token));
        Assert.Equal(1, filesystem.ReadCount);
        await mounted.Browser.DisposeAsync();
        Assert.Equal(1, filesystem.DisposalCount);
    }

    /// <summary>Replacement cancels a blocked read but keeps the original filesystem alive until the worker drains.</summary>
    [Fact]
    public async Task SourceReplacementRetainsBlockedReadLeaseUntilWorkerReturns()
    {
        var filesystem = new MemoryFileSystem(FlicMediaFixture.Create());
        await using var mounted = await MountAsync(filesystem);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        filesystem.AfterRead = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)), "The test did not release its bounded read.");
        };
        var pending = OpenAsync(mounted);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            await mounted.Browser.ReplaceAsync(Wrap(new MemoryFileSystem(FlicMediaFixture.Create())),
                TestContext.Current.CancellationToken);
            Assert.True(mounted.Snapshot.CancellationToken.IsCancellationRequested);
            Assert.Equal(0, filesystem.DisposalCount);
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, filesystem.DisposalCount);
        Assert.Equal(1, filesystem.ReadCount);
    }

    /// <summary>A failing source retirement stays reachable and terminal after a canceled preparation drains.</summary>
    [Fact]
    public async Task FailedRetirementRetainsOriginalOwnerWithoutRetry()
    {
        var injected = new IOException("synthetic source retirement failure");
        var filesystem = new MemoryFileSystem(FlicMediaFixture.Create()) { DisposalFailure = injected };
        await using var mounted = await MountAsync(filesystem);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        filesystem.AfterRead = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)), "The test did not release its bounded read.");
        };
        var pending = OpenAsync(mounted);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            await mounted.Browser.DisposeAsync();
            Assert.Equal(0, filesystem.DisposalCount);
        }
        finally { release.Set(); }

        var failure = await Assert.ThrowsAsync<AggregateException>(() => pending);
        Assert.Contains(failure.Flatten().InnerExceptions, error => ReferenceEquals(error, injected));
        var retained = Assert.IsType<FlicPreviewPreparation>(failure.Data["RetainedFlicPreparation"]);
        var retirement = retained.DisposeAsync().AsTask();
        Assert.True(retirement.IsFaulted);
        await Assert.ThrowsAnyAsync<Exception>(() => retirement);
        Assert.Same(retirement, retained.DisposeAsync().AsTask());
        Assert.Equal(1, filesystem.DisposalCount);
        Assert.Equal(1, filesystem.ReadCount);
    }

    /// <summary>Disposal waits for retained decoded pixels before releasing the retired source.</summary>
    [Fact]
    public async Task RetainedSampleDelaysPreparationRetirement()
    {
        var filesystem = new MemoryFileSystem(FlicMediaFixture.Create());
        await using var mounted = await MountAsync(filesystem);
        var prepared = await OpenAsync(mounted);
        var session = Assert.IsAssignableFrom<IDecodedMediaSession>(prepared.DecodedSession);
        var sample = Assert.IsType<DecodedVideoSample>(await session.ReadVideoAsync(TestContext.Current.CancellationToken));
        try
        {
            await mounted.Browser.DisposeAsync();
            var retirement = prepared.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, prepared.DisposeAsync().AsTask());
            Assert.Equal(0, filesystem.DisposalCount);
            Assert.Equal(FirstPixels, sample.Image.Pixels.ToArray());
        }
        finally
        {
            await sample.DisposeAsync();
            await prepared.DisposeAsync();
        }
        Assert.Equal(1, filesystem.DisposalCount);
    }

    /// <summary>Explicit transfer leaves the caller owning the exact session after the preparation result retires.</summary>
    [Fact]
    public async Task TakeTransfersOneSessionWithoutRetiringIt()
    {
        var filesystem = new MemoryFileSystem(FlicMediaFixture.Create());
        await using var mounted = await MountAsync(filesystem);
        var prepared = await OpenAsync(mounted);
        var expected = prepared.DecodedSession;
        await using var session = prepared.TakeDecodedSession();
        Assert.Same(expected, session);
        Assert.Throws<InvalidOperationException>(() => prepared.TakeDecodedSession());
        await prepared.DisposeAsync();
        await mounted.Browser.DisposeAsync();
        Assert.Equal(0, filesystem.DisposalCount);
        await using (var sample = Assert.IsType<DecodedVideoSample>(
            await session.ReadVideoAsync(TestContext.Current.CancellationToken)))
        {
            Assert.Equal(FirstPixels, sample.Image.Pixels.ToArray());
        }
        await session.DisposeAsync();
        Assert.Equal(1, filesystem.DisposalCount);
    }

    /// <summary>A re-presentation seeks the transferred session back to its origin and replays frame zero without another source read.</summary>
    [Fact]
    public async Task DecodedSessionRealignsToOriginForRepresentationWithoutASecondRead()
    {
        var filesystem = new MemoryFileSystem(FlicMediaFixture.Create());
        await using var mounted = await MountAsync(filesystem);
        var prepared = await OpenAsync(mounted);
        await using var session = prepared.TakeDecodedSession();
        await prepared.DisposeAsync();
        for (var index = 0; index < 2; index++)
        {
            var consumed = Assert.IsType<DecodedVideoSample>(await session.ReadVideoAsync(TestContext.Current.CancellationToken));
            Assert.Equal(TimeSpan.FromMilliseconds(142L * index), consumed.Timestamp);
            await consumed.DisposeAsync();
        }

        var generation = session.Generation;
        var origin = session.Description.TimelineOrigin;
        var realigned = await session.SeekAsync(new DecodedMediaSeekRequest(origin, session.Selection),
            TestContext.Current.CancellationToken);
        Assert.Equal(origin, realigned.Position);
        Assert.True(session.Generation > generation);
        Assert.Equal(session.Generation, realigned.Generation);
        await using var replayed = Assert.IsType<DecodedVideoSample>(
            await session.ReadVideoAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.Zero, replayed.Timestamp);
        Assert.Equal(FirstPixels, replayed.Image.Pixels.ToArray());
        Assert.Equal(1, filesystem.ReadCount);
    }

    /// <summary>Both success routes detach their palettes and index buffers without mutating the borrowed payload.</summary>
    /// <param name="legacy">Whether a supported legacy-only palette declaration forces the fallback route.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedFramesAreIndependentOfOriginalByteArray(bool legacy)
    {
        var bytes = FlicMediaFixture.Create(partialPalette: legacy);
        var original = (byte[])bytes.Clone();
        await using var mounted = await MountAsync(new MemoryFileSystem(bytes));
        await using var prepared = await OpenAsync(mounted);
        Assert.Equal(original, bytes);
        Array.Clear(bytes);
        Assert.Equal(Hash(original), prepared.EncodedSha256, ignoreCase: true);
        if (legacy)
        {
            var clip = Assert.IsType<ClassicVideoClip>(prepared.LegacyClip);
            Assert.Equal(FirstPixels, clip.GetFrame(0).Pixels);
            Assert.Equal(LastPixels, clip.GetFrame(3).Pixels);
        }
        else
        {
            var session = Assert.IsAssignableFrom<IDecodedMediaSession>(prepared.DecodedSession);
            await using var sample = Assert.IsType<DecodedVideoSample>(
                await session.ReadVideoAsync(TestContext.Current.CancellationToken));
            Assert.Equal(FirstPixels, sample.Image.Pixels.ToArray());
        }
    }

    /// <summary>Unrecognized sources and Arena profiles explicitly admit real CEL/FLC selections.</summary>
    /// <param name="arena">Whether the source declares the known Arena profile.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealNodeInAdmittedProfileIsCandidate(bool arena)
    {
        await using var mounted = await MountAsync(new MemoryFileSystem(FlicMediaFixture.Create()),
            arena ? GameProfiles.For(BethesdaGame.Arena) : null);
        Assert.True(FlicPreviewPreparation.IsCandidate(mounted.Snapshot, mounted.Node));
        Assert.False(FlicPreviewPreparation.IsCandidate(mounted.Snapshot,
            ((BethesdaBrowseSource)mounted.Snapshot.Source).Session.Root));
    }

    /// <summary>A different game's profile does not accidentally enter Arena-native FLC preparation.</summary>
    [Fact]
    public async Task DifferentGameProfileDeclinesBeforeRead()
    {
        var filesystem = new MemoryFileSystem(FlicMediaFixture.Create());
        await using var mounted = await MountAsync(filesystem, GameProfiles.For(BethesdaGame.Daggerfall));
        Assert.False(FlicPreviewPreparation.IsCandidate(mounted.Snapshot, mounted.Node));
        await Assert.ThrowsAsync<ArgumentException>(() => OpenAsync(mounted));
        Assert.Equal(0, filesystem.ReadCount);
    }

    /// <summary>Uses the production asynchronous preparation entry point with the current test cancellation token.</summary>
    private static Task<FlicPreviewPreparation> OpenAsync(Mounted mounted) =>
        FlicPreviewPreparation.OpenAsync(mounted.Snapshot, mounted.Node, TestContext.Current.CancellationToken);

    /// <summary>Opens one synthetic source through the real Shared source-retirement boundary.</summary>
    private static async Task<Mounted> MountAsync(IGameFileSystem filesystem, GameProfile? profile = null)
    {
        var browser = new BrowserSession();
        var source = Wrap(filesystem, profile);
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var node = Assert.Single(source.Session.Root.Children[0].Children);
        return new Mounted(browser, browser.Current!, node, filesystem);
    }

    /// <summary>Transfers the test filesystem to an ordinary BMT browse source and immutable tree.</summary>
    private static BethesdaBrowseSource Wrap(IGameFileSystem filesystem, GameProfile? profile = null) =>
        new(new AssetBrowseSession(filesystem, "synthetic", "synthetic", AssetTreeBuilder.Build(filesystem, "synthetic"), profile));

    /// <summary>Hashes synthetic encoded bytes for independent source/provenance assertions.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Retains the exact source opening for one ownership test.</summary>
    private sealed record Mounted(BrowserSession Browser, BrowserSnapshot Snapshot, AssetNode Node,
        IGameFileSystem FileSystem) : IAsyncDisposable
    {
        /// <inheritdoc />
        public ValueTask DisposeAsync() => Browser.DisposeAsync();
    }

    /// <summary>Returns borrowed synthetic bytes, actual provenance and deterministic synchronous read barriers.</summary>
    private sealed class MemoryFileSystem(byte[] bytes, string label = "memory") : IGameFileSystem
    {
        private int _disposals;
        private int _reads;
        /// <inheritdoc />
        public string Label => label;
        /// <summary>Observed source retirements.</summary>
        internal int DisposalCount => Volatile.Read(ref _disposals);
        /// <summary>Observed bounded source reads.</summary>
        internal int ReadCount => Volatile.Read(ref _reads);
        /// <summary>The actual read admission limit supplied by preparation.</summary>
        internal long LastMaximumBytes { get; private set; }
        /// <summary>Simulates an unreadable higher-priority layer.</summary>
        internal bool Unreadable { get; init; }
        /// <summary>Injects an actual source-owner retirement failure after counting the release attempt.</summary>
        internal Exception? DisposalFailure { get; init; }
        /// <summary>Overrides returned provenance to test rejection of another path.</summary>
        internal string? ReturnedPath { get; init; }
        /// <summary>Runs before returning the bounded read result.</summary>
        internal Action? AfterRead { get; set; }
        /// <inheritdoc />
        public bool Exists(string path) => VfsPath.Comparer.Equals(VfsPath.Normalize(path), VideoPath);
        /// <inheritdoc />
        public GameFileEntry? TryStat(string path) => Exists(path) ? new GameFileEntry(VideoPath, bytes.Length, Label) : null;
        /// <inheritdoc />
        public byte[]? TryReadAllBytes(string path) => throw new InvalidOperationException("Unexpected unbounded or second legacy read.");
        /// <inheritdoc />
        public GameFileReadResult? TryReadAllBytesBounded(string path, long maximumBytes)
        {
            ObjectDisposedException.ThrowIf(DisposalCount != 0, this);
            Interlocked.Increment(ref _reads);
            LastMaximumBytes = maximumBytes;
            var entry = TryStat(path);
            var result = !Unreadable && entry is not null && bytes.Length <= maximumBytes
                ? new GameFileReadResult(entry with { Path = ReturnedPath ?? entry.Path }, bytes) : null;
            AfterRead?.Invoke();
            return result;
        }
        /// <inheritdoc />
        public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null) =>
            VfsPath.MatchesPrefix(VideoPath, prefix is null ? null : VfsPath.Normalize(prefix))
                ? [new GameFileEntry(VideoPath, bytes.Length, Label)] : [];
        /// <inheritdoc />
        public GameFileEnumerationPage EnumerateFilesBounded(string? prefix, int maximumEntries)
        {
            var entries = EnumerateFiles(prefix).ToArray();
            return new GameFileEnumerationPage(entries.Take(maximumEntries).ToArray(), entries.Length > maximumEntries);
        }
        /// <inheritdoc />
        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            if (DisposalFailure is { } failure) { throw failure; }
        }
    }
}
