using System.Buffers.Binary;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Caching;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Exercises real shared disk persistence using small synthetic sources and actual VFS resolution.</summary>
public sealed class DdsThumbnailProducerTests
{
    /// <summary>Fresh application owners and reopened/labeled source sessions reuse stored pixels without source mutation.</summary>
    [Theory]
    [InlineData("texture.dds")]
    [InlineData("texture.DDX")]
    public void ReopenedSourceUsesPersistentPixelsAndIndependentBuffers(string name)
    {
        using var fixture = new Fixture();
        var original = Dds(0xf800);
        fixture.Write("source", name, original);
        using (var first = fixture.Open("first display label"))
        {
            var (image, hit) = Render(fixture.Producer(), first, name);
            Assert.False(hit);
            AssertSolid(image, 255, 0);
            image.Rgba[0] = 0;
        }
        Assert.Single(Directory.EnumerateFiles(fixture.CachePath, "*.thumb", SearchOption.AllDirectories));
        using (var reopened = fixture.Open("different localized label"))
        {
            var (image, hit) = Render(fixture.Producer(), reopened, name);
            Assert.True(hit);
            AssertSolid(image, 255, 0);
            image.Rgba[1] = 42;
            var (again, againHit) = Render(fixture.Producer(), reopened, name);
            Assert.True(againHit);
            AssertSolid(again, 255, 0);
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.File("source", name)));
    }

    /// <summary>Clearing the producer's real shared owner removes persistence while retained pixels and source data survive.</summary>
    [Fact]
    public void ClearAndReopenRetainCurrentPixelsAndRegenerateStoredArtwork()
    {
        using var fixture = new Fixture();
        var original = Dds(0xf800);
        fixture.Write("source", "texture.dds", original);
        using var session = fixture.Open();
        var cache = fixture.CacheOwner();
        var (retained, firstHit) = Render(new DdsThumbnailProducer(cache), session, "texture.dds");
        Assert.False(firstHit);
        Assert.True(cache.Clear(TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(fixture.CachePath, "*.thumb", SearchOption.AllDirectories));
        AssertSolid(retained, 255, 0);
        Assert.Equal(original, File.ReadAllBytes(fixture.File("source", "texture.dds")));
        var reopened = fixture.Producer();
        var (regenerated, afterClearHit) = Render(reopened, session, "texture.dds");
        Assert.False(afterClearHit);
        AssertSolid(regenerated, 255, 0);
        Assert.True(Render(reopened, session, "texture.dds").Hit);
        AssertSolid(retained, 255, 0);
    }

    /// <summary>Equal-length replacement with unchanged timestamp, pixel size and scale each have distinct identities.</summary>
    [Fact]
    public void ContentSizeAndScaleInvalidateWithoutChangingPixelPolicy()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("source", "texture.dds", Dds(0xf800));
        using var session = fixture.Open();
        var producer = fixture.Producer();
        Assert.False(Render(producer, session, "texture.dds").Hit);
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, Dds(0x07e0));
        File.SetLastWriteTimeUtc(path, time);
        var (green, changedHit) = Render(producer, session, "texture.dds");
        Assert.False(changedHit);
        AssertSolid(green, 0, 255);
        var (small, sizeHit) = Render(producer, session, "texture.dds", 2);
        Assert.False(sizeHit);
        Assert.Equal((2, 2), (small.Width, small.Height));
        AssertSolid(small, 0, 255);
        var (scaled, scaleHit) = Render(producer, session, "texture.dds", 2, 1.5);
        Assert.False(scaleHit);
        Assert.Equal(small.Rgba, scaled.Rgba);
        Assert.True(Render(fixture.Producer(), session, "texture.dds", 2, 1.5).Hit);
    }

    /// <summary>Distinct physical roots and distinct merged paths remain distinct occurrences even for identical bytes.</summary>
    [Fact]
    public void SourceRootAndOccurrenceDoNotAlias()
    {
        using var fixture = new Fixture();
        fixture.Write("source", "a.dds", Dds(0xf800));
        fixture.Write("source", "b.dds", Dds(0xf800));
        fixture.Write("other", "a.dds", Dds(0xf800));
        using var first = fixture.Open();
        Assert.False(Render(fixture.Producer(), first, "a.dds").Hit);
        Assert.False(Render(fixture.Producer(), first, "b.dds").Hit);
        using var other = fixture.Open(directory: "other");
        Assert.False(Render(fixture.Producer(), other, "a.dds").Hit);
        Assert.Equal(3, Directory.EnumerateFiles(fixture.CachePath, "*.thumb", SearchOption.AllDirectories).Count());
    }

    /// <summary>Equal pixels from a different winning layer do not reuse the earlier layer's identity.</summary>
    [Fact]
    public void LayerOrderAndReadableFallbackUseActualProvenance()
    {
        using var fixture = new Fixture();
        fixture.Write("upper", "texture.dds", Dds(0xf800));
        fixture.Write("lower", "texture.dds", Dds(0xf800));
        using (var upperFirst = fixture.OpenLayers("upper", "lower"))
        {
            Assert.False(Render(fixture.Producer(), upperFirst, "texture.dds").Hit);
        }
        using (var lowerFirst = fixture.OpenLayers("lower", "upper"))
        {
            Assert.False(Render(fixture.Producer(), lowerFirst, "texture.dds").Hit);
        }
        var unavailable = new ObservedFileSystem(new LooseFileSystem(fixture.Directory("upper")))
        {
            ReadUnavailable = true
        };
        using var fallback = fixture.OpenWith(new LayeredGameFileSystem(
            [unavailable, new LooseFileSystem(fixture.Directory("lower"))]));
        Assert.Equal(fixture.Directory("upper"), fallback.FileSystem.TryStat("texture.dds")!.Source);
        var (image, hit) = Render(fixture.Producer(), fallback, "texture.dds");
        Assert.True(hit); // Same actual lower layer as the prior opening, despite upper stat metadata.
        AssertSolid(image, 255, 0);
        Assert.Equal(1, unavailable.Reads);
        Assert.False(unavailable.Disposed);
    }

    /// <summary>A readable but undecodable override is not replaced by a lower layer or cached as a negative result.</summary>
    [Fact]
    public void UndecodablePrimaryPreservesExistingLayerPolicy()
    {
        using var fixture = new Fixture();
        fixture.Write("upper", "texture.dds", new byte[136]);
        fixture.Write("lower", "texture.dds", Dds(0x07e0));
        using var session = fixture.OpenLayers("upper", "lower");
        var result = fixture.Producer().TryRender(session, Node(session, "texture.dds"), 4, 1,
            TestContext.Current.CancellationToken, out var hit);
        Assert.Null(result);
        Assert.False(hit);
        Assert.Empty(Directory.EnumerateFiles(fixture.CachePath, "*.thumb", SearchOption.AllDirectories));
        fixture.Write("upper", "texture.dds", Dds(0xf800));
        var (image, repairedHit) = Render(fixture.Producer(), session, "texture.dds");
        Assert.False(repairedHit);
        AssertSolid(image, 255, 0);
    }

    /// <summary>Corrupt optional storage is regenerated from the same unchanged source.</summary>
    [Fact]
    public void CorruptEntryDoesNotBreakDecoding()
    {
        using var fixture = new Fixture();
        fixture.Write("source", "texture.dds", Dds(0xf800));
        using var session = fixture.Open();
        Assert.False(Render(fixture.Producer(), session, "texture.dds").Hit);
        var stored = Assert.Single(Directory.EnumerateFiles(fixture.CachePath, "*.thumb", SearchOption.AllDirectories));
        File.WriteAllBytes(stored, [1, 2, 3]);
        var (image, hit) = Render(fixture.Producer(), session, "texture.dds");
        Assert.False(hit);
        AssertSolid(image, 255, 0);
        Assert.True(Render(fixture.Producer(), session, "texture.dds").Hit);
    }

    /// <summary>An unavailable dedicated cache directory leaves normal decoding usable.</summary>
    [Fact]
    public void StorageFailureLeavesPixelsAvailable()
    {
        using var fixture = new Fixture();
        fixture.Write("source", "texture.dds", Dds(0xf800));
        File.WriteAllText(fixture.CachePath, "not a directory");
        var owner = fixture.CacheOwner();
        var producer = new DdsThumbnailProducer(owner);
        using var session = fixture.Open();
        var (image, hit) = Render(producer, session, "texture.dds");
        Assert.False(hit);
        AssertSolid(image, 255, 0);
        Assert.NotNull(owner.LastError);
    }

    /// <summary>Construction and pre-canceled work perform neither source reads nor cache initialization.</summary>
    [Fact]
    public async Task PreCancellationPropagatesWithoutSourceOrCacheIo()
    {
        using var fixture = new Fixture();
        fixture.Write("source", "texture.dds", Dds(0xf800));
        var observed = new ObservedFileSystem(new LooseFileSystem(fixture.Directory("source")));
        using var session = fixture.OpenWith(observed);
        var producer = fixture.Producer();
        Assert.False(Directory.Exists(fixture.CachePath));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await canceled.CancelAsync();
        var error = Assert.Throws<OperationCanceledException>(() => producer.TryRender(session,
            Node(session, "texture.dds"), 4, 1, canceled.Token, out _));
        Assert.Equal(canceled.Token, error.CancellationToken);
        Assert.Equal(0, observed.Reads);
        Assert.False(Directory.Exists(fixture.CachePath));
    }

    /// <summary>Cancellation arriving during the synchronous source read is observed before cache opening/publication.</summary>
    [Fact]
    public void CancellationAfterReadDoesNotBecomeAMissOrStore()
    {
        using var fixture = new Fixture();
        fixture.Write("source", "texture.dds", Dds(0xf800));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var observed = new ObservedFileSystem(new LooseFileSystem(fixture.Directory("source")))
        {
            AfterRead = canceled.Cancel
        };
        using var session = fixture.OpenWith(observed);
        var error = Assert.Throws<OperationCanceledException>(() => fixture.Producer().TryRender(session,
            Node(session, "texture.dds"), 4, 1, canceled.Token, out _));
        Assert.Equal(canceled.Token, error.CancellationToken);
        Assert.Equal(1, observed.Reads);
        Assert.False(observed.Disposed);
        Assert.False(Directory.Exists(fixture.CachePath));
    }

    /// <summary>An equal path from another opening cannot cross the captured source owner.</summary>
    [Fact]
    public void ForeignNodeIsRejectedBeforeReading()
    {
        using var fixture = new Fixture();
        fixture.Write("source", "texture.dds", Dds(0xf800));
        var observed = new ObservedFileSystem(new LooseFileSystem(fixture.Directory("source")));
        using var first = fixture.OpenWith(observed);
        using var other = fixture.Open();
        Assert.Throws<ArgumentException>(() => fixture.Producer().TryRender(first,
            Node(other, "texture.dds"), 4, 1, TestContext.Current.CancellationToken, out _));
        Assert.Equal(0, observed.Reads);
        Assert.False(Directory.Exists(fixture.CachePath));
    }

    /// <summary>The existing sprite palette route stays live and uncached until companion identity is available.</summary>
    [Fact]
    public void SpriteCompanionChangesRemainOnExistingPipeline()
    {
        using var fixture = new Fixture();
        fixture.Write("source", "ART.FRM", Frm());
        var green = new byte[768];
        green[7 * 3 + 1] = 63;
        var red = new byte[768];
        red[7 * 3] = 63;
        fixture.Write("source", "ART.PAL", green);
        fixture.Write("source", "COLOR.PAL", red);
        using var session = fixture.Open();
        var first = AssetThumbnailSource.TryRender(session, Node(session, "ART.FRM"), 2,
            persistentDds: fixture.Producer(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(first.HasValue);
        AssertSolid(first.Value, 0, 255);
        fixture.Write("source", "ART.PAL", red);
        var second = AssetThumbnailSource.TryRender(session, Node(session, "ART.FRM"), 2,
            persistentDds: fixture.Producer(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(second.HasValue);
        AssertSolid(second.Value, 255, 0);
        Assert.False(Directory.Exists(fixture.CachePath));
    }

    /// <summary>Runs the real producer with the current test cancellation token and requires an image.</summary>
    private static (RgbaThumbnail Image, bool Hit) Render(DdsThumbnailProducer producer,
        AssetBrowseSession session, string name, int cellPixels = 4, double deviceScale = 1)
    {
        var result = producer.TryRender(session, Node(session, name), cellPixels, deviceScale,
            TestContext.Current.CancellationToken, out var hit);
        Assert.True(result.HasValue);
        return (result.Value, hit);
    }

    /// <summary>Returns one exact builder-owned occurrence, not a reconstructed path-only node.</summary>
    private static AssetNode Node(AssetBrowseSession session, string name) =>
        Assert.Single(session.Root.Children, node => node.Name == name);

    /// <summary>Checks every RGBA pixel against the independently authored opaque source color.</summary>
    private static void AssertSolid(RgbaThumbnail image, byte red, byte green)
    {
        Assert.Equal(image.Width * image.Height * 4, image.Rgba.Length);
        for (var index = 0; index < image.Rgba.Length; index += 4)
        {
            Assert.Equal(red, image.Rgba[index]);
            Assert.Equal(green, image.Rgba[index + 1]);
            Assert.Equal((byte)0, image.Rgba[index + 2]);
            Assert.Equal((byte)255, image.Rgba[index + 3]);
        }
    }

    /// <summary>Authors a complete 136-byte solid BC1 DDS; DDX suffix coverage is routing, not a new DDX codec.</summary>
    private static byte[] Dds(ushort color)
    {
        var bytes = new byte[136];
        "DDS "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x21007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 4);
        "DXT1"u8.CopyTo(bytes.AsSpan(84));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(128), color);
        return bytes;
    }

    /// <summary>Authors one 2x2 indexed FRM frame using the documented fixed header and palette index seven.</summary>
    private static byte[] Frm()
    {
        var header = new byte[FalloutFrmFile.HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, FalloutFrmFile.RetailVersion);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 1);
        var frame = new byte[FalloutFrmFile.FrameHeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(frame, 2);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(FalloutFrmFile.FrameAreaSizePosition),
            (uint)(frame.Length + 4));
        return [.. header, .. frame, 7, 7, 7, 7];
    }

    /// <summary>Owns only this test's temporary synthetic files and optional shared cache directory.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Directory.CreateTempSubdirectory("bmt-dds-cache-").FullName;
        /// <summary>The dedicated optional storage path, separate from all synthetic sources.</summary>
        internal string CachePath => Path.Combine(_root, "cache");
        /// <summary>Locates a test-owned source layer.</summary>
        internal string Directory(string name) => Path.Combine(_root, name);
        /// <summary>Locates one synthetic source file.</summary>
        internal string File(string directory, string name) => Path.Combine(Directory(directory), name);
        /// <summary>Writes only an explicitly authored bounded synthetic source.</summary>
        internal string Write(string directory, string name, byte[] bytes)
        {
            System.IO.Directory.CreateDirectory(Directory(directory));
            var path = File(directory, name);
            System.IO.File.WriteAllBytes(path, bytes);
            return path;
        }
        /// <summary>Creates a fresh application owner using the existing shared default quotas.</summary>
        internal ApplicationThumbnailCache CacheOwner() => new("BmtDdsThumbnailTests", CachePath);
        /// <summary>Creates a producer without opening its dedicated storage.</summary>
        internal DdsThumbnailProducer Producer() => new(CacheOwner());
        /// <summary>Opens one real loose source with independent display label and stable source path.</summary>
        internal AssetBrowseSession Open(string label = "source", string directory = "source") =>
            OpenWith(new LooseFileSystem(Directory(directory)), label, Directory(directory));
        /// <summary>Opens the actual ordered pair of test-owned loose layers.</summary>
        internal AssetBrowseSession OpenLayers(string first, string second) =>
            OpenWith(new LayeredGameFileSystem([new LooseFileSystem(Directory(first)),
                new LooseFileSystem(Directory(second))]));
        /// <summary>Owns the supplied filesystem and builds real source-tree occurrences.</summary>
        internal AssetBrowseSession OpenWith(IGameFileSystem fileSystem, string label = "source", string? sourcePath = null) =>
            new(fileSystem, label, sourcePath ?? _root, AssetTreeBuilder.Build(fileSystem, label));
        /// <summary>Deletes only the fresh temporary directory owned by this fixture.</summary>
        public void Dispose() => System.IO.Directory.Delete(_root, recursive: true);
    }

    /// <summary>Observes real loose reads and simulates an unreadable override without substituting stat provenance.</summary>
    private sealed class ObservedFileSystem(IGameFileSystem inner) : IGameFileSystem
    {
        /// <summary>Counts actual bounded payload read attempts.</summary>
        internal int Reads { get; private set; }
        /// <summary>Records whether the source owner released this wrapper.</summary>
        internal bool Disposed { get; private set; }
        /// <summary>Simulates an unreadable layer while leaving its stat result intact.</summary>
        internal bool ReadUnavailable { get; init; }
        /// <summary>Allows deterministic cancellation after a synchronous source read.</summary>
        internal Action? AfterRead { get; init; }
        /// <summary>Retains actual underlying layer identity.</summary>
        public string Label => inner.Label;
        /// <summary>Delegates physical existence unchanged.</summary>
        public bool Exists(string path) => inner.Exists(path);
        /// <summary>Retains the original stat metadata even for an unreadable simulated override.</summary>
        public GameFileEntry? TryStat(string path) => inner.TryStat(path);
        /// <summary>Preserves the simulated read availability for the legacy unbounded path.</summary>
        public byte[]? TryReadAllBytes(string path) => ReadUnavailable ? null : inner.TryReadAllBytes(path);
        /// <summary>Observes one real bounded read without replacing its returned provenance.</summary>
        public GameFileReadResult? TryReadAllBytesBounded(string path, long maximumBytes)
        {
            Reads++;
            var read = ReadUnavailable ? null : inner.TryReadAllBytesBounded(path, maximumBytes);
            AfterRead?.Invoke();
            return read;
        }
        /// <summary>Delegates actual source-tree enumeration.</summary>
        public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null) => inner.EnumerateFiles(prefix);
        /// <summary>Delegates bounded source-tree enumeration.</summary>
        public GameFileEnumerationPage EnumerateFilesBounded(string? prefix, int maximumEntries) =>
            inner.EnumerateFilesBounded(prefix, maximumEntries);
        /// <summary>Releases the wrapped source exactly when its owning session disposes it.</summary>
        public void Dispose()
        {
            Disposed = true;
            inner.Dispose();
        }
    }
}
