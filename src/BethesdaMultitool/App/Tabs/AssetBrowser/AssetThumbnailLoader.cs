// LIFO-stack scheduling, the session model and the three-way staleness guard are ported from
// JimmyPCTool / AweMultitool (https://github.com/slfx77/JimmyPCTool, MIT licence) —
// src/AweMultitool/App/Tabs/ThumbnailLoader.cs. Retargeted onto AssetBrowseSession + ThumbnailCache.

using System.Collections.Concurrent;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Imaging;
using Microsoft.UI.Dispatching;

namespace BethesdaMultitool;

/// <summary>
///     Decodes gallery thumbnails in the background, <b>newest request first</b>, one source at a time.
///     <para>
///         A retail install holds tens of thousands of assets, so decoding them up front is out of
///         the question — and decoding them in list order is worse than useless, because after a
///         fling-scroll the tiles the user is actually looking at are the ones requested LAST.
///         Requests therefore go on a stack, not a queue.
///     </para>
///     <para>
///         Three guards stop the wrong image appearing. Opening a source starts a new
///         <i>session</i> and cancels the old one; the worker only touches its own session's state;
///         and the dispatcher callback that finally attaches a bitmap re-checks both that its
///         session is current and that the tile is still realized. That last check is the one that
///         matters — cancelling a token does nothing to a callback already queued on the UI thread.
///     </para>
/// </summary>
internal sealed class AssetThumbnailLoader : IDisposable
{
    private readonly ThumbnailCache _cache;
    private readonly int _cellPixels;
    private readonly double _deviceScale;
    private readonly DispatcherQueue _dispatcher;
    private bool _disposed;
    private Session? _session;

    public AssetThumbnailLoader(DispatcherQueue dispatcher, ThumbnailCache cache, int cellPixels, double deviceScale)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(cache);

        _dispatcher = dispatcher;
        _cache = cache;
        _cellPixels = cellPixels;
        _deviceScale = deviceScale;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _session, null)?.Dispose();
    }

    /// <summary>Abandons the previous session and starts one reading through <paramref name="source" />.</summary>
    public void BeginSession(AssetBrowseSession source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var session = new Session(source);

        // Publish before cancelling, so a worker checking whether it is current cannot see the old
        // session as the live one.
        var previous = Interlocked.Exchange(ref _session, session);
        previous?.Dispose();

        if (_disposed)
        {
            session.Dispose();
            return;
        }

        session.Start(this);
    }

    /// <summary>Queues a realized tile. UI thread only.</summary>
    public void Request(AssetGalleryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.IsRequested || item.Thumbnail is not null)
        {
            return;
        }

        var session = Volatile.Read(ref _session);
        if (session is null)
        {
            return;
        }

        item.IsRequested = true;
        session.Pending.Push(item);
        session.Signal.Release();
    }

    private void Work(Session session)
    {
        while (!session.Token.IsCancellationRequested)
        {
            try
            {
                session.Signal.Wait(session.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!session.Pending.TryPop(out var item))
            {
                continue;
            }

            // The tile may have scrolled away between being queued and being reached.
            if (!item.IsRealized)
            {
                item.IsRequested = false;
                continue;
            }

            Render(session, item);
        }
    }

    private void Render(Session session, AssetGalleryItem item)
    {
        byte[]? bgra = null;
        var width = 0;
        var height = 0;

        try
        {
            var key = new ThumbnailKey(session.Source.SourcePath, item.Node.VirtualPath, _cellPixels);
            var fitted = _cache.GetOrAdd(
                key, () => AssetThumbnailSource.TryRender(session.Source, item.Node, _cellPixels, session.Token));

            if (fitted is { Rgba: not null } thumbnail)
            {
                width = thumbnail.Width;
                height = thumbnail.Height;
                bgra = PremultipliedBgra.FromRgba(thumbnail.Rgba);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException
                                      or IOException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            // One unreadable asset must not end the session; the tile shows a broken marker. Over a
            // retail install some formats are simply not decoded yet, so this is expected traffic.
        }

        if (session.Token.IsCancellationRequested)
        {
            return;
        }

        var caption = bgra is null ? "no preview" : $"{width}×{height}";
        _dispatcher.TryEnqueue(() =>
        {
            // Both checks are required: the session may have been replaced, and the tile may have
            // been recycled, since this callback was queued.
            if (!ReferenceEquals(Volatile.Read(ref _session), session) || !item.IsRealized)
            {
                item.IsRequested = false;
                return;
            }

            item.SetCaption(caption);
            item.SetThumbnail(bgra is null ? null : AssetBitmapFactory.FromPremultipliedBgra(width, height, bgra),
                _deviceScale);
        });
    }

    /// <summary>One source's worth of decoding, cancelled as a unit.</summary>
    private sealed class Session : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;
        private Task? _worker;

        public Session(AssetBrowseSession source)
        {
            Source = source;
            Token = _cts.Token;
        }

        public AssetBrowseSession Source { get; }

        public ConcurrentStack<AssetGalleryItem> Pending { get; } = new();

        public SemaphoreSlim Signal { get; } = new(0);

        public CancellationToken Token { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                try
                {
                    _cts.Cancel();
                    Signal.Release();
                }
                finally
                {
                    // The worker only posts UI callbacks; it never waits for them. Drain it before
                    // the caller can release its file system or thumbnail cache.
                    _worker?.GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                // Cancellation is the normal way this worker stops.
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn("[AssetBrowser] Thumbnail worker failed during shutdown: {0}", ex.Message);
            }
            finally
            {
                Signal.Dispose();
                _cts.Dispose();
            }
        }

        public void Start(AssetThumbnailLoader owner)
        {
            _worker = Task.Factory.StartNew(
                () => owner.Work(this), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }
}
