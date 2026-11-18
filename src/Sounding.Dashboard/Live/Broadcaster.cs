using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Sounding.Dashboard.Live;

public sealed class Broadcaster<T> where T : class
{
    private readonly ConcurrentDictionary<Guid, Channel<T>> _subscribers = new();
    private readonly CancellationToken _stopping;

    // Streams end when the application stops, so shutdown doesn't wait for every open browser tab to disconnect;
    // the browser reconnects, to another replica when there is one.
    public Broadcaster(IHostApplicationLifetime lifetime)
    {
        _stopping = lifetime.ApplicationStopping;
        _stopping.Register(static state => ((Broadcaster<T>)state!).CompleteAll(), this);
    }

    public T? Latest { get; private set; }

    public void Publish(T value)
    {
        Latest = value;
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(value);
        }
    }

    public async IAsyncEnumerable<T> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        if (_stopping.IsCancellationRequested)
        {
            channel.Writer.TryComplete();
        }

        try
        {
            if (Latest is { } latest)
            {
                yield return latest;
            }

            await foreach (var value in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return value;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    private void CompleteAll()
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryComplete();
        }
    }
}
