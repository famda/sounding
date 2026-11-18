---
title: Set up a local model
description: Run Ollama and Open WebUI alongside Sounding, then ask a local model to investigate your containers.
---

Ask about your containers without sending their monitoring data to a hosted model. This guide runs a model
served by [Ollama](https://ollama.com) and the [Open WebUI](https://openwebui.com) chat app in Docker next to Sounding.
Image and model downloads still need internet access.

By the end, you'll have a chat at `http://localhost:3000` that can use Sounding's read-only tools. Already have an
MCP-capable assistant? [Connect it directly](connect-an-ai-assistant.md) instead.

**You need:**

- Sounding's dashboard running in Docker Compose 2.30 or later.
- About 15 GB of free disk space.
- An NVIDIA graphics card and the
  [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html).
  How much memory the card needs depends on the model you choose. Without a card, remove `gpus: all` below: answers
  then take many minutes.

Open WebUI is a separate project, used here unmodified under [its license](https://docs.openwebui.com/license). See
[openwebui.com](https://openwebui.com).

## 1. Add the chat app

Add these services and volumes to the `compose.yaml` that runs the dashboard:

```yaml title="compose.yaml"
services:
  # ...your existing services, including dashboard...

  chat-model:
    image: ollama/ollama:0.40.16
    environment:
      OLLAMA_CONTEXT_LENGTH: "65536"
    volumes:
      - chat-model:/root/.ollama
    gpus: all

  chat:
    image: ghcr.io/open-webui/open-webui:v0.13.3
    ports: ["127.0.0.1:3000:8080"]
    environment:
      OLLAMA_BASE_URL: http://chat-model:11434
      WEBUI_AUTH: "False"
      TOOL_SERVER_CONNECTIONS: >-
        [{"type": "mcp", "url": "http://dashboard:8080/mcp", "auth_type": "none",
          "config": {"enable": true}, "info": {"id": "sounding", "name": "Sounding"}}]
      DEFAULT_MODEL_METADATA: '{"toolIds": ["server:mcp:sounding"]}'
    volumes:
      - chat:/app/backend/data

volumes:
  # ...your existing volumes...
  chat-model:
  chat:
```

The chat app connects to Sounding with its tools switched on. The URL uses the dashboard's service name, so both
must be in the same Compose project.

> [!WARNING]
> This example turns off Open WebUI's sign-in and binds the chat to localhost. Keep it private, just like
> [the dashboard](../operate/secure-sounding.md).

## 2. Start it and download a model

Start the chat app:

```bash
docker compose up -d
```

Download a model that supports tools, such as Qwen 3.5 with 9 billion parameters:

```bash
docker compose exec chat-model ollama pull qwen3.5:9b
```

> [!NOTE]
> `qwen3.5:9b` is about 7 GB and runs well on a card with 12 GB of memory. Bigger models investigate more reliably
> but need more memory.
>
> To try another model, pick one from [Ollama](https://ollama.com)'s
> [models that support tools](https://ollama.com/search?c=tools) and pull it the same way, with its name in place of
> `qwen3.5:9b`.

## 3. Ask a question

Open <http://localhost:3000> and select the model you downloaded. Pick a container from your Sounding dashboard
and use its name in your question. For example:

> Use Sounding to check the memory limit for web.

Open the same container’s **Memory** page and compare the configured limit with the assistant’s answer.

The chat shows each tool the assistant uses to answer your question. Open a tool result to see the data behind
the answer. If the assistant answers without calling a tool, it has not checked your containers; see the table
below.

For a follow-up, ask about activity over time: "Why is worker restarting?" or "What changed for web in the last
30 minutes?" Use the names from your own dashboard.

Open WebUI doesn't show Sounding's [prompts](what-sounding-provides.md#prompts): ask in your own words.

For a repeatable problem to investigate, continue with the [memory leak tutorial](tutorial-find-a-memory-leak.md).

## If something doesn't work

| Problem | What to do |
|---|---|
| The assistant answers without using tools. | Check that **Sounding** is on under **+** > **Tools** in the chat. |
| "Failed to connect to MCP server 'sounding'". | Check that the URL ends in `/mcp`, and that the dashboard runs in the same Compose project. |
| The assistant gives up or repeats itself. | Ask a narrower question, such as "Check the web container", or try a bigger model. |
| Answers take many minutes. | Run `docker compose exec chat-model ollama ps`: `PROCESSOR` should say `100% GPU`. If not, check the NVIDIA Container Toolkit or choose a smaller model. |
| The assistant says a container doesn't exist. | Use the name you see on the dashboard. |

Open WebUI keeps its settings after the first start. To change the connection later, use your profile picture >
**Settings** > **Admin** > **Integrations** > **External Tool Servers**.

## Next steps

- [Tutorial: find a memory leak](tutorial-find-a-memory-leak.md)
- [What Sounding provides](what-sounding-provides.md)
