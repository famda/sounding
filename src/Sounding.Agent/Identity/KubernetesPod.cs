namespace Sounding.Agent.Identity;

// The pod and container this agent runs in, when Kubernetes started it. The manifests pass the pod's namespace, name and
// UID through the Downward API and name the container explicitly. Without them, the UID and container name are read from
// the kubelet's path for /dev/termination-log (…/kubelet/pods/<uid>/containers/<name>/…), the namespace from the service
// account's mount, and the pod name from the hostname (which spec.hostname can change): a fallback, reported as a warning.
public sealed record KubernetesPod(string? Namespace, string Pod, string? Uid, string? Container, string? Warning)
{
    private const string PodsSegment = "/kubelet/pods/";
    private const string ContainersSegment = "/containers/";
    private const string NamespaceFile = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    // ReplicaSet and pod-name suffixes use this alphabet (no vowels, no 0, 1 or 3), which keeps "web-api" whole.
    private const string SuffixAlphabet = "bcdfghjklmnpqrstvwxz2456789";

    public static KubernetesPod? Detect(AgentOptions options, string hostname)
    {
        var ns = NonEmpty(options.PodNamespace);
        var pod = NonEmpty(options.PodName);
        var uid = NonEmpty(options.PodUid);
        var container = NonEmpty(options.PodContainer);
        if (ns is null && pod is null && uid is null && container is null
            && Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") is null)
        {
            return null;
        }

        var missing = new List<string>(4);
        if (uid is null || container is null)
        {
            var (mountUid, mountContainer) = FromMounts(Path.Combine(options.ProcPath, "self/mountinfo"));
            if (uid is null) { missing.Add("SOUNDING_POD_UID"); uid = mountUid; }
            if (container is null) { missing.Add("SOUNDING_POD_CONTAINER"); container = mountContainer; }
        }

        if (ns is null)
        {
            missing.Add("SOUNDING_POD_NAMESPACE");
            ns = File.Exists(NamespaceFile) ? NonEmpty(File.ReadAllText(NamespaceFile).Trim()) : null;
        }

        if (pod is null)
        {
            missing.Add("SOUNDING_POD_NAME");
            pod = hostname;
        }

        string? warning = null;
        if (uid is null || container is null)
        {
            warning = $"Kubernetes pod identity not found ({string.Join(", ", missing)} not set, and not readable from the pod's mounts): "
                + "this run is recorded as a new container, so its history does not carry over a restart. Set them from the Downward API.";
        }
        else if (missing.Count > 0)
        {
            warning = $"{string.Join(", ", missing)} not set: read from the pod's mounts or hostname instead, which a pod's settings can change. "
                + "Set them from the Downward API.";
        }

        return new KubernetesPod(ns, pod, uid, container, warning);
    }

    // The pod's UID and the container's name: unique in the cluster, kept when the container restarts in its pod, new
    // when the pod is replaced, even by one with the same name (a StatefulSet's "db-0"). Null when either is unknown.
    public string? Id => Uid is null || Container is null ? null : $"{Uid}/{Container}";

    // Namespace and workload, and the container when it isn't named after the workload: the same for every replica
    // and across rollouts, as "shop/catalog-api" or "shop/catalog-api/sidecar". A label, not an identity.
    public string Name
    {
        get
        {
            var workload = Workload(Pod);
            var name = Container is null || string.Equals(workload, Container, StringComparison.Ordinal) ? workload : $"{workload}/{Container}";
            return Namespace is null ? name : $"{Namespace}/{name}";
        }
    }

    // A guess from the pod name's shape, for the label only: a Deployment's pods are "<name>-<replicaset hash>-<5>", a
    // DaemonSet's or Job's "<name>-<5>", a StatefulSet's "<name>-<ordinal>". A bare pod keeps its name, unless it ends
    // like one of these; SOUNDING_DISPLAY_NAME sets the label instead.
    public static string Workload(string pod)
    {
        var name = pod.AsSpan();
        var single = StripSuffix(name, 5);
        if (!single.IsEmpty)
        {
            var deployment = StripSuffix(single, 6, 10);
            return (deployment.IsEmpty ? single : deployment).ToString();
        }

        var digits = name.Length;
        while (digits > 0 && char.IsAsciiDigit(name[digits - 1])) digits--;
        return digits < name.Length && digits > 1 && name[digits - 1] == '-' ? name[..(digits - 1)].ToString() : pod;
    }

    private static ReadOnlySpan<char> StripSuffix(ReadOnlySpan<char> name, int min, int max = 0)
    {
        var dash = name.LastIndexOf('-');
        var length = name.Length - dash - 1;
        if (dash < 1 || length < min || length > Math.Max(min, max)) return default;
        foreach (var c in name[(dash + 1)..])
        {
            if (!SuffixAlphabet.Contains(c)) return default;
        }

        return name[..dash];
    }

    private static (string? Uid, string? Container) FromMounts(string mountInfo)
    {
        if (!File.Exists(mountInfo)) return (null, null);
        foreach (var line in File.ReadLines(mountInfo))
        {
            var pods = line.IndexOf(PodsSegment, StringComparison.Ordinal);
            if (pods < 0) continue;
            var rest = line.AsSpan(pods + PodsSegment.Length);
            var containers = rest.IndexOf(ContainersSegment, StringComparison.Ordinal);
            if (containers <= 0 || rest[..containers].IndexOf('/') >= 0) continue;
            var name = rest[(containers + ContainersSegment.Length)..];
            var end = name.IndexOf('/');
            if (end > 0) return (rest[..containers].ToString(), name[..end].ToString());
        }

        return (null, null);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
