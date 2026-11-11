using System.Diagnostics;

namespace Sounding.Dashboard.Live;

public sealed class FleetFreshness
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);
    private long _lastRefresh;

    public void Refreshed(long startedAt) => Interlocked.Exchange(ref _lastRefresh, startedAt);

    public bool IsReady => Interlocked.Read(ref _lastRefresh) is var at && at != 0
        && Stopwatch.GetElapsedTime(at) < MaxAge;
}
