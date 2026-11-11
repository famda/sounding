using System.Globalization;
using Microsoft.Extensions.Options;

namespace Sounding.Agent.Sampling;

public sealed class SystemSampler(IOptions<AgentOptions> options, HostIdentity identity)
{
    private readonly string _procPath = options.Value.ProcPath;
    private string? _kernel;

    public string? Kernel => _kernel ??= ReadFirstLine("sys/kernel/osrelease");

    public SystemStats Sample()
    {
        var uptime = ReadFirstLine("uptime")?.Split(' ') is [var seconds, ..]
            ? double.Parse(seconds, CultureInfo.InvariantCulture)
            : (double?)null;

        var load = ReadFirstLine("loadavg")?.Split(' ') is [var one, var five, var fifteen, ..]
            ? new[] { one, five, fifteen }.Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()
            : null;

        return new SystemStats(identity.HostDisplayName, Kernel, uptime, load);
    }

    private string? ReadFirstLine(string relativePath)
    {
        var path = Path.Combine(_procPath, relativePath);
        return File.Exists(path) ? KernelFile.ReadAllLines(path).FirstOrDefault()?.Trim() : null;
    }
}
