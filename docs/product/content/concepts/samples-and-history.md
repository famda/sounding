---
title: Samples and history
description: How often Sounding measures, how much history you can see, and what a gap or a dash means.
---

By default, Sounding samples every container and host once a second. The dashboard shows those figures live and over
the last **1m**, **5m**, **15m**, **1h**, **6h**, or **24h**.

## Samples and rates

Each measurement is a **sample**. Many figures are **rates**, such as megabytes per second or requests per second. A rate
is the change between two samples, so Sounding can calculate it after the second sample.

Some figures are measured less often:

| Figure | Default schedule |
|---|---|
| Most figures | Every second |
| Process history (the **Last …** view of process tables) | About every 5 seconds; slower sampling intervals also slow this down |
| Disk space, largest folders and files, deleted files still open | A filesystem scan, followed by a 30-second pause before the next scan |
| Disk growth, and when the disk will be full | After a few minutes of measurements |

## History

Choose **1m**, **5m**, **15m**, **1h**, **6h**, or **24h** in the header. Each chart shows about 180 points, so wider
ranges average more samples into each point: in the 1-hour range, each point is the average of 20 seconds, and in the
24-hour range, of 8 minutes. Select a shorter range to inspect a brief spike.

Sounding keeps 24 hours of samples, events, and captures by default. The dashboard deletes older data every minute,
samples an hour at a time, so up to an hour more is kept. While no dashboard runs, nothing is deleted; it catches up
when one starts. See `SOUNDING_RETENTION_HOURS` in [configuration settings](../reference/configuration.md#history).

If an agent can't reach the database, it holds its samples in memory, by default for 15 minutes, and writes them once
it can: the charts show no gap. A longer outage leaves a gap for the samples it had to drop.

## Missing values

Not every figure is available on every machine or container. The dashboard shows:

- **–** or **Not available**, with the reason when it's known, when Sounding can't read a value. Kernel versions, virtual
  machines, and container permissions all affect what's readable.
- **A gap in a chart** when samples are missing, for example while the container was stopped or couldn't reach the
  database for longer than its agent holds samples in memory.

Neither means zero. A missing value says nothing about how much of the resource was used.

## Next steps

- [Limits and capacity](limits-and-capacity.md)
- [Metrics glossary](../reference/metrics.md)
