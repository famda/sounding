---
title: Samples and history
description: How often Sounding measures, how much history you can see, and what a gap or a dash means.
---

By default, Sounding samples every container and host once a second. The dashboard shows those figures live and over
the last **1m**, **5m**, **15m**, **1h**, **6h**, or **24h**. Longer ranges average samples, so use a shorter range to
investigate a brief spike.

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
24-hour range, of 8 minutes. A short spike can disappear in a wide range; switch to a shorter one to see it.

The default retention target is 24 hours. Running host-sampling agents prune older samples, events, and captures
periodically. If all those agents stop, the dashboard alone doesn't delete old data; cleanup resumes when eligible
agents report again. Retention isn't an exact deletion deadline. See `SOUNDING_RETENTION_MINUTES` in
[configuration settings](../reference/configuration.md#sampling-and-history).

## Missing values

Not every figure is available on every machine or container. The dashboard shows:

- **–** or **Not available**, with the reason when it's known, when Sounding can't read a value. Kernel versions, virtual
  machines, and container permissions all affect what's readable.
- **A gap in a chart** when samples are missing, for example while the container was stopped or couldn't reach the
  database.

Neither means zero. A missing value says nothing about how much of the resource was used.

## Next steps

- [Limits and capacity](limits-and-capacity.md)
- [Metrics glossary](../reference/metrics.md)
