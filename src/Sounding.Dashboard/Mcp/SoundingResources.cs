using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Containers;
using Sounding.Dashboard.Application.Hosts;
using Wolverine;

namespace Sounding.Dashboard.Mcp;

// Reference text that helps a model read the tools' numbers, and the latest state of a host or container as
// resources a client can attach to a conversation.
[McpServerResourceType]
public sealed class SoundingResources(IMessageBus bus)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerResource(UriTemplate = "sounding://reference/metrics", Name = "metrics", Title = "Metrics reference", MimeType = "text/markdown")]
    [Description("What each metric means, its unit, and what values indicate a problem.")]
    public static string Metrics() => Reference.Metrics;

    [McpServerResource(UriTemplate = "sounding://reference/events", Name = "events", Title = "Events reference", MimeType = "text/markdown")]
    [Description("Every event kind and level Sounding records, and what each means.")]
    public static string Events() => Reference.Events;

    [McpServerResource(UriTemplate = "sounding://hosts/{host}", Name = "host", Title = "Host", MimeType = "application/json")]
    [Description("A host's latest sample and its containers, as get_host returns it.")]
    public async Task<string> Host([Description("Host id or name.")] string host, CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(Found(await bus.InvokeAsync<Result<HostDetails>>(new GetHostQuery(host), cancellationToken)), Json);

    [McpServerResource(UriTemplate = "sounding://containers/{container}", Name = "container", Title = "Container", MimeType = "application/json")]
    [Description("A container's state and latest sample, as get_container returns it.")]
    public async Task<string> Container([Description("Container id or name.")] string container, CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(Found(await bus.InvokeAsync<Result<ContainerDetails>>(new GetContainerQuery(container), cancellationToken)), Json);

    // A resource that does not exist is a protocol error (not a tool result), so it is raised with its code.
    private static T Found<T>(Result<T> result) => result.IsSuccess
        ? result.Value!
        : throw new McpProtocolException(result.Error ?? result.Status.ToString(),
            result.Status == ResultStatus.NotFound ? McpErrorCode.ResourceNotFound : McpErrorCode.InvalidParams);

    private static class Reference
    {
        public const string Metrics = """
            # Sounding metrics

            Rates are per second, averaged over each sample (1 s) or bucket. CPU "cores" is cores in use (1.5 = one and a
            half cores busy); "percent of host" is against all of the host's cores.

            ## Basic (get_metrics without 'metrics')
            | Metric | Meaning | Look for |
            |---|---|---|
            | cpuCores, cpuPercent | CPU in use | Near the CPU limit (container) or 90 % (host) |
            | memoryBytes, memoryUsedBytes | Memory in use, including page cache a container can reclaim | Near the memory limit; steady growth |
            | io/disk Read/Write BytesPerSecond | Disk throughput | Sustained high writes with ioPressure |
            | net Receive/Transmit BytesPerSecond | Network throughput | Drops to zero (service stopped) |
            | gpuPercent, gpu…MemoryPercent | GPU and VRAM use | VRAM near 100 % |

            ## Detail (get_metrics with their names in 'metrics', or "all")
            | Metric | Unit | Meaning | Look for |
            |---|---|---|---|
            | cpuPressure, memoryPressure, ioPressure | % | Share of time some tasks stalled waiting for the resource (PSI "some") | Over 20-40 % sustained |
            | cpuPressureFull, memoryPressureFull, ioPressureFull | % | Share of time all tasks stalled (PSI "full") | Over 10 %: real slowdown |
            | throttled | % | CPU periods in which the container hit its CPU limit | Over 25 %: limit too low or CPU-bound |
            | throttledMs | ms/s | Time held back by the CPU limit | |
            | anon | bytes | Application memory (heap) that cannot be reclaimed | Steady growth: leak |
            | file | bytes | Page cache, reclaimable | |
            | kernel | bytes | Kernel memory (sockets, slab) | Growth with many connections |
            | majorFaults | /s | Pages read back from disk | High with memory near limit: thrashing |
            | refaults | /s | Evicted pages needed again | Over 1000/s: working set does not fit |
            | reclaimScanned, reclaimed | bytes/s | Memory reclaim activity | High: memory pressure |
            | tasks | count | Processes and threads | Near the pids limit |
            | tasksStarted | /s | New processes/threads | Spikes: fork storms, retries spawning processes |
            | readOps, writeOps | ops/s | Disk operations | |
            | retransmits | /s | TCP segments sent again | Over 10/s: packet loss or congestion |
            | resets | /s | TCP resets sent | Rejected connections |
            | failedConnects | /s | Outgoing TCP connections that failed | Any sustained value: dependency down |
            | dnsLookups, dnsFailures | /s | DNS queries and failures | Failures: name does not resolve (NXDOMAIN) |
            | dnsReplyMs | ms | Average DNS reply time | Over 100 ms; ~2000 ms means timeouts and retries |
            | udpErrors, udpBufferErrors | /s | UDP receive errors, drops for full buffers | Any |
            | conntrack | count | Tracked connections | Near the host maximum |
            | openFiles | count | Open file descriptors | Steady growth: leak |
            | logLines, logErrors, logWarnings | lines/s | Output rate by level | Error rate rising |
            | internetIn/Out, internalIn/Out, networkIn/Out | bytes/s | Traffic split: internet, same host, other hosts | |
            | toDevice, toHost, launches | bytes/s, /s | GPU transfers and kernel launches | |

            Host-only: cpuUser, cpuSystem, cpuIoWait (over 20 %: disk bound), cpuSteal (over 10 %: noisy neighbour VM),
            cpuIrq, swapIn, swapOut (any sustained: memory short), running, blocked (tasks in D state), contextSwitches,
            committed, slabReclaimable, slabUnreclaimable, fileHandles, power:<gpu> (W), clock:<gpu> (MHz), temp:<sensor> (°C).

            ## Processes
            state: R running, S sleeping, D uninterruptible wait (usually disk or network storage), Z zombie (exited, not
            reaped), T stopped. waitMillisecondsPerSecond: time spent waiting for CPU. oomScore: higher is killed first.
            memoryGrowthBytesPerHour: slope of memory over the window (needs a few minutes of samples).

            get_processes shows the state now; get_process_trends shows growth over a period.

            ## Requests (get_network, get_dependencies)
            Decoded from the packets for HTTP, PostgreSQL, MySQL, Redis and MongoDB over the last minute. role client: this
            container called the peer; server: the peer called it. Errors are HTTP 5xx and protocol errors. p95Ms is the
            95th percentile latency.
            """;

        public const string Events = """
            # Sounding events

            | Kind | Level | Example | Meaning |
            |---|---|---|---|
            | lifecycle | info | Started; Exited with code 0 | The container started or ended normally |
            | lifecycle | warning | Exited with code 1; Stopped by signal 9 | Ended with an error, or killed (9 = SIGKILL, often the OOM killer; 15 = SIGTERM, a normal stop request) |
            | process | info | Started worker (pid 42) | Processes started or ended |
            | oom | error | Out of memory: worker (pid 42) was killed | The container ran out of memory; the named process was likely killed |
            | memory | warning | Reached its memory limit | The container reached its memory limit |
            | cpu | warning | CPU throttled: held back in 40% of periods | At least 25 % of CPU periods throttled by the limit |
            | port | info | Listening on 8080/tcp | A new port started accepting connections |
            | destination | info | New internet destination api.example.com:443/tcp | First contact with an internet address |
            | gpu | error | GPU 0 reported Xid 79 | NVIDIA driver error on the GPU |
            """;
    }
}
