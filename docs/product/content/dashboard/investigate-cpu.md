---
title: Investigate a CPU slowdown
description: Find out whether a container is held back by its limit, competing for the processor, or busy with its own work.
---

When a container is slow, the CPU page tells you which of three things is happening: its **limit** is holding it back,
it's **competing** with other work for the processor, or one of its **processes** is simply busy.

![The CPU page of a container](../../assets/container-cpu.png)

## 1. Compare use with the limit

Open the container, then **CPU**. The first chart shows the cores it uses, and its CPU limit as a dashed line.

Use close to the limit is a first sign, but not the whole story: go on to the next step even if the line stays below the
limit.

## 2. Check for throttling

Look at **CPU throttling**. It confirms the limit held the container back at those times. If that overlaps with the
slowdown, try a higher limit or fewer concurrent threads, then compare the results.

> [!NOTE]
> A container can be throttled even when its average use stays below the limit. Its CPU quota is enforced per scheduling
> period, commonly 100 ms but configurable, and a program that works in short bursts on several threads can use up its
> share quickly.

To reduce throttling, raise the container's CPU limit or make the program use fewer threads at once.

## 3. Check for competition

Look at **CPU pressure**: the share of time work was ready to run but waiting for a processor. A few percent, steadily,
means the container is competing for the CPU, with other containers or with the host itself.

Then open the host's **CPU** page:

- **CPU queue** shows how many threads want a processor. Above the dashed line, which is the number of cores, some of
  them wait.
- **Cores** shows each core's load. One busy core with the rest idle points to a program that can only use one thread.
- The host overview shows which containers use the most.

## 4. Find the busy process

Scroll to **Processes by CPU**. By default it covers the selected time range, including processes that have already
ended; select **Now** for the current moment. Hover over a chart to see the processes at that moment.

Select a process to see the processes it started and, for the busiest, the threads using the CPU.

> [!TIP]
> If **Processes started** shows a steady high rate, something keeps starting short-lived programs, such as a health
> check or a crashing process. Their CPU time doesn't show up in the process list.

## Next steps

- [Investigate memory problems](investigate-memory.md)
- [CPU in the metrics glossary](../reference/metrics.md#cpu)
