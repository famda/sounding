---
title: Remove Sounding
description: Take the agent out of your images and stop the dashboard; optionally delete Sounding's data.
---

To stop using Sounding, remove the agent from your images and stop the dashboard. Deleting Sounding's stored data is
optional.

## 1. Take the agent out of your images

In each Dockerfile you changed:

1. Delete the `COPY --from=famda/sounding:agent-…` line.
2. Remove `/opt/sounding/run` from the `ENTRYPOINT`, putting back the original entrypoint. If the image had none,
   delete the `ENTRYPOINT` line.

Then remove the `SOUNDING_` settings from your services, and any mounts or capabilities you added only for Sounding
(such as `/host/proc` or `SYS_PTRACE`). Rebuild and redeploy.

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
