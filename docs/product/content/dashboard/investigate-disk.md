---
title: Investigate disk usage
description: Find out whether a disk is slow or full, and what is filling it.
---

A slow disk and a full disk are different problems. The Disk page helps with both.

![The Disk page of a container](../../assets/container-disk.png)

## A disk is slow

1. Open the container or host, then **Disk**.
2. Look at **I/O pressure**: how often work waited for the disk. A steady value means the disk is a bottleneck.
3. Compare with the read and write throughput, and **Disk operations**:
   - High pressure with **little data** often means many small requests. Disks are limited in requests per second as
     well as in bytes.
   - High pressure with **a lot of data** means the workload needs a faster disk.
4. On a host, **Devices** shows which disk the load lands on. **Busy** close to 100% means it was active almost
   continuously, not necessarily that it reached its maximum throughput. Compare it with latency and I/O pressure.
5. **Processes by disk I/O** shows which processes read and write the most.

**Host vs containers** compares the host's disk traffic with the containers'. Compare the figures for trends, not exact
totals: a large gap can reflect unmonitored activity or differences in how the figures are counted.

## A disk is filling up

1. Open the container, then **Disk**, and look at **Storage**:
   - **Space used** is the size of the container's files: its image plus everything it wrote since it started, without
     mounted volumes.
   - **Growing** and **Disk full in** estimate how fast it grows, and when the disk would be full at that rate. They need
     a few minutes of measurements.
   ![Largest folders and files, growing folders, and a deleted log file that still takes space](../../assets/container-disk-space.png)

2. Find where it's written:
   - **Growing folders** shows the folders that grew since the previous filesystem scan. Scans pause for 30 seconds
     after finishing, so the interval can be longer on large filesystems.
   - **Largest folders** and **Largest files** show what takes the most space.
3. Check **Deleted but still open**. A file deleted while a program still has it open keeps its space until the program
   closes it. This is common with log files rotated without telling the program, and can fill a disk with nothing
   visible.
4. Check **Volumes and mounts** for the free space of each volume. **Inodes used** close to 100% means no new files can
   be created, even with free space left.

> [!NOTE]
> Containers built from the same image each count the image's files, so the host's **Containers' space** can be more
> than the containers really take.

## Next steps

- [Investigate network activity](investigate-network.md)
- [Disk in the metrics glossary](../reference/metrics.md#disk-and-filesystem)
