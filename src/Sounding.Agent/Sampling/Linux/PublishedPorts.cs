using System.Text.Json;

namespace Sounding.Agent.Sampling;

// The host ports Docker publishes this container's ports on (docker run -p), from Docker's own record of the
// container (config.v2.json, else hostconfig.json, in its data root), read the way dockerd sees its files: through
// the host's /proc. That takes the host's /proc mounted and an agent running as root with CAP_SYS_PTRACE; without
// them nothing is known, and the dashboard goes by the traffic that arrives from the host. Only those two files
// are read. Published ports can't change while the container runs, so they're read once, retried every minute
// until that works.
public sealed class PublishedPorts(string procPath, string hostProcPath)
{
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);
    private Dictionary<(string Protocol, int Port), List<int>>? _ports;
    private DateTime _retryAt = DateTime.MinValue;

    // The host ports of a container port, or null when it isn't published or that can't be known.
    public IReadOnlyList<int>? Of(string protocol, int port)
    {
        if (_ports is null && DateTime.UtcNow >= _retryAt)
        {
            _retryAt = DateTime.UtcNow + Retry;
            _ports = Read();
        }

        return _ports?.GetValueOrDefault((protocol, port));
    }

    private Dictionary<(string, int), List<int>>? Read()
    {
        try
        {
            if (ContainerId() is not { } id || !Directory.Exists(hostProcPath))
            {
                return null;
            }

            foreach (var dir in Directory.EnumerateDirectories(hostProcPath))
            {
                if (!int.TryParse(Path.GetFileName(dir), out _) || Text(Path.Combine(dir, "comm"))?.Trim() != "dockerd")
                {
                    continue;
                }

                var root = Path.Combine(dir, "root");
                foreach (var dataRoot in DataRoots(dir, root))
                {
                    var container = $"{root}{dataRoot}/containers/{id}";
                    if (Text($"{container}/config.v2.json") is { } config && Parse(config, "NetworkSettings", "Ports") is { } ports)
                    {
                        return ports;
                    }

                    if (Text($"{container}/hostconfig.json") is { } host && Parse(host, "PortBindings") is { } bindings)
                    {
                        return bindings;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    // The container's full id: Docker bind-mounts /etc/hostname from <data root>/containers/<id>/hostname.
    private string? ContainerId()
    {
        foreach (var line in File.ReadLines(Path.Combine(procPath, "self/mountinfo")))
        {
            var fields = line.Split(' ');
            if (fields.Length > 4 && fields[4] == "/etc/hostname" && fields[3].EndsWith("/hostname", StringComparison.Ordinal))
            {
                var id = Path.GetFileName(Path.GetDirectoryName(fields[3]));
                return id is { Length: 64 } && id.All(char.IsAsciiHexDigitLower) ? id : null;
            }
        }

        return null;
    }

    // Where dockerd keeps its data: --data-root, else data-root in its config file, else the default.
    private static IEnumerable<string> DataRoots(string process, string root)
    {
        var args = (Text(Path.Combine(process, "cmdline")) ?? "").Split('\0');
        string? Arg(string name) => args.Select((a, i) => a == name && i + 1 < args.Length ? args[i + 1]
            : a.StartsWith(name + "=", StringComparison.Ordinal) ? a[(name.Length + 1)..] : null).FirstOrDefault(a => a is not null);

        if (Arg("--data-root") is { } given)
        {
            yield return given.TrimEnd('/');
        }

        if (Text(root + (Arg("--config-file") ?? "/etc/docker/daemon.json")) is { } config)
        {
            string? configured = null;
            try
            {
                using var json = JsonDocument.Parse(config);
                configured = json.RootElement.TryGetProperty("data-root", out var value) ? value.GetString() : null;
            }
            catch (JsonException)
            {
            }

            if (configured is not null)
            {
                yield return configured.TrimEnd('/');
            }
        }

        yield return "/var/lib/docker";
    }

    // {"80/tcp": [{"HostIp": "0.0.0.0", "HostPort": "8088"}]} at the given path, by container port.
    private static Dictionary<(string, int), List<int>>? Parse(string text, params string[] path)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            var node = json.RootElement;
            foreach (var name in path)
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node))
                {
                    return null;
                }
            }

            if (node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var ports = new Dictionary<(string, int), List<int>>();
            foreach (var entry in node.EnumerateObject())
            {
                if (entry.Name.Split('/') is not [var number, var protocol] || !int.TryParse(number, out var port) || entry.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var hostPorts = entry.Value.EnumerateArray()
                    .Select(b => b.TryGetProperty("HostPort", out var p) && int.TryParse(p.GetString(), out var hostPort) ? hostPort : 0)
                    .Where(p => p > 0).Distinct().ToList();
                if (hostPorts.Count > 0)
                {
                    ports[(protocol, port)] = hostPorts;
                }
            }

            return ports;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
