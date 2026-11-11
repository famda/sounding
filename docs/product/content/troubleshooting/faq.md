---
title: Frequently asked questions
description: Short answers to common questions about Sounding.
---

## Do I have to change my container images?

Yes. Sounding runs inside each container you monitor, so its agent must be in the image: one `COPY` line and a change to
the `ENTRYPOINT`. See [add Sounding to your application](../get-started/getting-started.md).

## Is it a sidecar? Does it need the Docker socket?

No to both. The agent runs inside your container, next to your application, and reads what Linux reports to the
container. It doesn't need access to Docker.

## Does it change how my application runs?

The launcher starts your application with the same user and command. It passes signals to it, keeps its exit code, and
passes its output through unchanged. Sounding adds an agent inside the container, so its CPU and memory use count
toward that container. It also sets `CUDA_INJECTION64_PATH` to measure GPU use, unless your container already sets it.

## What happens if the agent or the database fails?

Your application keeps running. Only the monitoring stops: figures taken while the database is unreachable are lost.

## How much does the agent cost?

The agent's CPU and memory count toward the container. Find processes marked **Sounding** in the process tables to see
its activity. Each agent keeps one database connection and writes about one row of figures per second by default. To
reduce the load, measure less often with `SOUNDING_INTERVAL_MILLISECONDS`.

## Does Sounding see my other containers?

It shows the containers that have the agent in their image. Figures for a whole host, such as its CPU or memory, include
everything running on the machine, including containers without Sounding.

## Is my data sent anywhere?

No. The agents write only to your PostgreSQL database, and the dashboard reads only from it. Sounding has no cloud
service and sends no usage data.

## How far back can I look?

24 hours. The database keeps 24 hours of history by default, and the dashboard shows up to 24 hours.

## Does the dashboard have users and passwords?

No. Keep it private, or put it behind a reverse proxy that signs users in. See
[secure Sounding](../operate/secure-sounding.md).

## Does it work with Kubernetes, Podman, or ARM?

Not yet. Sounding supports x86-64 Linux containers on Docker Engine and Docker Desktop. See
[requirements](../reference/requirements.md).

## Can I use it with Alpine or distroless images?

Yes. The agent brings everything it needs, so it works in any Linux image, including Alpine, BusyBox, and distroless.
