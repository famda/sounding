---
title: Troubleshoot Sounding
description: Fix the most common problems, from a host that doesn't appear to missing GPU or network details.
---

Start with what you see:

- [My container doesn't appear](#my-container-doesnt-appear)
- [The dashboard is stale or reconnecting](#the-dashboard-shows-stale-data-or-reconnecting)
- [A chart is empty](#a-chart-is-empty-or-shows-not-available)
- [A host appears twice or under the wrong name](#a-host-appears-twice-or-under-the-wrong-name)
- [Network details are missing](#connections-requests-or-captures-are-missing)
- [GPU data is missing](#gpu-data-is-missing)
- [Output or process details are missing](#output-or-process-details-are-missing)
- [Disk space looks wrong](#disk-space-looks-wrong)

Each section covers what you see, likely causes, and what to check next.

## My container doesn't appear

Work through these in order:

1. **The image doesn't start through Sounding.** Check that the image contains `/opt/sounding` and that its
   `ENTRYPOINT` starts with `/opt/sounding/run`. See
   [add the agent to your image](../get-started/getting-started.md#1-add-the-agent-to-your-image).
2. **The agent can't reach the database.** Look in the container's logs for connection errors. Your application keeps
   running even when the agent can't connect, so a working application doesn't mean Sounding is working.
3. **The connection string points to `localhost`.** Inside a container, `localhost` is the container itself. Use the
   database's host name.
4. **The container and the dashboard use different databases.** Check that their connection strings point to the same
   database by comparing the host, port, and database name. Other options can differ.
5. **The database requires encryption.** The agent can't connect to a server that requires it. See
   [protect the connection](../operate/set-up-the-database.md#protect-the-connection).
6. **The platform isn't supported.** Sounding needs x86-64 Linux containers with cgroup v2. See
   [requirements](../reference/requirements.md).

## A container shows Not reporting

The container hasn't reported for more than 10 seconds.

- **It was killed or crashed.** A container that's killed can't report that it stopped, so it shows **Not reporting**
  instead of **Stopped**. Check its status:

  ```sh
  docker compose ps
  ```
- **It lost its connection to the database.** Check the container's logs, and that the database is up.
- **It measures less often than every 10 seconds.** Raise `SOUNDING_STALE_AFTER_SECONDS` on the dashboard above
  the sampling interval in seconds. For example, if `SOUNDING_INTERVAL_MILLISECONDS` is `15000` (15 seconds), use a
  threshold above 15 seconds, such as `20`.

## The dashboard shows Stale data or Reconnecting

- **Reconnecting** or **Connecting**: your browser can't reach the dashboard. Check that the dashboard is running and
  reachable.
- **Stale data**: the dashboard is reachable but has had no new figures for more than 10 seconds. Check the dashboard's
  logs and its connection to the database.

The last figures stay on screen until the dashboard recovers.

## A chart is empty or shows Not available

- **The container just started.** Charts need a few seconds of samples, and rates need two. Disk-space scans run
  asynchronously with a 30-second pause after each scan, and growth estimates need a few minutes.
- **History unavailable** appears. Select **Retry history**. Live figures keep updating in the meantime.
- **The host's network, disks, or GPUs show Not available.** One container on the host needs access to them. See
  [show the host's network, disks, and GPUs](../operate/show-host-network-disk-and-gpu.md).
- **No container reports the host.** At least one container on each host must have `SOUNDING_SAMPLE_HOST` set to
  `true`, the default.
- **The system doesn't provide that figure.** Some figures depend on the Linux kernel version, the virtual machine, or
  the container's permissions. **Not available** often says why. Remember that a missing value isn't zero.

## A host appears twice, or under the wrong name

- **After a reboot, a host appears twice.** A host's ID is the machine's boot ID, which changes when it restarts. Set
  `SOUNDING_HOST_ID` to a fixed value on every container of the machine.
- **A host shows an ID instead of a name.** Set `SOUNDING_HOST_DISPLAY_NAME` on every container of the machine.
- **A container shows its ID instead of its name.** Docker only provides names on user-defined networks, not on its
  default one. Set `SOUNDING_DISPLAY_NAME`, or attach the container to a network of your own.
- **Two containers' figures are mixed.** They share an ID. If you set `SOUNDING_CONTAINER_ID` or `SOUNDING_HOST_ID`, give
  each container, and each machine, its own.

## Connections, requests, or captures are missing

The Network page says **Packet capture off**: Sounding can't read the container's traffic, usually because the
container's security settings remove the permission it needs. See
[allow packet inspection and process details](../operate/allow-packet-inspection-and-process-details.md#network-traffic).

If captures work but **Requests** stays empty:

- The traffic is encrypted, such as HTTPS. Sounding can't read encrypted requests.
- The protocol isn't one Sounding decodes. See [investigate network activity](../dashboard/investigate-network.md#find-slow-or-failing-requests).

## GPU data is missing

**The host's GPU page shows no cards:**

1. Check that the host has an NVIDIA GPU, its driver, and the NVIDIA Container Toolkit.
2. Check that one container with Sounding has GPU access, and that `nvidia-smi` works in it:

   ```sh
   docker compose exec your-app nvidia-smi
   ```

**The cards appear, but a container shows no GPU use:**

1. Check that the container's GPU programs use CUDA. Graphics work (OpenGL, Vulkan) isn't measured per container.
2. Check that the programs start through Sounding's launcher, as described in
   [add the agent to your image](../get-started/getting-started.md#1-add-the-agent-to-your-image).
3. Check that `CUDA_INJECTION64_PATH` isn't already set in the container: another tool using it, such as a profiler,
   prevents Sounding from measuring.
4. If the CUDA programs run as a different user from the agent, the folder they report in (`/tmp/gpu-meter` by default)
   must be writable by all of them. Create it in your Dockerfile:

   ```dockerfile title="Dockerfile"
   RUN mkdir -p /tmp/gpu-meter && chmod 1777 /tmp/gpu-meter
   ```

## Output or process details are missing

- **Latest output and log lines are empty:** the container has a terminal (`docker run -t`, or `tty: true` in Compose),
  or it runs the agent without an application. Sounding reads output only from containers without a terminal.
- **Some process cells show –:** the process runs as another user. See
  [other users' processes](../operate/allow-packet-inspection-and-process-details.md#other-users-processes).

## Disk space looks wrong

- **Space used differs from what Docker reports.** Sounding counts the container's files: its image plus what it wrote,
  without volumes. Docker's own figure usually counts only what the container wrote.
- **The containers' space adds up to more than the disk.** Containers built from the same image each count the image's
  files.
- **The disk fills up but no large files appear.** Check **Deleted but still open**: deleted files that a program still
  holds open keep their space.

## Get more detail from the agent

The agent writes its messages to your container's logs. To see more of them, set this on the container:

```yaml title="compose.yaml"
environment:
  SOUNDING_LOGGING__LOGLEVEL__DEFAULT: Information
```

Then recreate the container and read its logs:

```sh
docker compose up -d your-app
docker compose logs your-app
```

Check the logs for anything sensitive before sharing them.

## Still stuck?

Check the [FAQ](faq.md), or open an issue in the project's repository with the agent's logs (see
[get more detail from the agent](#get-more-detail-from-the-agent)).
