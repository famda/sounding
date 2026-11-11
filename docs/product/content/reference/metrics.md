---
title: Metrics glossary
description: What each figure on the dashboard means, and what to compare it with.
---

What each figure on the dashboard means, page by page. Where Linux uses a different name for it, that name follows in
italics, so you can look it up elsewhere.

Jump to [CPU](#cpu), [memory](#memory), [GPU](#gpu), [disk](#disk-and-filesystem), [network](#network), or
[processes](#processes).

## Reading the numbers

| Term | Meaning |
|---|---|
| Host, container, fleet | A host figure covers the whole machine (on Docker Desktop, Docker's virtual machine). A container figure covers one container. A fleet figure adds up all hosts. |
| Now or total | Most figures describe the latest moment. A few are **totals** since the container or machine started, such as out-of-memory kills or interface errors: a non-zero total may be from the past. |
| Rate | A change per second, such as bytes per second or requests per second. Rates need two samples before they can be calculated. |
| Percent | Always check what it's a percentage of: a container's limit, the host's capacity, or the share of time. The chart's label says which. |
| Capacity and limit | **Capacity** is what a host or device has. A **limit** is what Docker allows a container to use; it isn't reserved for it. See [limits and capacity](../concepts/limits-and-capacity.md). |
| Some waiting, all waiting | Pressure charts show the share of time **some** of the work, or **all** of it at once, was stalled waiting for the processor, memory, or disk (*PSI some and full pressure*). Hosts only show some waiting for the CPU. |
| Missing values | **–**, **Not available**, or a gap in a chart means Sounding couldn't read the value. It isn't zero. See [missing values](../concepts/samples-and-history.md#missing-values). |

## Overview

| Term | Meaning |
|---|---|
| **In use**, **Host total** | What the host or container uses now. On a host, **Host total** includes everything on the machine. |
| **Containers** | What the monitored containers use, added up. |
| **Other** | What the host uses outside the monitored containers: the system, programs on the machine itself, and containers without Sounding. |
| **Available**, **Free** | Memory available for new work (free, or cache that can be freed), and unused disk space. |
| **Limits** | The containers' limits added up, next to what the host has. When they add up to more, the containers can't all reach their limits at once. |
| **Running**, **Not reporting**, **Stopped** | A container's status. See [status](../concepts/hosts-and-containers.md#status). |

## CPU

| Figure | Meaning |
|---|---|
| CPU in use | Processing time used, in cores: 1 core is one processor core busy all the time. |
| CPU limit, allowed cores | The container's limit in cores, and the cores it may run on. A container can have either, both, or neither. |
| CPU pressure | The share of time work was ready to run but waited for a processor. A steady few percent means competition for the CPU. |
| CPU throttling | Time the container was held back by its CPU limit, in milliseconds per second (*throttled time*). It can pass 1,000 ms/s, because it adds up across cores. The quota applies per scheduling period, commonly 100 ms but configurable, so bursts can be throttled even when average use stays below the limit. |
| Throttled now, throttled periods | The share of CPU quota periods in which the limit held the container back, and how many times it has happened since the container started. |
| CPU time split | Host processor time outside the programs' own work: **System** (the system working for the programs), **I/O wait** (idle while waiting for the disk), **Interrupts** (hardware events), and **Stolen by VM** (time the virtual machine's host gave to other machines, *steal*). |
| Load average | The host's work waiting for or using a processor, averaged over 1, 5, and 15 minutes. Compare it with the number of cores; it isn't a percentage. |
| CPU queue | Threads running or ready to run (**Running**), and threads waiting on the disk or a network drive (**Waiting on I/O**) (*run queue* and *blocked*). More running threads than cores means some wait. |
| Context switches | How often the host's processors switch from one thread to another, per second. |
| Processes started | New processes and threads per second. A steady high rate means something keeps starting short-lived programs, whose CPU time the process list misses. |
| Tasks | The container's processes and threads, against its limit, if it has one. |
| Temperatures | The machine's temperature sensors, with their high and critical marks. Virtual machines usually have none. |

## Memory

| Figure | Meaning |
|---|---|
| In use (host) | Memory taken, leaving out cache the system can free. **Available** is what new work can use. |
| In use (container) | The container's memory, leaving out inactive cache, as `docker stats` does. **With reclaimable cache** includes it. The limit counts that cache too, so the system starts freeing memory a little before the line reaches the limit. |
| Limit, peak | The container's memory limit, and the most it has used, cache included. |
| Program data | The programs' own data, such as their heaps and stacks (*anonymous memory*). It can't be dropped, only moved to swap, so it's what makes a container reach its limit. Steady growth can mean a leak. |
| Cache | Recently read files kept in memory (*page cache*). The system frees it when memory is needed. |
| Shared memory, network buffers, kernel | Smaller parts of the container's memory. They can overlap the others, so they don't add up to the total. |
| Memory pressure | The share of time work stalled waiting for memory. Steady pressure means memory is too tight. |
| Memory read from disk | Memory read back from disk per second: **From disk** (*major page faults*), and **Re-read**, files read again soon after being dropped from the cache (*refaults*). A steady rate can indicate memory pressure; check it alongside pressure and the workload. |
| Swap | Memory moved to disk. **Swap activity** on a host shows memory moved out and back per second; steady activity slows everything down. |
| Memory being freed | The system looking for memory to free (**Checked**) and freeing it (**Freed**) (*reclaim*). Checking much more than it frees means memory is very tight: the step before out-of-memory kills. |
| Unsaved writes | Data written by programs that is still in memory, waiting to be saved (**Waiting**) or being saved (**Being written**) (*dirty* and *writeback*). Growth means writes arrive faster than the disk takes them. |
| Out-of-memory kills, hit the limit, over the soft limit | Since the container started: programs killed for lack of memory, times the container reached its limit, and times it went over its soft limit (`memory.high`), where the system slows it down instead of killing. |
| Committed | Memory programs have reserved on the host, against the system's limit for it. Whether reaching it causes failures depends on the system's settings. |
| Kernel caches | Memory the system uses for its own caches (*slab*): the part it can free, and the part it can't. |

## GPU

| Figure | Meaning |
|---|---|
| Compute | The share of time the GPU was running work. It isn't how much of the chip was used: a card at 100% can have spare capacity. A container's compute is the time its CUDA programs' work ran. |
| VRAM | GPU memory in use. A container's VRAM is what its CUDA programs allocated. |
| Peak VRAM | The most GPU memory a container or program has held. |
| To GPU, to host | Data copied between the computer's memory and the GPU per second. On a host page, the containers' CUDA programs added up. |
| GPU kernel launches, average GPU kernel | Pieces of GPU work started per second, and how long each takes on average. A *kernel* here is a piece of GPU work, unrelated to the Linux kernel. |
| Top GPU kernels | The GPU work that took the most time, by name, as a share of one GPU. Work running side by side can add up to more than 100%. |
| Failed allocations | GPU memory requests that failed. |
| Power, power limit, energy | The card's power draw, its limit, and the energy used over the selected range. Always the whole card. |
| GPU clock, memory clock | The card's current and maximum speeds, in MHz. |
| Clock limited by | Why the card isn't at full speed. **GPU idle** is normal; a power or temperature reason means it's being held back. |
| Temperature, headroom | The card's temperature, and how far it is from slowing down to cool. |
| Performance state, PCIe link, video encoder and decoder | The card's power mode, its connection to the computer, and the use of its video engines. |
| Memory errors (ECC) | Memory errors the card corrected, and those it couldn't. Any uncorrected error is worth investigating. |

## Disk and filesystem

| Figure | Meaning |
|---|---|
| Read, write | Data read from and written to disk per second. |
| Host vs containers | The host's disk traffic next to the containers'. The two are counted differently, so compare their trends, not exact amounts. |
| Disk operations | Read and write requests per second (IOPS). Many small requests can overload a disk even with little data. |
| I/O pressure | The share of time work waited for the disk. Steady pressure means the disk is a bottleneck. |
| Busy | The share of time a host disk had I/O in progress. Close to 100% means continuous activity, not necessarily maximum throughput, especially on devices that handle requests in parallel. Compare latency and I/O pressure too. |
| Read time, write time, queue | How long each request took on average, and how many were waiting at once. |
| Space used, files | The size of the container's files, and how many there are: its image plus everything it wrote, without mounted volumes. Containers built from the same image each count the image's files. |
| Growing, disk full in | How fast the container's files grow, and when the disk would be full at that rate. They need a few minutes of measurements. |
| Inodes used | Every file uses one inode, and a disk has a fixed number. At 100%, no new files can be created even with free space. |
| Volumes and mounts | The container's volumes, bind mounts, and temporary file systems, with the free space where each is stored. |
| Largest folders, largest files | What takes the most space in the container's files, without mounted volumes. |
| Growing folders | Folders that grew since the previous filesystem scan. Each scan is followed by a 30-second pause, so large filesystems have longer intervals. |
| Deleted but still open | Files deleted while a program still has them open. Their space is freed only when the program closes them. |
| Open files | Files, connections, and other handles a process has open, against its limit. |

## Network

| Figure | Meaning |
|---|---|
| In, out | Data received and sent per second. A container's traffic includes traffic to other containers; the host's leaves out Docker's internal interfaces, so it's only what enters and leaves the machine. |
| Packets | Packets received and sent per second. Many packets with little data means many small messages. |
| Internet vs internal | Traffic to and from public addresses, and everything else (**Not internet**): other containers, the host, your network. **Same network** is the part to containers on the same Docker networks. |
| Interface errors, drops | Totals since the machine or container started. A growing count matters more than an old one. |
| TCP health | Per second: data sent again (**Resent**, *retransmits*), connections cut off by this side (**Connections reset**), and connection attempts that failed (**Failed connections**). |
| UDP errors | UDP messages lost on arrival, lost because the program was too slow to read them (**Buffer full**), or sent to a port nobody listens on (**No listener**). |
| Connection tracking | Connections the system tracks for its firewall and port forwarding, against its limit. When it's full, new connections are dropped. |
| DNS, DNS reply time | Name lookups per second, those that failed, and how long they took. A host shows its slowest container. |
| Connection states | The container's connections by state. Many `TIME_WAIT` means many short connections; a growing `CLOSE_WAIT` count means a program isn't closing connections the other side already closed. |
| Local ports in use | How many of its local ports the container uses for its busiest destination. When they run out, new connections to it fail. |
| Accept queue, clients | Connections waiting for the program to accept them, and the addresses that connected in the last hour. |
| Handshake, round trip | The time a connection took to open, and the current time for data to travel there and back. |
| Delivery rate, limited by | How fast data was delivered, and what limited sending: the receiver, the send buffer, the program not sending, or the network. |
| Queued, resent | Data waiting in the connection now, and data sent again in each direction. |
| Sent, received, in, out | Data over the connection's lifetime, and per second now. |
| First seen, last seen, new destination | When the connection was first and last seen, and whether its destination was first contacted in the last 15 minutes. |
| Requests: per second, errors | Requests per second, and the share that got an error reply, over the last minute. HTTP counts 5xx replies (not 4xx); mail and FTP count 4xx and 5xx; databases and message brokers count their error replies. |
| Median, 95th | Half, and 95 in 100, of the answers came within this time. Messages that get no answer have no time. |
| Sent / received, per connection | The average size of requests and answers, and decoded requests per observed connection in the last minute. A value of 1 means one request per connection in that window, not necessarily a newly opened connection. |

## Processes

| Figure | Meaning |
|---|---|
| PID, name, user, command | Which program it is, and who runs it. Hover a name for its full command line. |
| CPU, avg CPU, peak CPU | Cores used now, on average while it ran in the selected range, and at most. |
| Waiting, interrupted | Time it was ready but waiting for a processor, and how often it was taken off the processor, per second. |
| State, threads | Whether it's running, sleeping, waiting on the disk (`D`), or has exited but not been cleaned up (`Z`, a zombie), and its number of threads. |
| Memory, peak memory, growth | Memory it uses (*RSS*), the most it used, and how fast its memory grew during the range. Steady growth is what a leak looks like. Memory shared between processes counts in each of them. |
| Program data, code & files, swapped | Its own data, the files it has loaded such as its code and libraries, and memory moved to swap. |
| Own share | Its memory, with memory shared with other processes divided between them (*PSS*). |
| OOM score | How likely it is to be killed first when memory runs out, from 0 to 1000. |
| Read, write, open files | Its disk activity, and its open files against its limit. |
| Compute, VRAM, peak VRAM | Its GPU use and GPU memory. |
| Ports, connections | The ports it listens on, and the connections it holds. |
| Flags | **root** (runs as administrator), **+N privileges** (Linux privileges beyond Docker's defaults), **unrestricted** (no security filter on system calls), **shell**, **thread**, **Sounding** (part of Sounding). |
| Status | Whether it's still running, or when it exited. |

## Events and logs

| Item | Meaning |
|---|---|
| Events | Things that happened. See the [list of events](events.md). |
| Out-of-memory victim | The process most likely killed, worked out from which process disappeared. It may not always be the right one. |
| Log lines, errors, warnings | Lines printed per second, and lines that look like errors or warnings because they contain a word such as *error*, *fatal*, *exception*, or *warn*. |
| Latest output | The last lines each container printed. Not available when the container has a terminal. |
