namespace Sounding.Agent.Reporting;

// One tick, ready to write. What is kept over time (the sample, the process history, the host sample, events) stays
// until it is written; what is only shown as it is now (State, Parts) is let go once a newer tick replaces it.
public sealed class Tick(
    DateTimeOffset at,
    ContainerUsage? usage,
    string? detail,
    string? processHistory,
    string? hostSnapshot,
    IReadOnlyList<AgentEvent> events,
    ContainerState state,
    IReadOnlyDictionary<string, string> parts)
{
    public long Sequence { get; set; }
    public DateTimeOffset At => at;
    public ContainerUsage? Usage => usage;
    public string? Detail => detail;
    public string? ProcessHistory => processHistory;
    public string? HostSnapshot => hostSnapshot;
    public IReadOnlyList<AgentEvent> Events => events;
    public ContainerState? State { get; private set; } = state;
    public IReadOnlyDictionary<string, string>? Parts { get; private set; } = parts;

    // Strings count twice their length (UTF-16); the rest is a rough fixed cost.
    public long Size => 256 + 2L * ((detail?.Length ?? 0) + (processHistory?.Length ?? 0) + (hostSnapshot?.Length ?? 0)
        + events.Sum(e => e.Message.Length + 16));

    public void Release()
    {
        State = null;
        Parts = null;
    }
}

// The ticks not yet written, oldest first, bounded in count and memory: while the database can't be reached (or has
// no schema yet) the agent keeps sampling, and the oldest ticks go first when the bound is reached.
public sealed class TickBuffer(int capacity, long maxBytes)
{
    private readonly Lock _lock = new();
    private readonly LinkedList<Tick> _ticks = new();
    private long _bytes;
    private long _sequence;
    private Tick? _newest;
    private long _dropped;

    public int Count
    {
        get { lock (_lock) return _ticks.Count; }
    }

    public void Add(Tick tick)
    {
        lock (_lock)
        {
            if (_newest is { } previous)
            {
                previous.Release();
            }

            tick.Sequence = ++_sequence;
            _ticks.AddLast(tick);
            _bytes += tick.Size;
            _newest = tick;
            while (_ticks.Count > 1 && (_ticks.Count > capacity || _bytes > maxBytes))
            {
                _bytes -= _ticks.First!.Value.Size;
                _ticks.RemoveFirst();
                _dropped++;
            }
        }
    }

    // The oldest ticks, up to max, and the newest one for what is shown as it is now; nothing is removed until Commit.
    public (List<Tick> History, Tick? Newest) Peek(int max)
    {
        lock (_lock)
        {
            return (_ticks.Take(max).ToList(), _newest);
        }
    }

    // Removes the ticks written, up to and including that sequence number.
    public void Commit(long sequence)
    {
        lock (_lock)
        {
            while (_ticks.First is { } first && first.Value.Sequence <= sequence)
            {
                _bytes -= first.Value.Size;
                _ticks.RemoveFirst();
            }
        }
    }

    // How many ticks were dropped to stay within the bound since the last call.
    public long TakeDropped()
    {
        lock (_lock)
        {
            var dropped = _dropped;
            _dropped = 0;
            return dropped;
        }
    }
}
