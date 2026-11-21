using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Sounding.Agent;

// Sampling runs on its timer and never waits for the database: each tick goes into a bounded buffer, and the writer
// writes what is there. While the database can't be reached, or the dashboard hasn't created its schema yet, ticks
// stay in the buffer (the oldest go first when it's full) and are written once it can.
public sealed class AgentService(
    AgentConnection database,
    AgentRepository repository,
    HostLeadership leadership,
    HostSampler hostSampler,
    CgroupSampler cgroupSampler,
    StorageSampler storageSampler,
    GpuMeterSampler gpuMeterSampler,
    ProcessSampler processSampler,
    TrafficSampler trafficSampler,
    LogSampler logSampler,
    GpuSampler gpuSampler,
    SystemSampler systemSampler,
    ExitReport exitReport,
    HostIdentity identity,
    IOptions<AgentOptions> options,
    ILogger<AgentService> logger) : BackgroundService
{
    // A capture net-meter hasn't finished this long after its end is marked as failed.
    private static readonly TimeSpan CaptureGrace = TimeSpan.FromSeconds(15);

    // A backlog is written this many ticks at a time, with a pause between, so that agents reconnecting together
    // don't all write their backlog at once.
    private const int WriteChunk = 60;
    private static readonly TimeSpan BacklogPause = TimeSpan.FromMilliseconds(200);
    private const long BufferBytes = 32L * 1024 * 1024;

    private static readonly IReadOnlySet<HostRole> NoRoles = new HashSet<HostRole>();

    private readonly EventDetector _detector = new();
    private (long Id, DateTimeOffset Until)? _capture;
    private PacketCapture? _completedCapture;
    private DateTimeOffset _nextCapturePoll;

    private const string NetworkOff = "Turned off for this container (SOUNDING_SAMPLE_NETWORK=false): another container sharing its network reports it";

    // Containers of one Kubernetes pod share a network: one of them reports it, so the traffic isn't counted twice.
    private readonly bool _sampleNetwork = options.Value.SampleNetwork;

    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(Math.Max(250, options.Value.IntervalMilliseconds));
    private readonly HashSet<string> _failing = [];

    private readonly TickBuffer _buffer = new(
        (int)Math.Max(1, Math.Max(1, options.Value.BufferMinutes) * 60_000L / Math.Max(250, options.Value.IntervalMilliseconds)), BufferBytes);
    private readonly SemaphoreSlim _sampled = new(0);

    // The process history (process_samples) every 5 s, besides the latest list every tick. Counted in ticks:
    // a deadline in time slips to 6 s whenever a tick fires a few milliseconds early.
    private readonly int _processHistoryTicks = (int)Math.Max(1, Math.Round(5000.0 / Math.Max(250, options.Value.IntervalMilliseconds)));
    private long _ticks;

    private readonly IReadOnlyList<HostRole> _roles = options.Value.SampleHost ? hostSampler.Roles : [];

    // The host roles this agent holds, as the writer last found them: held only while it is connected.
    private volatile IReadOnlySet<HostRole> _held = NoRoles;

    private long _checkedGeneration = -1;
    private long _registeredGeneration = -1;
    private string? _waiting;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Container {Name} ({ContainerId}) on host {HostName} ({HostId}), sampling every {Interval}",
            identity.Name, identity.ContainerId, identity.HostDisplayName, identity.HostId, _interval);
        logger.LogInformation("May sample the host's {Roles}", string.Join(", ", _roles));
        return Task.WhenAll(SampleAsync(stoppingToken), WriteAsync(stoppingToken));
    }

    private async Task SampleAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                _buffer.Add(Sample());
                if (_sampled.CurrentCount == 0) _sampled.Release();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Sampling failed: {Message}", ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private Tick Sample()
    {
        var at = DateTimeOffset.UtcNow;
        var usage = Try("container usage", cgroupSampler.Sample, null);
        var storage = Try("container storage", storageSampler.Sample, null);
        var host = hostSampler.Sample(_held);
        var gpu = Try("GPU use", gpuMeterSampler.Sample, null);
        var (traffic, dns, network) = _sampleNetwork ? Try("network activity", trafficSampler.Sample, (null, null, null)) : (null, null, null);
        var processes = Try("processes", () => processSampler.Sample(gpu?.Processes ?? new Dictionary<int, ProcessGpu>()), []);
        processes = processes
            .Select(p => trafficSampler.PortsByPid.TryGetValue(p.Pid, out var ports) || trafficSampler.ConnectionsByPid.ContainsKey(p.Pid)
                ? p with { Ports = ports, Connections = trafficSampler.ConnectionsByPid.TryGetValue(p.Pid, out var open) ? open : null }
                : p)
            .ToList();
        var (logs, recentLogs) = logSampler.Sample();
        if (usage is not null)
        {
            usage = usage with
            {
                GpuPercent = gpu?.Percent,
                GpuMemoryBytes = gpu?.MemoryBytes,
                DiskUsedBytes = storage?.UsedBytes,
                Detail = usage.Detail is { } detail
                    ? detail with
                    {
                        Gpus = gpu?.Devices ?? [],
                        GpuMemoryPeakBytes = gpu?.PeakMemoryBytes,
                        GpuKernels = gpu?.Kernels,
                        GpuAllocationFailures = gpu?.AllocationFailures,
                        Traffic = traffic,
                        Dns = dns,
                        LargestPaths = storage?.LargestPaths,
                        FileCount = storage?.FileCount,
                        Mounts = storage?.Mounts,
                        DiskGrowthBytesPerHour = storage?.GrowthBytesPerHour,
                        DiskFreeBytes = storage?.FreeBytes,
                        OpenFiles = processSampler.OpenFilesTotal,
                        OpenFilesUncounted = processSampler.OpenFilesTotal is null ? null : processSampler.OpenFilesUncounted,
                        GrowingPaths = storage?.GrowingPaths,
                        LargestFiles = storage?.LargestFiles,
                        DeletedFiles = storage?.DeletedFiles,
                        Logs = logs,
                        RecentLogs = recentLogs,
                    }
                    : null,
            };
        }

        var state = new ContainerState(processes, network ?? new NetworkActivity("sockets", _sampleNetwork ? "Not available" : NetworkOff, [], 0, [], []), usage?.Detail);
        var events = _detector.Detect(usage?.Detail, processes, processSampler.Complete);
        events.AddRange(trafficSampler.TakeEvents());
        events.AddRange(gpuSampler.TakeEvents());

        // What each sample keeps is serialized now: a tick may wait in the buffer, and its objects are let go.
        return new Tick(
            at,
            usage,
            usage?.Detail is { } sampled ? JsonSerializer.Serialize(AgentRepository.PerSample(sampled), SoundingJson.Stored.ContainerDetail) : null,
            _ticks++ % _processHistoryTicks == 0 && processes.Count > 0
                ? JsonSerializer.Serialize(processes, SoundingJson.Stored.IReadOnlyListProcessUsage)
                : null,
            host.Snapshot is { } snapshot ? JsonSerializer.Serialize(snapshot, SoundingJson.Stored.HostSnapshot) : null,
            events,
            state,
            host.Parts);
    }

    private async Task WriteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _sampled.WaitAsync(stoppingToken);
            await WriteBufferedAsync(stoppingToken);
        }
    }

    // Writes every tick in the buffer, oldest first; on failure they stay for the next time.
    private async Task WriteBufferedAsync(CancellationToken cancellationToken)
    {
        if (!await ReadyAsync(cancellationToken))
        {
            _held = NoRoles;
            return;
        }

        try
        {
            _held = _roles.Count > 0 ? await leadership.RefreshAsync(_roles, cancellationToken) : NoRoles;
            while (_buffer.Peek(WriteChunk) is ({ Count: > 0 } history, { } newest))
            {
                try
                {
                    await repository.WriteAsync(history, newest, cancellationToken);
                }
                catch (PostgresException ex) when (ex.SqlState.StartsWith("22", StringComparison.Ordinal)
                    || ex.SqlState.StartsWith("23", StringComparison.Ordinal) && ex.SqlState != PostgresErrorCodes.ForeignKeyViolation)
                {
                    // The data itself was refused: it would block every tick behind it.
                    logger.LogWarning("The database refused {Count} samples, which are dropped: {Message}", history.Count, ex.MessageText);
                }

                _buffer.Commit(history[^1].Sequence);
                if (_buffer.TakeDropped() is > 0 and var dropped)
                {
                    logger.LogWarning("{Count} samples were dropped: the database was out of reach longer than the buffer holds", dropped);
                }

                if (history.Count < WriteChunk) break;
                await Task.Delay(BacklogPause, cancellationToken);
            }

            await CaptureAsync(cancellationToken);
            if (_waiting is not null)
            {
                logger.LogInformation("Writing to the database");
                _waiting = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed(ex);
        }
    }

    // Connected, at a schema this agent can write to, and registered.
    private async Task<bool> ReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = await database.GetAsync(cancellationToken);
            if (_checkedGeneration != database.Generation)
            {
                var status = await DatabaseSchema.CheckAsync(connection, cancellationToken);
                var problem = status.State switch
                {
                    SchemaState.Missing => "the Sounding dashboard hasn't created the database schema yet",
                    SchemaState.AgentTooOld => $"the database schema (version {status.Version}) needs a newer agent: upgrade the agent in this image",
                    SchemaState.DatabaseTooOld => $"the database schema (version {status.Version}) is older than this agent writes to: upgrade the Sounding dashboard",
                    _ => null,
                };
                if (problem is not null)
                {
                    Waiting(problem);
                    return false;
                }

                repository.Use(status.Version);
                _checkedGeneration = database.Generation;
                _registeredGeneration = -1;
            }

            if (_registeredGeneration != database.Generation)
            {
                await repository.RegisterAsync(systemSampler.Kernel, cancellationToken);
                _registeredGeneration = database.Generation;
                logger.LogInformation("Registered with the database");
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed(ex);
            return false;
        }
    }

    private void Failed(Exception ex)
    {
        if (DatabaseSchema.IsMissing(ex))
        {
            _checkedGeneration = -1;
        }
        else if (ex is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // This container's or host's row was removed: register again.
            _registeredGeneration = -1;
        }

        Waiting(ex is PostgresException postgres ? postgres.MessageText : ex.Message);
    }

    private void Waiting(string reason)
    {
        if (reason == _waiting) return;
        _waiting = reason;
        logger.LogWarning("Keeping samples in memory: {Reason}", reason);
    }

    // A packet capture the dashboard asked for: started on its notification, uploaded when net-meter is done.
    private async Task CaptureAsync(CancellationToken cancellationToken)
    {
        if (_capture is { } running)
        {
            _completedCapture ??= trafficSampler.TakeCapture();
            if (_completedCapture is { } done)
            {
                await repository.FinishCaptureAsync(running.Id, done.Data, done.Packets, cancellationToken);
                _completedCapture = null;
                _capture = null;
            }
            else if (DateTimeOffset.UtcNow > running.Until + CaptureGrace)
            {
                trafficSampler.CancelCapture();
                await repository.FinishCaptureAsync(running.Id, null, 0, cancellationToken);
                _capture = null;
            }

            return;
        }

        var requested = database.TakeCaptureRequest();
        if (!requested && DateTimeOffset.UtcNow < _nextCapturePoll) return;
        _nextCapturePoll = DateTimeOffset.UtcNow.AddSeconds(5);
        if (await repository.TakeCaptureRequestAsync(cancellationToken) is not { } request)
        {
            return;
        }

        _capture = (request.Id, DateTimeOffset.UtcNow.AddSeconds(request.Seconds));
        trafficSampler.StartCapture(request.Seconds);
        if (!trafficSampler.Capturing)
        {
            logger.LogWarning("Capture {Id} failed: packet capture is off", request.Id);
            await repository.FinishCaptureAsync(request.Id, null, 0, cancellationToken);
            _capture = null;
        }
    }

    private T Try<T>(string part, Func<T> sample, T fallback)
    {
        try
        {
            var value = sample();
            if (_failing.Remove(part))
            {
                logger.LogInformation("{Part} readable again", part);
            }

            return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_failing.Add(part))
            {
                logger.LogWarning("{Part} not available: {Message}", part, ex.Message);
            }

            return fallback;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            var exit = await exitReport.WaitAsync(TimeSpan.FromMilliseconds(500));
            // What was sampled last, then the stop.
            await WriteBufferedAsync(cancellationToken);
            if (_waiting is null)
            {
                await repository.MarkStoppedAsync(exit, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not mark container as stopped: {Message}", ex.Message);
        }

        await database.DisposeAsync();
    }
}
