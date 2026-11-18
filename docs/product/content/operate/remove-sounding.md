---
title: Remove Sounding
description: Take the agent out of your images and stop the dashboard; optionally delete Sounding's data.
---

To stop using Sounding, remove the agent from your images and stop the dashboard. On Kubernetes, see
[On Kubernetes](#on-kubernetes). Deleting Sounding's stored data is optional.

## 1. Take the agent out of your images

In each Dockerfile you changed:

1. Delete the `COPY --from=famda/sounding:agent-…` line.
2. Remove `/opt/sounding/run` from the `ENTRYPOINT`, putting back the original entrypoint. If the image had none,
   delete the `ENTRYPOINT` line.

Then remove the `SOUNDING_` settings from your services, and any mounts or capabilities you added only for Sounding
(such as `/host/proc` or `SYS_PTRACE`). Rebuild and redeploy. Confirm the application handles a normal request or completes a usual job with its
original startup command.

## 2. Stop the dashboard

Remove the `dashboard` service from your Compose file, then run:

```sh
docker compose up -d --remove-orphans
```

## 3. Optional: delete the data

> [!WARNING]
> Deleting the database or its tables can't be undone. Check that you're connected to the right database and make any
> needed backups first.

If Sounding has a dedicated database and user, connect to a different database, then delete them. Delete the user only
if it isn't used elsewhere:

```sql
DROP DATABASE sounding;
DROP USER sounding;
```

If Sounding shares a database with other data, delete only Sounding's tables:

```sql
DROP TABLE captures, events, process_samples, container_samples, host_samples, host_parts, containers, hosts;
```

## On Kubernetes

1. **Take the agent out of your workloads.** Remove it from the images you build, as above. For workloads whose init
   container copies it, delete the `sounding-agent` init container and the `sounding` volume and its mount, and put
   back the image's own `command`. Remove the `SOUNDING_` settings, then deploy the changes. Verify the workload handles a request or completes a job.
2. **Delete the settings** from each workload namespace:

   ```shell
   kubectl -n my-app delete secret sounding-agent
   kubectl -n my-app delete configmap sounding-agent
   kubectl label namespace my-app sounding-agents-
   ```

3. **Remove the node agent**, if you run it: `kubectl delete -f node-agent.yaml`. This deletes the `sounding-node`
   namespace.
4. **Stop the dashboard, and keep the data**, by deleting only the dashboard:

   ```shell
   kubectl -n sounding delete deployment/dashboard service/dashboard networkpolicy/dashboard
   ```

   Or **delete Sounding with its data**. With the bundled PostgreSQL, deleting the namespace deletes the database's
   volume and every sample in it, which can't be undone:

   ```shell
   kubectl delete namespace sounding
   ```

   With your own PostgreSQL, delete the data as in [step 3](#3-optional-delete-the-data).
