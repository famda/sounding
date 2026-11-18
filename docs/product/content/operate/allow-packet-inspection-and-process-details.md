---
title: Allow packet inspection and process details
description: What Sounding needs to read network traffic and other users' processes, and how to allow it.
---

If **Network** shows **Packet capture off**, check the network traffic permission below. If the process table
shows missing details for programs running as another user, check the process permissions. Most figures are
available with the default permissions.

## Network traffic

Connections, requests, DNS lookups, the network map, and packet captures come from reading the container's network
traffic. This needs the `NET_RAW` Linux capability, which Docker gives every container by default.

When Sounding can't read the traffic, the Network page says **Packet capture off**. You still see open connections and
the container's traffic totals, but not the data per connection, requests, DNS lookups, or captures.

> [!NOTE]
> Decide whether this visibility is worth the extra access in your environment before changing security settings. Keep
> restrictive settings if your policy requires them.

This happens when:

- **The container drops `NET_RAW`**, for example with `cap_drop: [ALL]`. Add it back:

  ```yaml title="compose.yaml"
  services:
    your-app:
      cap_drop: [ALL]
      cap_add: [NET_RAW]
  ```

- **The container runs as a non-root user with `no-new-privileges`.** Sounding's traffic reader can't get its permission
  then. You can keep that security setting; open connections and traffic totals remain available without packet details.

- **On Kubernetes, the namespace enforces the restricted Pod Security Standard.** It requires every capability to be
  dropped and doesn't allow `NET_RAW` back. You can keep it: connections and traffic totals remain. Packet details need
  the **baseline** standard, which allows `NET_RAW`, on the workload's namespace. See
  [Pod Security Standards](../deployment/kubernetes.md#pod-security-standards).

## Other users' processes

If processes in the container run as different users, Sounding can't read some details of the ones that aren't its own:
their disk activity, their exact memory share, and their open files. Those cells show **–**.

To see them, the container must run as `root` and have the `SYS_PTRACE` capability:

```yaml title="compose.yaml"
services:
  your-app:
    cap_add: [SYS_PTRACE]
```

With the host's `/proc` also mounted (see [show the host's network](show-host-network-disk-and-gpu.md#network)),
Sounding can then also tell which of the container's ports are published on the host.

> [!WARNING]
> `SYS_PTRACE` lets the container inspect other processes. Grant it only where you need these details.

## Next steps

- [Secure Sounding](secure-sounding.md)
- [Investigate network activity](../dashboard/investigate-network.md)
