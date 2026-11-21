---
title: Upgrade Sounding
description: Move the dashboard and your agents to a new version of Sounding.
---

Sounding's agent and dashboard are released together with the same version number. Run the same version for both.

## Choose an image tag

Both images are published to the [`famda/sounding`](https://hub.docker.com/r/famda/sounding) repository on Docker Hub,
with a tag prefix for each part:

| Agent tag | Dashboard tag | Follows |
|---|---|---|
| `famda/sounding:agent-latest` | `famda/sounding:dashboard-latest` | The newest release. Pre-releases don't move it. |
| `famda/sounding:agent-1.2.0` | `famda/sounding:dashboard-1.2.0` | Exactly this release. Pre-releases look like `1.3.0-rc.1` or `1.3.0-preview.1`. |
| `famda/sounding:agent-sha-1a2b3c4` | `famda/sounding:dashboard-sha-1a2b3c4` | One build of the source, by its short commit hash, including builds between releases. |

The setup guides and demos use the `latest` tags. To choose when to upgrade, use the version tags, with the same
version for the agent and the dashboard. The [releases page](https://github.com/famda/sounding/releases) lists each
version and what changed.

## Upgrade

Back up the database before upgrading. Upgrade the dashboard first: when it starts, it updates Sounding's tables for
its version, keeping your history. Then upgrade the agents. Meanwhile, agents of the other version keep reporting the
figures both versions have. If a release changes the tables in a way an agent can't write to, that agent holds its
figures in memory, and its log says whether to upgrade the agent or the dashboard.

### With Docker Compose

1. **Update the dashboard.** Change its image tag if you pin a version, then pull and recreate it:

   ```sh
   docker compose pull dashboard
   docker compose up -d dashboard
   ```

2. **Rebuild your application images** with the new agent. Change the tag in the `COPY --from=famda/sounding:agent-…`
   line if needed, then rebuild with `--pull` so Docker fetches the new agent:

   ```sh
   docker compose build --pull
   docker compose up -d
   ```

3. **Deploy the rebuilt images** as you usually do.

### On Kubernetes

The sample's manifests use the `-latest` tags with `imagePullPolicy: Always`, so a restart pulls the newest release. To
choose when to upgrade, set the version tags in `sounding.yaml` and `node-agent.yaml` instead.

1. **Update the dashboard.** With a version tag, change its `image` and run `kubectl apply -f sounding.yaml`. With
   `dashboard-latest`, restart it: `kubectl -n sounding rollout restart deployment/dashboard`.
2. **Update the node agent**, if you run it, the same way: `kubectl apply -f node-agent.yaml`, or
   `kubectl -n sounding-node rollout restart daemonset/node-agent`.
3. **Update your workloads.** For images with the agent built in, rebuild and push them as above, then deploy the new
   tag. For workloads whose init container copies the agent, change the init container's tag, or with `agent-latest`
   set `imagePullPolicy: Always` on it, and restart the workload:
   `kubectl -n my-app rollout restart deployment/web`.

Each replaced pod is a new container in the dashboard; see
[Sounding on Kubernetes](../concepts/kubernetes.md#hosts-and-containers).

A dashboard never runs against tables newer than its own version: if you go back to an older dashboard, it stops and
its log says to upgrade it. To return to an older version, restore the backup you took before upgrading.

After upgrading, open a workload you just updated. Select **1m** on its **CPU** page and check that new samples
continue to appear.

## Next steps

- [Configuration settings](../reference/configuration.md)
