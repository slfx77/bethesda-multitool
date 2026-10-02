using System.Collections.Immutable;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Assets;

/// <summary>Candidate-preserving reads over shared archive handles. Caller drains readers before disposal.</summary>
internal sealed class AssetSelectionSession : IDisposable
{
    internal const long MaximumPayloadBytes = 512L * 1024 * 1024;
    private const int MaximumReceipts = 4096;
    private readonly Layer[] _layers;
    private readonly object _receiptGate = new();
    private readonly Queue<AssetSelectionReceipt> _receipts = new();
    private readonly Dictionary<string, AssetSelectionReceipt> _last = new(StringComparer.OrdinalIgnoreCase);
    private long _sequence;
    private bool _disposed;
    private readonly ReaderWriterLockSlim _admission = new(LockRecursionPolicy.SupportsRecursion);
    private readonly AsyncLocal<AssetReadScope?> _scope = new();

    internal AssetReadScope CaptureReads() => new(this, _scope.Value);
    internal void ObserveCached(IEnumerable<AssetSelectionReceipt> receipts)
    {
        foreach (var receipt in receipts) _scope.Value?.Add(receipt);
    }

    internal AssetSelectionSession(AssetSourcePlan plan, CancellationToken cancellationToken = default)
    {
        Plan = plan;
        var layers = new List<Layer>();
        try
        {
            foreach (var mount in plan.Mounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                layers.Add(new Layer(mount));
            }
            _layers = layers.ToArray();
        }
        catch { foreach (var layer in layers) layer.Dispose(); throw; }
    }

    internal AssetSourcePlan Plan { get; }
    internal long Sequence { get { lock (_receiptGate) return _sequence; } }
    internal bool ReceiptsTruncated { get; private set; }
    internal AssetSelectionReceipt? LastReceipt(string path)
    {
        lock (_receiptGate) return _last.GetValueOrDefault(Normalize(path));
    }
    /// <summary>Rechecks stat identities, including new higher-priority loose candidates. Not a content-hash check.</summary>
    internal bool IsCurrent(AssetSelectionReceipt receipt) =>
        receipt.Status == AssetSelectionStatus.Selected && receipt.Attempts.All(a => a.Status == "read") &&
        receipt.PlanIdentity == Plan.Identity && Probe(receipt.RequestedPath).Candidates.SequenceEqual(receipt.Candidates);

    internal string StatIdentity(string path)
    {
        var probe = Probe(path);
        var text = Plan.Identity + "|" + probe.Status + "|" + string.Join(";", probe.Candidates) +
            "|" + string.Join(";", probe.Attempts);
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }
    internal ImmutableArray<AssetSelectionReceipt> Receipts(long afterSequence = 0)
    {
        lock (_receiptGate) return _receipts.Where(r => r.Sequence > afterSequence).ToImmutableArray();
    }

    internal AssetSelectionReceipt Probe(string path, CancellationToken cancellationToken = default)
    {
        _admission.EnterReadLock();
        try
        {
        var (normalized, candidates, attempts) = Find(path, cancellationToken);
        var first = candidates.FirstOrDefault();
        var ambiguous = first is not null && candidates.Count(c => c.MountIndex == first.MountIndex) > 1;
        return new(Plan.Policy, Plan.Identity, normalized,
            ambiguous ? AssetSelectionStatus.Ambiguous : first is not null ? AssetSelectionStatus.Selected :
            attempts.Count > 0 ? AssetSelectionStatus.Unavailable : AssetSelectionStatus.Missing,
            candidates, ambiguous ? null : first, attempts.ToImmutable());
        }
        finally { _admission.ExitReadLock(); }
    }

    internal SelectedAssetRead<byte[]> Read(string path, long maximumBytes = MaximumPayloadBytes,
        CancellationToken cancellationToken = default) => Read(path, static bytes => bytes, maximumBytes, cancellationToken);

    internal SelectedAssetRead<T> Read<T>(string path, Func<byte[], T?> decode,
        long maximumBytes = MaximumPayloadBytes, CancellationToken cancellationToken = default) where T : class
    {
        _admission.EnterReadLock();
        try
        {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, int.MaxValue);
        var (normalized, candidates, attempts) = Find(path, cancellationToken);
        foreach (var group in candidates.GroupBy(c => c.MountIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Count() != 1)
            {
                attempts.Add(new(null, group.Key, "ambiguous-physical-entries")
                    { DeclaredMount = _layers[group.Key].Snapshot });
                return Finish<T>(normalized, AssetSelectionStatus.Ambiguous, candidates, null, attempts, null, null);
            }
            var candidate = group.First();
            try
            {
                var bytes = _layers[group.Key].Read(candidate, maximumBytes);
                cancellationToken.ThrowIfCancellationRequested();
                var value = decode(bytes);
                cancellationToken.ThrowIfCancellationRequested();
                if (value is null) { attempts.Add(new(candidate, group.Key, "decode-unavailable")); continue; }
                attempts.Add(new(candidate, group.Key, "read"));
                return Finish(normalized, AssetSelectionStatus.Selected, candidates, candidate, attempts,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException or OverflowException)
            {
                attempts.Add(new(candidate, group.Key, $"read-failed:{ex.GetType().Name}"));
            }
        }
        return Finish<T>(normalized, candidates.Length == 0 && attempts.Count == 0
            ? AssetSelectionStatus.Missing : AssetSelectionStatus.Unavailable, candidates, null, attempts, null, null);
        }
        finally { _admission.ExitReadLock(); }
    }

    private SelectedAssetRead<T> Finish<T>(string path, AssetSelectionStatus status,
        ImmutableArray<AssetCandidate> candidates, AssetCandidate? selected,
        ImmutableArray<AssetReadAttempt>.Builder attempts, string? hash, T? value) where T : class
    {
        lock (_receiptGate)
        {
            var receipt = new AssetSelectionReceipt(Plan.Policy, Plan.Identity, path, status, candidates,
                selected, attempts.ToImmutable(), hash, ++_sequence);
            if (_receipts.Count == MaximumReceipts)
            {
                var old = _receipts.Dequeue();
                if (_last.GetValueOrDefault(old.RequestedPath)?.Sequence == old.Sequence) _last.Remove(old.RequestedPath);
                ReceiptsTruncated = true;
            }
            _receipts.Enqueue(receipt);
            _last[path] = receipt;
            _scope.Value?.Add(receipt);
            return new(receipt, value);
        }
    }

    private (string Path, ImmutableArray<AssetCandidate> Candidates, ImmutableArray<AssetReadAttempt>.Builder Attempts)
        Find(string path, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        var normalized = Normalize(path);
        var candidates = ImmutableArray.CreateBuilder<AssetCandidate>();
        var attempts = ImmutableArray.CreateBuilder<AssetReadAttempt>();
        for (var i = 0; i < _layers.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            try { candidates.AddRange(_layers[i].Find(normalized, i)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            { attempts.Add(new(null, i, $"source-unavailable:{ex.GetType().Name}")
                { DeclaredMount = _layers[i].Snapshot }); }
        }
        return (normalized, candidates.ToImmutable(), attempts);
    }

    internal static string Normalize(string path)
    {
        var normalized = path.Replace('/', '\\').TrimStart('\\');
        if (normalized.Length == 0 || normalized.Contains(':') || normalized.Split('\\').Any(s => s is ".." or "." or ""))
            throw new ArgumentException("Expected a data-relative asset path.", nameof(path));
        return normalized;
    }

    public void Dispose()
    {
        _admission.EnterWriteLock();
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var layer in _layers) layer.Dispose();
        }
        finally { _admission.ExitWriteLock(); }
    }

    internal sealed class AssetReadScope : IDisposable
    {
        private readonly AssetSelectionSession _owner;
        private readonly AssetReadScope? _parent;
        private readonly List<AssetSelectionReceipt> _items = [];
        internal AssetReadScope(AssetSelectionSession owner, AssetReadScope? parent)
        { _owner = owner; _parent = parent; owner._scope.Value = this; }
        internal IReadOnlyList<AssetSelectionReceipt> Receipts { get { lock (_items) return _items.Distinct().ToArray(); } }
        internal void Add(AssetSelectionReceipt item) { lock (_items) _items.Add(item); _parent?.Add(item); }
        public void Dispose() => _owner._scope.Value = _parent;
    }

    private sealed class Layer(AssetMount mount) : IDisposable
    {
        private readonly object _gate = new();
        private ArchiveLease? _lease;
        internal AssetMountSnapshot Snapshot { get; } = new(mount.Path, mount.Kind, mount.Role, mount.Origin,
            mount.SourceLength, mount.SourceWriteTicks);
        private ArchiveReader Reader
        {
            get
            {
                lock (_gate)
                {
                    Validate(mount.Path, mount.SourceLength, mount.SourceWriteTicks);
                    var reader = (_lease ??= ArchiveHandleRegistry.Shared.Acquire(mount.Path)).Reader;
                    Validate(mount.Path, mount.SourceLength, mount.SourceWriteTicks);
                    return reader;
                }
            }
        }

        internal IEnumerable<AssetCandidate> Find(string path, int rank)
        {
            if (mount.Kind == AssetMountKind.LooseDirectory)
            {
                if (!Directory.Exists(mount.Path))
                    throw new IOException("Declared loose asset directory is unavailable.");
                var file = new FileInfo(System.IO.Path.Combine(mount.Path, path.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
                if (file.Exists) yield return new(rank, file.FullName, path, 0, null, file.Length,
                    mount.Role, file.Length, file.LastWriteTimeUtc.Ticks);
                yield break;
            }
            var reader = Reader;
            var source = new FileInfo(mount.Path);
            foreach (var entry in reader.FindPhysicalEntries(path))
                yield return new(rank, mount.Path, path, entry.Occurrence, entry.Entry.Offset, entry.Entry.Size,
                    mount.Role, source.Length, source.LastWriteTimeUtc.Ticks,
                    entry.Entry.Record is BsaFileRecord bsa ? bsa.NameHash :
                        entry.Entry.Record is Ba2FileRecord ba2 ? ba2.NameHash : null,
                    entry.Entry.Record is BsaFileRecord bsaSize ? bsaSize.RawSize : null);
        }

        internal byte[] Read(AssetCandidate candidate, long maximum)
        {
            Validate(candidate.SourcePath, candidate.SourceLength, candidate.SourceWriteTicks);
            if (mount.Kind == AssetMountKind.LooseDirectory)
            {
                using var stream = new FileStream(candidate.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > maximum) throw new InvalidDataException("Asset exceeds the read limit.");
                var bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                Validate(candidate.SourcePath, candidate.SourceLength, candidate.SourceWriteTicks);
                return bytes;
            }
            var physical = Reader.FindPhysicalEntries(candidate.VirtualPath)
                .Single(entry => entry.Occurrence == candidate.Occurrence);
            var data = Reader.ExtractBounded(physical.Entry, maximum);
            Validate(candidate.SourcePath, candidate.SourceLength, candidate.SourceWriteTicks);
            return data;
        }

        private static void Validate(string path, long? length, long? ticks)
        {
            var file = new FileInfo(path);
            if (!file.Exists || length is null || ticks is null || file.Length != length || file.LastWriteTimeUtc.Ticks != ticks)
                throw new IOException("Asset source changed; create a new source plan.");
        }

        public void Dispose() { lock (_gate) { _lease?.Dispose(); _lease = null; } }
    }
}
