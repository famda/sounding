---
title: Upgrade Sounding
description: Move the dashboard and your agents to a new version of Sounding.
---

Sounding's agent and dashboard are built together from the same commit. Run the same build for both.

## Choose an image tag

Both images are published to the [`famda/sounding`](https://hub.docker.com/r/famda/sounding) repository on Docker Hub,
with a tag prefix for each part. Every build is tagged with its short commit hash, for example `1a2b3c4`:

| Agent tag | Dashboard tag | Follows |
|---|---|---|
| `famda/sounding:agent-latest` | `famda/sounding:dashboard-latest` | The newest build. |
| `famda/sounding:agent-1a2b3c4` | `famda/sounding:dashboard-1a2b3c4` | Exactly this build. |

The setup guides and demos use the `latest` tags. To choose when to upgrade, use the commit tags, with the same commit
for the agent and the dashboard.

## Upgrade

Back up the database before upgrading. Schema updates are automatic, but they don't always preserve every kind of
history: the current migration from older JSON-backed sample tables deletes the old host and container chart samples.

### With Docker Compose

1. **Update the dashboard.** Change its image tag if you pin a commit, then pull and recreate it:

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

The sample's manifests use the `-latest` tags with `imagePullPolicy: Always`, so a restart pulls the newest build. To
choose when to upgrade, set the commit tags in `sounding.yaml` and `node-agent.yaml` instead.

1. **Update the dashboard.** With a commit tag, change its `image` and run `kubectl apply -f sounding.yaml`. With
   `dashboard-latest`, restart it: `kubectl -n sounding rollout restart deployment/dashboard`.
2. **Update the node agent**, if you run it, the same way: `kubectl apply -f node-agent.yaml`, or
   `kubectl -n sounding-node rollout restart daemonset/node-agent`.
3. **Update your workloads.** For images with the agent built in, rebuild and push them as above, then deploy the new
   tag. For workloads whose init container copies the agent, change the init container's tag, or with `agent-latest`
   set `imagePullPolicy: Always` on it, and restart the workload:
   `kubectl -n my-app rollout restart deployment/web`.

Each replaced pod is a new container in the dashboard; see
[Sounding on Kubernetes](../concepts/kubernetes.md#hosts-and-containers).

The database schema is updated automatically the first time the new version connects. To inspect history removed by
the migration, restore a backup to a separate database and use the commit tags of the Sounding build you ran before. Connecting the
new version to that restored database applies the migration again.

After upgrading, open a workload you just updated. Select **1m** on its **CPU** page and check that new samples
continue to appear.

## Next steps

- [Configuration settings](../reference/configuration.md)
