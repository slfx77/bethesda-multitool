using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Vfs;

/// <summary>
///     Pins the shared archive-handle registry lifecycle: same-instance dedup, dispose only after
///     the last lease, idempotent lease disposal, staleness replacement with the detached handle
///     staying readable, keep-warm parking, failed opens never cached, and the concurrent
///     acquire/release contract (no zero-resurrection of a disposed handle).
/// </summary>
public sealed class ArchiveHandleRegistryTests : IDisposable
{
    private readonly string _root;

    public ArchiveHandleRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vfs-registry-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; leaked temp dirs are harmless.
        }
    }

    private static byte[] PayloadFor(int index)
    {
        var payload = new byte[64 + index % 512];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((index * 31 + i * 7) & 0xFF);
        }

        return payload;
    }

    private string WriteBsa(string fileName, params (string Path, byte[] Data)[] files)
    {
        using var writer = new BsaWriter(false, embedFileNames: false);
        foreach (var (path, data) in files)
        {
            writer.AddFile(path, data);
        }

        var bsaPath = Path.Combine(_root, fileName);
        writer.Write(bsaPath);
        return bsaPath;
    }

    [Fact]
    public void Acquire_SamePath_SharesOneReader()
    {
        var bsa = WriteBsa("shared.bsa", ("meshes\\a.nif", PayloadFor(1)));
        var registry = new ArchiveHandleRegistry();

        using var lease1 = registry.Acquire(bsa);
        using var lease2 = registry.Acquire(bsa);

        Assert.Same(lease1.Reader, lease2.Reader);
        Assert.Equal(1, registry.OpenHandleCount);
        Assert.Equal(PayloadFor(1), lease2.Reader.ReadFile("meshes\\a.nif"));
    }

    [Fact]
    public void Reader_IsDisposedOnlyAfterLastLease()
    {
        var bsa = WriteBsa("refcount.bsa", ("meshes\\a.nif", PayloadFor(2)));
        var registry = new ArchiveHandleRegistry();

        var lease1 = registry.Acquire(bsa);
        var lease2 = registry.Acquire(bsa);

        lease1.Dispose();
        // Still held by lease2: reads must keep working.
        Assert.Equal(PayloadFor(2), lease2.Reader.ReadFile("meshes\\a.nif"));
        Assert.Equal(1, registry.OpenHandleCount);

        lease2.Dispose();
        Assert.Equal(0, registry.OpenHandleCount);

        // A fresh acquire opens a fresh handle (the old one is gone, not resurrected).
        using var lease3 = registry.Acquire(bsa);
        Assert.NotSame(lease2.Reader, lease3.Reader);
        Assert.Equal(PayloadFor(2), lease3.Reader.ReadFile("meshes\\a.nif"));
    }

    [Fact]
    public void LeaseDispose_IsIdempotent()
    {
        var bsa = WriteBsa("idempotent.bsa", ("meshes\\a.nif", PayloadFor(3)));
        var registry = new ArchiveHandleRegistry();

        var lease1 = registry.Acquire(bsa);
        var lease2 = registry.Acquire(bsa);

        lease1.Dispose();
        lease1.Dispose(); // must NOT double-decrement and kill lease2's handle

        Assert.Equal(1, registry.OpenHandleCount);
        Assert.Equal(PayloadFor(3), lease2.Reader.ReadFile("meshes\\a.nif"));
        lease2.Dispose();
        Assert.Equal(0, registry.OpenHandleCount);
    }

    [Fact]
    public void Acquire_AfterIdentityChange_ReplacesHandle_WhileOldLeaseStaysReadable()
    {
        var bsa = WriteBsa("stale.bsa", ("meshes\\a.nif", PayloadFor(4)));
        var registry = new ArchiveHandleRegistry();

        var oldLease = registry.Acquire(bsa);

        // The leased memory map holds FileShare.Read, so on Windows the data cannot be rewritten
        // or renamed under a live handle — a real replacement happens only on Linux (unlink +
        // recreate while mapped) or between full releases. Timestamps are attribute writes and
        // bypass share modes on every OS, so drive the (length, mtime) identity check via mtime.
        File.SetLastWriteTimeUtc(bsa, File.GetLastWriteTimeUtc(bsa).AddHours(1));

        using var newLease = registry.Acquire(bsa);
        Assert.NotSame(oldLease.Reader, newLease.Reader);
        Assert.Equal(PayloadFor(4), newLease.Reader.ReadFile("meshes\\a.nif"));

        // The detached handle keeps serving its snapshot until its own release.
        Assert.Equal(PayloadFor(4), oldLease.Reader.ReadFile("meshes\\a.nif"));
        oldLease.Dispose();

        // Only the replacement remains tracked.
        Assert.Equal(1, registry.OpenHandleCount);
    }

    [Fact]
    public void KeepWarm_ParksAtZero_AndSweepsOnLastScopeClose()
    {
        var bsa = WriteBsa("warm.bsa", ("meshes\\a.nif", PayloadFor(7)));
        var registry = new ArchiveHandleRegistry();

        using (registry.KeepWarm())
        {
            var lease = registry.Acquire(bsa);
            var reader = lease.Reader;
            lease.Dispose();

            // Parked, not disposed: the next acquire joins the same warm handle.
            Assert.Equal(1, registry.OpenHandleCount);
            using var again = registry.Acquire(bsa);
            Assert.Same(reader, again.Reader);
        }

        // Last scope closed with no leases outstanding: parked handle swept.
        Assert.Equal(0, registry.OpenHandleCount);
    }

    [Fact]
    public void KeepWarm_ClosingScope_DoesNotTouchLeasedHandles()
    {
        var bsa = WriteBsa("warm2.bsa", ("meshes\\a.nif", PayloadFor(8)));
        var registry = new ArchiveHandleRegistry();

        var scope = registry.KeepWarm();
        var lease = registry.Acquire(bsa);
        scope.Dispose();

        // Still leased: survives the sweep and keeps reading.
        Assert.Equal(1, registry.OpenHandleCount);
        Assert.Equal(PayloadFor(8), lease.Reader.ReadFile("meshes\\a.nif"));

        // With every scope gone, dispose-at-zero applies again.
        lease.Dispose();
        Assert.Equal(0, registry.OpenHandleCount);
    }

    [Fact]
    public void KeepWarm_NestedScopes_SweepOnlyWhenLastCloses()
    {
        var bsa = WriteBsa("warm3.bsa", ("meshes\\a.nif", PayloadFor(12)));
        var registry = new ArchiveHandleRegistry();

        var outer = registry.KeepWarm();
        var inner = registry.KeepWarm();
        registry.Acquire(bsa).Dispose();

        inner.Dispose();
        // The outer scope is still open: the parked handle must survive the inner close.
        Assert.Equal(1, registry.OpenHandleCount);
        using (var lease = registry.Acquire(bsa))
        {
            Assert.Equal(PayloadFor(12), lease.Reader.ReadFile("meshes\\a.nif"));
        }

        outer.Dispose();
        Assert.Equal(0, registry.OpenHandleCount);
    }

    [Fact]
    public void Acquire_ParkedStaleHandle_IsReplacedAndDisposed()
    {
        var bsa = WriteBsa("parked-stale.bsa", ("meshes\\a.nif", PayloadFor(13)));
        var registry = new ArchiveHandleRegistry();

        using (registry.KeepWarm())
        {
            var lease = registry.Acquire(bsa);
            var parkedReader = lease.Reader;
            lease.Dispose(); // parked at refcount 0 under the scope

            File.SetLastWriteTimeUtc(bsa, File.GetLastWriteTimeUtc(bsa).AddHours(1));

            // The stale parked handle has no holders: acquire must replace it (and dispose it —
            // it never returns to the map, so OpenHandleCount stays 1, the replacement).
            using var fresh = registry.Acquire(bsa);
            Assert.NotSame(parkedReader, fresh.Reader);
            Assert.Equal(PayloadFor(13), fresh.Reader.ReadFile("meshes\\a.nif"));
            Assert.Equal(1, registry.OpenHandleCount);
        }

        Assert.Equal(0, registry.OpenHandleCount);
    }

    /// <summary>
    ///     Single-flight identity: overlapping holders of one path — leases all held at once —
    ///     must share exactly one reader instance.
    /// </summary>
    [Fact]
    public void ConcurrentAcquire_OverlappingLeases_ShareOneReader()
    {
        var bsa = WriteBsa("single-flight.bsa", ("meshes\\a.nif", PayloadFor(14)));
        var registry = new ArchiveHandleRegistry();

        var leases = new ArchiveLease[16];
        Parallel.For(0, leases.Length, i => leases[i] = registry.Acquire(bsa));

        try
        {
            Assert.Equal(1, registry.OpenHandleCount);
            Assert.All(leases, lease => Assert.Same(leases[0].Reader, lease.Reader));
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }

        Assert.Equal(0, registry.OpenHandleCount);
    }

    [Fact]
    public void FailedOpen_Propagates_AndIsNotCached()
    {
        var missing = Path.Combine(_root, "not-yet.bsa");
        var registry = new ArchiveHandleRegistry();

        Assert.ThrowsAny<IOException>(() => registry.Acquire(missing));
        Assert.Equal(0, registry.OpenHandleCount);

        // Once the file exists, the same path opens cleanly (no poisoned entry).
        WriteBsa("not-yet.bsa", ("meshes\\a.nif", PayloadFor(9)));
        using var lease = registry.Acquire(missing);
        Assert.Equal(PayloadFor(9), lease.Reader.ReadFile("meshes\\a.nif"));
    }

    [Fact]
    public void SharedHandle_RejectsConversionToggles()
    {
        var bsa = WriteBsa("noconvert.bsa", ("meshes\\a.nif", PayloadFor(10)));
        var registry = new ArchiveHandleRegistry();

        using var lease = registry.Acquire(bsa);
        var extractor = lease.Reader.AsBsaExtractor;
        Assert.NotNull(extractor);
        Assert.Throws<InvalidOperationException>(() => extractor!.EnableNifConversion(true));
        Assert.Throws<InvalidOperationException>(() => extractor!.EnableDdxConversion(true));
        Assert.Throws<InvalidOperationException>(() => extractor!.EnableXmaConversion(true));
    }

    /// <summary>
    ///     The zero-resurrection race: workers hammering acquire → read → release on one path must
    ///     never observe a disposed reader, and the registry must end empty.
    /// </summary>
    [Fact]
    public void ConcurrentAcquireRelease_NeverServesADisposedReader()
    {
        var bsa = WriteBsa("race.bsa", ("meshes\\a.nif", PayloadFor(11)));
        var registry = new ArchiveHandleRegistry();
        var expected = PayloadFor(11);

        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            using var lease = registry.Acquire(bsa);
            var bytes = lease.Reader.ReadFile("meshes\\a.nif");
            Assert.NotNull(bytes);
            Assert.Equal(expected, bytes!);
        });

        Assert.Equal(0, registry.OpenHandleCount);
    }
}