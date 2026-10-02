namespace BethesdaMultitool.Core.Assets;

/// <summary>Bounded actual-read dependencies; eviction changes admission keys, never loses provenance silently.</summary>
internal sealed class AssetDependencyCache
{
    private const int Capacity = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<AssetSelectionReceipt>> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private long _epoch;
    internal IReadOnlyList<AssetSelectionReceipt> Recall(string key)
    {
        lock (_gate) return _items.GetValueOrDefault(key) ?? [];
    }
    internal string Qualify(AssetSelectionSession? session, string key, string path)
    {
        IReadOnlyList<AssetSelectionReceipt>? receipts;
        long epoch;
        lock (_gate)
        {
            receipts = _items.GetValueOrDefault(key);
            epoch = _epoch;
        }
        var qualified = AssetCacheIdentity.Qualify(session, key, path, receipts);
        return session is null ? qualified : qualified + ":" + epoch;
    }
    internal void Remember(string key, IReadOnlyList<AssetSelectionReceipt> receipts)
    {
        lock (_gate)
        {
            if (!_items.ContainsKey(key))
            {
                if (_items.Count == Capacity) { _items.Remove(_order.Dequeue()); _epoch++; }
                _order.Enqueue(key);
            }
            _items[key] = receipts;
        }
    }
}
