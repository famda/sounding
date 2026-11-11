using System.Collections;

namespace Sounding.Agent.Sampling;

// Bounded LRU metadata. Reads keep active names/identities alive; inactive entries expire.
internal sealed class ExpiringCache<TKey, T>(int capacity, TimeSpan lifetime) : IEnumerable<KeyValuePair<TKey, T>>
    where TKey : notnull
{
    private sealed class Entry(TKey key, T value, DateTime seen)
    {
        public TKey Key { get; } = key;
        public T Value { get; set; } = value;
        public DateTime Seen { get; set; } = seen;
    }
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recent = new();

    public T this[TKey key]
    {
        set
        {
            if (_entries.TryGetValue(key, out var old))
            {
                old.Value.Value = value;
                old.Value.Seen = DateTime.UtcNow;
                _recent.Remove(old);
                _recent.AddLast(old);
                return;
            }
            while (_entries.Count >= capacity) RemoveOldest();
            _entries[key] = _recent.AddLast(new Entry(key, value, DateTime.UtcNow));
        }
    }

    public bool TryGetValue(TKey key, out T value)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            var now = DateTime.UtcNow;
            if (now - node.Value.Seen <= lifetime)
            {
                value = node.Value.Value;
                node.Value.Seen = now;
                _recent.Remove(node);
                _recent.AddLast(node);
                return true;
            }
            _entries.Remove(key);
            _recent.Remove(node);
        }
        value = default!;
        return false;
    }

    public T GetValueOrDefault(TKey key, T fallback = default!) => TryGetValue(key, out var value) ? value : fallback;

    public bool TryAdd(TKey key, T value)
    {
        if (TryGetValue(key, out _)) return false;
        this[key] = value;
        return true;
    }

    public void Prune(DateTime now)
    {
        while (_recent.First is { } first && now - first.Value.Seen > lifetime) RemoveOldest();
    }

    private void RemoveOldest()
    {
        _entries.Remove(_recent.First!.Value.Key);
        _recent.RemoveFirst();
    }

    public IEnumerator<KeyValuePair<TKey, T>> GetEnumerator() =>
        _entries.Select(pair => new KeyValuePair<TKey, T>(pair.Key, pair.Value.Value.Value)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
