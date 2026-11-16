using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Mcp;

// Suggests host and container names while a user fills a prompt's or resource template's arguments.
public static class SoundingCompletions
{
    private const int MaxValues = 100;

    public static ValueTask<CompleteResult> Complete(RequestContext<CompleteRequestParams> context, CancellationToken cancellationToken)
    {
        var argument = context.Params?.Argument;
        var fleet = context.Services?.GetService<FleetSnapshot>()?.Current;
        if (argument is null || fleet is null) return ValueTask.FromResult(Empty);

        var (containers, hosts) = argument.Name switch
        {
            "target" => (true, true),
            "container" => (true, false),
            "host" => (false, true),
            _ => (false, false),
        };
        if (!containers && !hosts) return ValueTask.FromResult(Empty);

        var values = new List<string>();
        var typed = argument.Value ?? "";
        foreach (var host in fleet.Hosts)
        {
            if (hosts && (host.DisplayName.Contains(typed, StringComparison.OrdinalIgnoreCase) || host.HostId.StartsWith(typed, StringComparison.Ordinal))) values.Add(host.DisplayName);
            if (!containers) continue;
            foreach (var container in host.Containers)
            {
                var name = container.Name ?? container.ContainerId;
                if (name.Contains(typed, StringComparison.OrdinalIgnoreCase) || container.ContainerId.StartsWith(typed, StringComparison.Ordinal)) values.Add(name);
            }
        }

        values.Sort(StringComparer.OrdinalIgnoreCase);
        return ValueTask.FromResult(new CompleteResult
        {
            Completion = new Completion { Values = [.. values.Take(MaxValues)], Total = values.Count, HasMore = values.Count > MaxValues },
        });
    }

    private static CompleteResult Empty => new() { Completion = new Completion { Values = [] } };
}
