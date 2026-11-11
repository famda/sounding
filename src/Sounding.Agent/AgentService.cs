using Microsoft.Extensions.Options;
using Npgsql;

namespace Sounding.Agent;

public sealed class AgentService(
    NpgsqlDataSource dataSource,
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
    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(1);

    // A capture net-meter hasn't finished this long after its end is marked as failed.
    private static readonly TimeSpan CaptureGrace = TimeSpan.FromSeconds(15);

    private readonly EventDetector _detector = new();
    private (long Id, DateTimeOffset Until)? _capture;
    private PacketCapture? _completedCapture;
    private DateTimeOffset _nextCapturePoll;

    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(Math.Max(250, options.Value.IntervalMilliseconds));
    private readonly TimeSpan _retention = TimeSpan.FromMinutes(Math.Max(1, options.Value.RetentionMinutes));
    private readonly HashSet<string> _failing = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Container {Name} ({ContainerId}) on host {HostName} ({HostId}), sampling every {Interval}",
            identity.Name, identity.ContainerId, identity.HostDisplayName, identity.HostId, _interval);

        using var timer = new PeriodicTimer(_interval);
        var registered = false;
        var registeredGeneration = 0L;
        var schemaReady = false;
        var nextPrune = DateTimeOffset.MinValue;
        var roles = options.Value.SampleHost ? hostSampler.Roles : [];
        logger.LogInformation("May sample the host's {Roles}", string.Join(", ", roles));

        do
        {
            try
            {
                if (!schemaReady)
                {
                    await DatabaseSchema.EnsureAsync(dataSource, stoppingToken);
                    schemaReady = true;
                }
                if (!registered || !database.IsOpen || registeredGeneration != database.Generation)
                {
                    registered = false;
                    await repository.RegisterAsync(systemSampler.Kernel, stoppingToken);
                    registeredGeneration = database.Generation;
                    registered = true;
                    logger.LogInformation("Registered with the database");
                }

                var usage = Try("container usage", cgroupSampler.Sample, null);
                var storage = Try("container storage", storageSampler.Sample, null);
                var held = roles.Count > 0 ? await leadership.RefreshAsync(roles, stoppingToken) : new HashSet<HostRole>();
                var host = hostSampler.Sample(held);
                var gpu = Try("GPU use", gpuMeterSampler.Sample, null);
                var (traffic, dns, network) = Try("network activity", trafficSampler.Sample, (null, null, null));
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

                var state = new ContainerState(processes, network ?? new NetworkActivity("sockets", "Not available", [], 0, [], []), usage?.Detail);
                var events = _detector.Detect(usage?.Detail, processes, processSampler.Complete);
                events.AddRange(trafficSampler.TakeEvents());
                events.AddRange(gpuSampler.TakeEvents());
                await repository.WriteTickAsync(usage, state, host, events, stoppingToken);
                await CaptureAsync(stoppingToken);

                if (host.Snapshot is not null && DateTimeOffset.UtcNow >= nextPrune)
                {
                    await repository.PruneAsync(_retention, stoppingToken);
                    nextPrune = DateTimeOffset.UtcNow + PruneEvery;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (DatabaseSchema.IsMissing(ex))
                {
                    schemaReady = false;
                    registered = false;
                }
                else if (!database.IsOpen || ex is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
                {
                    registered = false;
                }
                logger.LogWarning("Tick failed: {Message}", ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
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
            await repository.MarkStoppedAsync(exit, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not mark container as stopped: {Message}", ex.Message);
        }

        await database.DisposeAsync();
    }
}
