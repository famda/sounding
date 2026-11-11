---
title: Monitor NVIDIA GPUs
description: See how busy each NVIDIA card is, and how much of it each container's programs use.
---

GPU monitoring is optional and applies to NVIDIA GPUs. The GPU page answers two questions: **how busy is each card**,
and **how much of it does each container use**. They're measured differently, and the answers don't always add up: a
card can be busy with work outside your monitored containers.

![The GPU page of a host](../../assets/host-gpu.png)

## Make GPUs visible

Sounding shows a host's GPUs when one of its containers with Sounding has access to them.

1. Install the NVIDIA driver and the NVIDIA Container Toolkit on the host.
2. Give a container with Sounding access to the GPUs. With Docker Compose:

   ```yaml title="compose.yaml"
   services:
     my-cuda-app:
       image: your-cuda-app-with-sounding
       environment:
         SOUNDING_CONNECTION_STRING: ${SOUNDING_CONNECTION_STRING}
       deploy:
         resources:
           reservations:
             devices:
               - driver: nvidia
                 count: all
                 capabilities: [gpu]
   ```

3. Open the host's **GPU** page. Each card appears under **Cards**.

Each container's share appears on its own GPU page when it runs CUDA programs, as long as they start through Sounding's
launcher, which is the case when you've [added Sounding to the image](../get-started/getting-started.md).

> [!NOTE]
> Sounding measures CUDA programs. Graphics work, such as OpenGL or Vulkan, isn't counted per container, though it shows
> in the card's own figures.

## Read the GPU page

- **Compute** is the share of time the GPU was running work. It isn't how much of the chip was used: a card at 100% can
  still have spare capacity.
- **VRAM** is the GPU's memory in use. A container's VRAM is what its CUDA programs allocated.
- **Power draw**, clocks, and temperature belong to the card, even on a container's page.
- **Cards** shows each card now. **Clock limited by** says why it isn't at full speed: **GPU idle** is normal; a power or
  temperature reason means the card is being held back.
- **Top GPU kernels** shows the GPU work that took the most time, by name.
- **GPU processes** shows which programs use the GPU. **Peak VRAM** is the most memory a program held: about what it
  needs to run.

## What to look for

| You see | It usually means |
|---|---|
| Low **Compute**, heavy **Host ↔ GPU transfers** | The GPU waits for data. Move less data, or overlap copies with work. |
| **Clock limited by** a power or thermal reason | The card is slowing down to stay within its limits. Check cooling and power settings. |
| **VRAM** close to the card's total | New allocations may fail. Check **failed allocations** on the card. |
| A busy card, but no container uses it | The work runs outside your monitored containers, or isn't CUDA. |

## Next steps

- [Processes, events, and output](processes-events-and-output.md)
- [GPU data is missing](../troubleshooting/troubleshoot.md#gpu-data-is-missing)
- [GPU in the metrics glossary](../reference/metrics.md#gpu)
