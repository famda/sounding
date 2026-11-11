namespace Sounding.Agent.Sampling;

// The names the kernel's files repeat on every read (counters, interfaces, devices, CPUs): each one string for
// as long as it keeps coming, instead of a new one per read. Not for concurrent use.
public sealed class NamePool
{
    // Names that come and go (the host's veth interfaces) would otherwise add up for ever.
    private const int Limit = 4096;

    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public string Get(ReadOnlySpan<char> name)
    {
        var lookup = _names.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(name, out var known))
        {
            return known;
        }

        if (_names.Count >= Limit)
        {
            _names.Clear();
        }

        var added = name.ToString();
        _names.Add(added);
        return added;
    }
}
