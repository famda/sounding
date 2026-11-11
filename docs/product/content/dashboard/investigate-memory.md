---
title: Investigate memory problems
description: Find out how close a container is to its memory limit, what fills its memory, and whether anything was killed.
---

The Memory page tells you how close a container is to its limit, what its memory is made of, and whether the limit has
already caused trouble.

![The Memory page of a container](../../assets/container-memory.png)

## 1. Compare use with the limit

Open the container, then **Memory**. The first chart shows the memory it uses, and its limit as a dashed line.

The figure leaves out cache the system can free at any time. The limit counts that cache, though, so the system starts
freeing memory a little before the line reaches the limit.

## 2. Check whether the limit caused trouble

Look at **Breakdown and limit events**:

- **Out-of-memory kills** counts programs in the container killed by the out-of-memory killer. Investigate both the
  container's limit and the host's available memory rather than assuming the container limit alone caused it.
- **Hit the limit** counts how often the container reached its limit.

Both count since the container started, so a non-zero value may be from the past. Open **Events** to see when it
happened, and which process was most likely killed.

## 3. Check for pressure

These charts show whether memory is tight right now:

- **Memory pressure**: how often work stalled waiting for memory.
- **Memory being freed**: the system working to free memory. Steady activity means memory is tight.
- **Memory read from disk**: memory that had to be read back from disk. A steady rate can be a sign of memory pressure;
  check it alongside memory pressure and the workload.

## 4. Find what fills the memory

**Memory composition** splits the container's memory:

- **Program data** is the programs' own data. It grows steadily when a program leaks memory.
- **Cache** holds recently read files. The system frees it when memory is needed, so a large cache is fine.
- **Kernel** is memory the system uses on the container's behalf.

Then scroll to **Processes by memory**. In the default **Last …** view, **Growth** shows how fast each process's memory
grew during the time range: steady growth is what a leak looks like.

## 5. Check the host

On the host's **Memory** page, check its available memory and **Swap activity**. If the host itself is short of memory,
every container on it is affected, whatever its limit.

## Next steps

- [Investigate disk usage](investigate-disk.md)
- [Memory in the metrics glossary](../reference/metrics.md#memory)
