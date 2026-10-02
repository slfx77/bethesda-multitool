namespace BethesdaMultitool.Core.WorldData;

/// <summary>Dispatches only the newest revision while the receiving view still owns this binding.</summary>
internal sealed class WorldAssetBinding : IDisposable
{
    private readonly WorldViewData _data;
    private readonly Func<Action, bool> _dispatch;
    private readonly Action _refresh;
    private readonly IDisposable _watch;
    private long _request;
    private int _disposed;

    internal WorldAssetBinding(WorldViewData data, Func<Action, bool> dispatch, Action refresh)
    {
        _data = data;
        _dispatch = dispatch;
        _refresh = refresh;
        data.AssetSourcesChanged += Changed;
        _watch = data.WatchAssetChanges();
    }

    private void Changed(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var request = Interlocked.Increment(ref _request);
        _dispatch(() =>
        {
            if (Volatile.Read(ref _disposed) == 0 && request == Volatile.Read(ref _request)) _refresh();
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Increment(ref _request);
        _data.AssetSourcesChanged -= Changed;
        _watch.Dispose();
    }
}
