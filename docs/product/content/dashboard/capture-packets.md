---
title: Capture packets
description: Record a container's network traffic for a few seconds, and read it in the dashboard or in Wireshark.
---

When the Network page isn't enough, you can record a container's traffic for a few seconds and read the packets
yourself, in the dashboard or in a tool such as Wireshark.

> [!WARNING]
> A capture keeps the first 128 bytes of every packet. For small packets, that includes their content: unencrypted
> requests, database queries, and possibly passwords. Capture only traffic you're allowed to inspect, and handle the
> files as sensitive. See [secure Sounding](../operate/secure-sounding.md).

## Record a capture

1. Open the container, then **Network**, and scroll to **Packet capture**.
2. Select **Capture 10 s** or **Capture 30 s**.
3. Wait while the row shows **waiting for the agent…** and then **capturing…**.

Only one capture can run per container at a time. Each capture keeps at most 200,000 packets, so a busy container's file
may stop collecting packets before the requested duration ends.

## Read it

When the capture is done, select **Open** to read its packets in the dashboard, or **Download .pcap** to open the file
in Wireshark or another packet analyser.

In the dashboard, select a packet to see its details and its first bytes. In this capture, the selected packet is an
HTTP request, readable as text: this is why captures need care.

![A capture opened in the dashboard, with an HTTP request selected](../../assets/packet-capture.png)

Captures follow the database's [history retention and cleanup](../concepts/samples-and-history.md#history), with a
24-hour target by default. A downloaded file is yours to protect and delete.

## If a capture fails

- **The buttons are unavailable, and the panel says Not available:** Sounding can't read this container's traffic. See
  [allow packet inspection and process details](../operate/allow-packet-inspection-and-process-details.md).
- **The capture shows failed:** the container stopped, or didn't start the capture within 2 minutes. Check that it's
  running, and select **Capture** again.

## Next steps

- [Monitor NVIDIA GPUs](monitor-gpus.md)
- [Secure Sounding](../operate/secure-sounding.md)
