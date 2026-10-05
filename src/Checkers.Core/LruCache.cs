namespace Checkers.Core;

/// <summary>Thread-safe LRU cache with a fixed capacity and a time-to-live per entry.</summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    sealed record Entry(TKey Key, TValue Value, DateTime ExpiresUtc);

    readonly int _capacity;
    readonly TimeSpan _ttl;
    readonly Func<DateTime> _utcNow;
    readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    readonly LinkedList<Entry> _order = new(); // most recently used first
    readonly object _lock = new();

    public LruCache(int capacity, TimeSpan ttl, Func<DateTime>? utcNow = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ttl = ttl;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(Math.Min(capacity, 1 << 16));
    }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (node.Value.ExpiresUtc > _utcNow())
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
                _order.Remove(node);
                _map.Remove(key);
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            var node = _order.AddFirst(new Entry(key, value, _utcNow() + _ttl));
            _map[key] = node;
            while (_map.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
