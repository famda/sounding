---
title: Frequently asked questions
description: Short answers to common questions about Sounding.
---

## Do I have to change my container images?

With [Docker Compose](../deployment/docker-compose.md), yes. Copy the agent into your image and start your existing
command through its launcher. On [Kubernetes](../deployment/kubernetes.md#2-add-the-agent-to-your-workload), an init
container can supply the agent to an existing image if cluster policy permits. In either case, the agent runs inside
each container you monitor.

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

The agents write to your PostgreSQL database, and the dashboard reads from it. Sounding sends no usage data. If
you [connect an assistant](../ai-assistants/connect-an-ai-assistant.md) that uses a hosted model, the model’s
provider receives the monitoring data the assistant reads.

## How far back can I look?

24 hours. The database keeps 24 hours of history by default, and the dashboard shows up to 24 hours.

## Does the dashboard have users and passwords?

No. Keep it private, or put it behind a reverse proxy that signs users in. See
[secure Sounding](../operate/secure-sounding.md).

## Does it work with Kubernetes, Podman, or ARM?

Kubernetes and ARM64 (including Apple silicon Macs), yes. Podman, not yet. See [supported platforms](../index.md#supported-platforms).

## Can I use it with Alpine or distroless images?

Yes. The agent brings everything it needs, so it works in any Linux image, including Alpine, BusyBox, and distroless.
