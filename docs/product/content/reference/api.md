---
title: REST API
description: Read what Sounding collects from your own scripts, tools and reports.
---

Everything the dashboard shows is also available as JSON, so your own scripts, tools and reports can use it. The API
runs on the dashboard's port, under `/api/v1`.

Want to ask a question rather than write requests? [Connect an AI assistant](../ai-assistants/connect-an-ai-assistant.md)
to investigate the same monitoring data through read-only tools.

> [!WARNING]
> Like the dashboard, the API has no sign-in. Anyone who can reach it can read everything and start packet captures.
> See [Secure Sounding](../operate/secure-sounding.md).

## Try it

Start with a question: **what needs attention right now?** The answer below is shortened, and comes from the worker in the
[memory leak tutorial](../ai-assistants/tutorial-find-a-memory-leak.md):

```bash
curl http://localhost:8080/api/v1/diagnostics
```

```json
{
  "status": "critical",
  "critical": 1,
  "warnings": 0,
  "findings": [
    {
      "severity": "critical",
      "code": "container-crash-loop",
      "targetName": "worker",
      "links": [{ "rel": "processTrends", "href": "/api/v1/containers/b7c0c89f252e/processes/top?sort=memory" }],
      "message": "Keeps restarting: started 6 times in the last 15 min, about every 28 s, and ended with an error 5 time(s) (last: Stopped by signal 9). In that time it reached its memory limit 10 time(s): ..."
    }
  ]
}
```

## Follow the evidence

You don't have to guess the next request. Each finding comes with `links`: requests that show more about it, ready
to call. Some also carry `advice`, for what only you can check.

In this example, the worker keeps restarting and has reached its memory limit. Check its current state, look back
at its stops and restarts, then find the processes whose memory grew. Use the container name you see on the dashboard:

```bash
curl http://localhost:8080/api/v1/containers/worker
curl "http://localhost:8080/api/v1/containers/worker/events?seconds=900"
curl "http://localhost:8080/api/v1/containers/worker/processes/top?seconds=900&sort=memoryGrowth"
```

The same investigation is available in conversation: the [memory leak tutorial](../ai-assistants/tutorial-find-a-memory-leak.md)
shows how an assistant follows the evidence and explains what it cannot confirm.

To browse every request and try it from your browser, open `http://<dashboard address>:8080/api/docs`.

## The requests

Most of what you can ask exists at three levels: for everything, for one host, and for one container. The level is
always in the address:

| For | Address starts with |
|---|---|
| Everything | `/api/v1` |
| One host | `/api/v1/hosts/{host}` |
| One container | `/api/v1/containers/{container}` |

Add one of these to it:

| Add | You get | Levels |
|---|---|---|
| nothing | The host or container now: latest figures, limits, restarts and how it stopped. | Host, container |
| `/diagnostics` | The problems Sounding sees, most serious first. | All |
| `/events` | Restarts, stops, out-of-memory kills, new ports and destinations. See [events](events.md). | All |
| `/timeline` | Events and the moments figures jumped or dropped, in order. | Host, container |
| `/changes` | The same across every container: what changed, in order. | Everything, host |
| `/contention` | Whether the host is short of CPU, memory or disk, and which containers take the most. | Host, container |
| `/checks/{check}` | A yes or no to one question, such as `memoryGrowing` or `requestsFailing`, with the figures behind it. | Container |
| `/history` | Chart data for CPU, memory, disk, network and GPU. | All (`/fleet/history` for everything) |
| `/history/detail` | Chart data for every other figure. See [metrics](metrics.md). | Host, container |
| `/history/summary` | Lowest, average, highest, last and trend of each figure. | Host, container |
| `/history/comparison` | The figures that differ between the last period and the one before. | Host, container |
| `/processes` | Processes now, or at a past moment with `at`. | Host, container |
| `/processes/top` | The busiest processes over a period, and those growing in memory. | Host, container |
| `/dependencies` | Which services call which, and how many calls fail or are slow. | All |
| `/dependencies/trace` | What a container calls, service by service, with each one's state and problems. | Container |
| `/network/overview` | Ports, busiest peers, failing requests and failed name lookups. | All |
| `/network` | Every connection. Large. | All |
| `/connections` | Connections, filtered and sorted. | Container |
| `/logs` | The most recent output. | Container |
| `/storage` | What fills the disk, and when it will be full. | Container |
| `/captures` | Packet captures: list them, or start one with `POST`. | Container |

Three more stand on their own: `/api/v1/hosts` and `/api/v1/containers` list your hosts and containers,
`/api/v1/captures/{id}` downloads a finished capture, and `/api/v1/fleet/stream` sends the state of everything about
once a second.

The API also describes itself in the OpenAPI format at `/openapi/v1.json`, which always matches the version you run.
Load it into an API tool, or generate a client in your language from it.

## Name hosts and containers

Wherever an address needs a host or a container, you can use its id, its name, or a part of its name that only it
has: `orders-api` finds `shop-orders-api-1`. If the name matches several, the answer lists them with their ids.
The one exception is a container recreated under the same name on the same machine: its running instance is meant.
A part of a name shared by different containers, such as `api` for `orders-api` and `catalog-api`, always needs
the longer name or the id, even when only one of them is running.

Lists show what the dashboard shows: containers that stopped or went quiet a few minutes ago drop out of them.
Asking for one by name or id still finds it, and its history and events stay available. A machine that stopped
reporting long ago is the exception for `/api/v1/hosts/{hostId}` itself: it has no current figures, so only its
history, events and timeline stay available.

## Periods and long lists

- Requests about a period take its length as `seconds`, from 10 seconds to 24 hours. Without it, they cover the last
  minute. Some look further back by default: events an hour, the timeline and changes 30 minutes, and diagnostics
  15 minutes of events (`eventSeconds`).
- The comparison (`/history/comparison`) is the exception: its `seconds` is the length of each of the two periods it
  compares, from 10 seconds to 12 hours, and 15 minutes by default.
- The lists of hosts and containers come in pages of up to 200 (`page`, `pageSize`), and say how many there are in
  total. A page past the end is empty. Other lists take a `limit`.
- An answer that could not look at everything says so: a diagnosis lists its `gaps`, changes their `coverage` (with
  more than 10 machines, only 10 are read), the timeline and event summary a `note` when a period holds more events
  than are read, and a dependency trace its `coverage`. Without such a remark, nothing was left out.

## Start a packet capture

```bash
curl -X POST http://localhost:8080/api/v1/containers/worker/captures \
  -H "Content-Type: application/json" -d '{"seconds": 10}'
```

The answer has the capture's `id`. The capture takes as long as you asked, from 1 to 60 seconds. Check
`/api/v1/containers/worker/captures` until its status is `done`, then download `/api/v1/captures/{id}`.

## When a request fails

Every failed request answers in the same format, [Problem Details](https://www.rfc-editor.org/rfc/rfc9457) JSON
(`application/problem+json`): `status` and `title` say what kind of failure it is, and `detail` explains why.

| Status | Meaning |
|---|---|
| `400` | A parameter is wrong or out of range, or a name matches several hosts or containers. `errors` says which. |
| `403` | A web page on another site tried to start a capture. Call the API from a script, or from the dashboard's own address. |
| `404` | Sounding doesn't know that host, container or capture, or the address is wrong. |
| `405` | The request uses the wrong method, such as `POST` instead of `GET`. |
| `409` | A packet capture is already running, or the container isn't running. |
| `415` | A `POST` didn't send JSON. Add the `Content-Type: application/json` header. |
| `500` | Something unexpected failed. The dashboard's log has the details. |
| `503` | The dashboard has just started. Try again in a few seconds. |

## Next steps

- [Metrics](metrics.md): what each figure means
- [Events](events.md)
- [Connect an AI assistant](../ai-assistants/connect-an-ai-assistant.md), to get answers instead of data
