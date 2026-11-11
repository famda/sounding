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

The setup guide and demo use the `latest` tags. To choose when to upgrade, use the commit tags, with the same commit
for the agent and the dashboard.

## Upgrade

Back up the database before upgrading. Schema updates are automatic, but they don't always preserve every kind of
history: the current migration from older JSON-backed sample tables deletes the old host and container chart samples.

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

The database schema is updated automatically the first time the new version connects. To inspect history removed by
the migration, restore a backup to a separate database and use the commit tags of the Sounding build you ran before. Connecting the
new version to that restored database applies the migration again.

## Next steps

- [Configuration settings](../reference/configuration.md)
