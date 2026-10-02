using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Xngine.Flic;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.Core.Media;
using Slfx77.Multitool.Media.Playback;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>Owns one exact browser occurrence prepared for strict FLC playback or the existing detached classic route.</summary>
/// <remarks>The representable VFS winner is read once. Strict decoder limits do not bound that inherited whole read
/// or the unchanged permissive fallback. A decoded session owns its source lease until all retained samples retire.</remarks>
internal sealed class FlicPreviewPreparation : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private IAsyncDisposable? _owner;
    private IDecodedMediaSession? _decoded;
    private Task? _retirement;

    private FlicPreviewPreparation(BrowserSnapshot snapshot, AssetNode node, IAsyncDisposable lease)
    {
        Snapshot = snapshot;
        Node = node;
        _owner = lease;
    }

    /// <summary>The exact source opening, borrowed as identity while its lease is owned by the decoded session.</summary>
    internal BrowserSnapshot Snapshot { get; }
    /// <summary>The builder-owned real source leaf, never a reconstructed path-only selection.</summary>
    internal AssetNode Node { get; }
    /// <summary>The actual successful read layer, which may differ from the stat-first layer.</summary>
    internal GameFileEntry Provenance { get; private set; } = null!;
    /// <summary>SHA256 of the bytes actually read, independent of mutable labels and stat-first provenance.</summary>
    internal string EncodedSha256 { get; private set; } = string.Empty;
    /// <summary>A detached legacy result on strict decline, or null if neither content decoder supports the bytes.</summary>
    internal ClassicVideoClip? LegacyClip { get; private set; }
    /// <summary>The specific strict-admission decline; cancellation and I/O failure never become a fallback.</summary>
    internal string? StrictDeclineReason { get; private set; }
    /// <summary>The borrowed decoded session while this preparation still owns it.</summary>
    internal IDecodedMediaSession? DecodedSession { get { lock (_gate) { return _decoded; } } }

    /// <summary>Restricts new native admission to FLC/CEL leaves in Arena or an unclassified source.</summary>
    internal static bool IsCandidate(BrowserSnapshot snapshot, AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(node);
        if (snapshot.Source is not BethesdaBrowseSource source || node.Kind == AssetNodeKind.Folder ||
            source.Session.Profile is { Game: not BethesdaGame.Arena })
        {
            return false;
        }
        var extension = Path.GetExtension(node.Name);
        return extension.Equals(".flc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads and prepares off-thread while retaining the exact source before the first asynchronous boundary.</summary>
    /// <param name="snapshot">The source that owns the selected tree.</param>
    /// <param name="node">The actual selected leaf from that tree.</param>
    /// <param name="cancellationToken">Selection lifetime, linked to retirement of the browser source.</param>
    /// <returns>An owned result; successful native bridge creation must explicitly take its decoded session.</returns>
    internal static async Task<FlicPreviewPreparation> OpenAsync(BrowserSnapshot snapshot, AssetNode node,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(node);
        if (snapshot.Source is not BethesdaBrowseSource source || !IsCandidate(snapshot, node))
        {
            throw new ArgumentException("The selected source is not an Arena-style FLC/CEL candidate.", nameof(node));
        }
        var root = node;
        while (root.Parent is not null) { root = root.Parent; }
        if (!ReferenceEquals(root, source.Session.Root))
        {
            throw new ArgumentException("The selected leaf does not belong to this exact source tree.", nameof(node));
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(snapshot.CancellationToken, cancellationToken);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        BrowserLease lease;
        try { lease = snapshot.AcquireLease(); }
        catch (InvalidOperationException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        var result = new FlicPreviewPreparation(snapshot, node, lease);
        try
        {
            await Task.Run(() => result.Read(source.Session.FileSystem, token), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result._decoded is null)
            {
                // Legacy frames are detached. Their source prerequisite ends as soon as preparation drains.
                await result.DisposeAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            return result;
        }
        catch (Exception failure)
        {
            try { await result.DisposeAsync().ConfigureAwait(false); }
            catch (Exception retirementFailure)
            {
                var retained = new AggregateException("FLC preparation and/or source retirement failed.", failure, retirementFailure);
                retained.Data["RetainedFlicPreparation"] = result;
                throw retained;
            }
            throw;
        }
    }

    /// <summary>Transfers the session once, after a native bridge has successfully taken ownership.</summary>
    /// <returns>The same session for ownership assertions; no session or encoded data is copied.</returns>
    internal IDecodedMediaSession TakeDecodedSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retirement is not null, this);
            var result = _decoded ?? throw new InvalidOperationException("This preparation owns no decoded session.");
            _decoded = null;
            _owner = null;
            return result;
        }
    }

    /// <summary>Rejects transfer and observes the same terminal disposal, retaining a failed prerequisite without retry.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_retirement is not null) { return new ValueTask(_retirement); }
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = completion.Task;
        }
        _ = RetireAsync(completion);
        return new ValueTask(completion.Task);
    }

    /// <summary>Reads one actual winner, preserving the existing content-based FLC/VID fallback decision.</summary>
    private void Read(IGameFileSystem filesystem, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var read = filesystem.TryReadAllBytesBounded(Node.VirtualPath, int.MaxValue)
            ?? throw new IOException("The selected classic video could not be read.");
        token.ThrowIfCancellationRequested();
        if (!VfsPath.Comparer.Equals(VfsPath.Normalize(read.Entry.Path), VfsPath.Normalize(Node.VirtualPath)))
        {
            throw new InvalidDataException("The video read returned another virtual occurrence.");
        }
        Provenance = read.Entry;
        EncodedSha256 = Convert.ToHexString(SHA256.HashData(read.Data));
        token.ThrowIfCancellationRequested();
        var identity = new JsonObject
        {
            ["source"] = Snapshot.Source.Id,
            ["generation"] = Snapshot.Generation,
            ["path"] = Node.VirtualPath,
            ["provenance"] = new JsonObject
            {
                ["Path"] = Provenance.Path,
                ["Size"] = Provenance.Size,
                ["Source"] = Provenance.Source
            },
            ["sha256"] = EncodedSha256
        }.ToJsonString();
        FlicDecodedMediaDecoder decoder;
        try
        {
            decoder = new FlicDecodedMediaDecoder(read.Data, Node.Name, identity,
                retainedInput: _owner, cancellationToken: token);
        }
        catch (Exception decline) when (decline is InvalidDataException or NotSupportedException)
        {
            token.ThrowIfCancellationRequested();
            StrictDeclineReason = decline.Message;
            LegacyClip = ClassicVideoClip.TryOpenBytes(read.Data, Node.Name);
            token.ThrowIfCancellationRequested();
            return;
        }
        _owner = decoder;
        var format = decoder.Description.Tracks[0].Video!;
        var session = new DecodedMediaSession(decoder, new DecodedMediaSessionLimits
        {
            MaximumRetainedVideoSamples = 2,
            MaximumVideoSampleBytes = checked(format.StoredWidth * format.StoredHeight * 4)
        });
        _owner = session;
        _decoded = session;
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Releases one owner while retaining the reference when its terminal cleanup fails.</summary>
    private async Task ReleaseOwnerAsync()
    {
        if (_owner is { } owner) { await owner.DisposeAsync().ConfigureAwait(false); }
        _owner = null;
        _decoded = null;
    }

    /// <summary>Completes the already-published retirement after the source/session has fully drained.</summary>
    private async Task RetireAsync(TaskCompletionSource completion)
    {
        try { await ReleaseOwnerAsync().ConfigureAwait(false); completion.TrySetResult(); }
        catch (Exception failure) { completion.TrySetException(failure); }
    }
}
