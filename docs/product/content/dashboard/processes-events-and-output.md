---
title: Processes, events, and output
description: Find the process behind a change, see what happened and when, and read what the containers printed.
---

Charts tell you that something changed. Process tables, events, and output can help you investigate what changed and
when.

## Processes

The CPU, Memory, GPU, and Disk pages end with a process table, sorted by what that page is about.

- **Last …** (the default) covers the selected time range: every process seen in it, including those that have ended,
  with averages and totals over the time each ran.
- **Now** shows the processes at this moment.
- **Hover over a chart** to see the processes at that moment.

Select a process to see the processes it started. On the CPU page, you also see the threads of the busiest processes.

Flags mark processes worth a second look:

| Flag | Meaning |
|---|---|
| **Sounding** | Part of Sounding: its launcher or agent. Use **Hide Sounding** to hide them. |
| **root** | Runs as the administrator inside the container. |
| **+N privileges** | Has Linux privileges beyond Docker's defaults. |
| **unrestricted** | Runs without Docker's security filter, so it can make any system call. |

Sounding can't read every detail of processes owned by another user. See
[allow packet inspection and process details](../operate/allow-packet-inspection-and-process-details.md).

## Events

The **Events** page lists what happened in the selected time range, newest first: containers starting and stopping,
processes starting and exiting, out-of-memory kills, memory limits reached, CPU throttling, new listening ports, new
internet destinations, and GPU errors.

![The Events page of a host: what happened, how much the containers printed, and their latest output](../../assets/host-events.png)

Use it to match a change in a chart with its cause: a spike in memory just before an out-of-memory kill, or a CPU drop
when a process exited. See the [list of events](../reference/events.md).

## Output

The **Events** page also shows what the containers print:

- **Log lines**: lines printed per second.
- **Errors and warnings**: lines that look like errors or warnings because they contain a word such as *error*,
  *fatal*, *exception*, or *warn*. This is a guess from the text, not the program's own log level.
- **Latest output**: the last lines each container printed.

This is a glimpse, not a log archive. Keep sending your logs where you do today.

> [!NOTE]
> Output isn't available when the container has a terminal (`docker run -t`, or `tty: true` in Compose).

## Next steps

- [How Sounding works](../concepts/how-sounding-works.md)
- [Events reference](../reference/events.md)
- [Events and output in the metrics glossary](../reference/metrics.md#events-and-logs)
