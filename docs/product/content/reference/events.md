---
title: Events
description: Every event Sounding records, what it means, and its level.
---

The **Events** page lists what happened on a host or container. Each event has a **kind** (the badge in the **Kind**
column) and a **level**: info, warning, or error. Warnings and errors are highlighted. A restart warning may suggest memory exhaustion based on the last sample.
An `oom` event records that a kill occurred. The named victim is Sounding’s best estimate.

| Kind | Level | Example | Meaning |
|---|---|---|---|
| `lifecycle` | info | Started | Sounding started in the container: the container started or restarted. |
| `lifecycle` | info | Exited with code 0, Stopped | The container's application ended normally. |
| `lifecycle` | warning | Exited with code 1, Stopped by signal 9 | The application ended with an error, or was stopped by a signal (9 is a forced kill). |
| `lifecycle` | warning | Restarted after stopping without warning: killed at once (forced, or out of memory), or its host stopped; its last sample, 4 s before this start, had memory at 48% of its limit | The container was stopped all at once, Sounding included, and has started again. Its memory at its last sample is the only evidence Sounding has of why: it doesn't know whether the container ran out of memory or was killed. |
| `lifecycle` | warning | Restarted after stopping without warning, possibly out of memory: its last sample, 2 s before this start, had memory at 96% of its limit | The same, with memory at 90% or more of its limit in a sample from the last 5 minutes. An out-of-memory kill is likely, not certain: on Kubernetes, `kubectl describe pod` shows **OOMKilled** when it was. |
| `process` | info | Started worker (pid 42), 3 processes exited: … | Processes started or ended in the container. |
| `oom` | error | Out of memory: worker (pid 42) was killed, using 512 MB | The container ran out of memory and the system killed a process. The process named is the one most likely killed. |
| `identity` | warning | SOUNDING_POD_UID, SOUNDING_POD_CONTAINER not set: read from the pod's mounts … | Kubernetes: settings that identify the container are missing, so the agent read them from the pod's files, or couldn't find them and recorded this run as a new container. See [configure the agents](../deployment/kubernetes.md#3-configure-the-agents-and-deploy). |
| `memory` | warning | Reached its memory limit | The container reached its memory limit. |
| `cpu` | warning | CPU throttled: held back in 40% of periods by its limit | At least 25% of CPU quota periods were throttled in a sample. Another event can be recorded after 10 consecutive samples below 1% throttling. |
| `port` | info | Listening on 8080/tcp (nginx) | A new port started accepting connections. |
| `destination` | info | New internet destination api.example.com (203.0.113.10):443/tcp | The container contacted an internet address for the first time. |
| `gpu` | error | GPU 0 reported Xid 79 | The NVIDIA driver reported an error on this GPU. Look up the Xid number in NVIDIA's documentation. |

Process starts and exits aren't recorded when a container has more processes than Sounding lists (more than 100), to
avoid reporting processes that only left the list.

Events are kept as long as the rest of the history: 24 hours by default.
