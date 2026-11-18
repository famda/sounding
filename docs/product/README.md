# Product Documentation

The product documentation is published at **<https://famda.github.io/sounding/>**. This folder holds its sources.

| Folder | Contents |
| --- | --- |
| [content](content/index.md) | The Markdown pages, one per site page. The folder path is the page URL. |
| [assets](assets/) | Images the pages and the README show. Screenshots are generated and not committed, except `overview.gif`. |
| [screenshots](screenshots/) | Playwright scenarios that capture the screenshots against a seeded demo database. |
| [site](site/README.md) | The Next.js static site that renders the pages, and how it is built and published. |

## Sections

- **Get started:** [What is Sounding?](content/index.md), [Getting started](content/get-started/getting-started.md)
  (with the requirements), [Monitor containers and hosts](content/get-started/monitor-containers-and-hosts.md),
  [Try the Docker demo](content/get-started/docker-demo.md),
  [Try the Kubernetes demo](content/get-started/kubernetes-demo.md).
- **Dashboard:** [Find your way around](content/dashboard/find-your-way-around.md), then the CPU, memory, disk, network,
  GPU, processes and packet capture guides in [content/dashboard](content/dashboard/).
- **AI assistants:** [Connect an AI assistant](content/ai-assistants/connect-an-ai-assistant.md),
  [What Sounding provides](content/ai-assistants/what-sounding-provides.md),
  [Tutorial: find a memory leak](content/ai-assistants/tutorial-find-a-memory-leak.md),
  [Set up a local model](content/ai-assistants/set-up-a-local-model.md).
- **Deployment:** [Docker Compose](content/deployment/docker-compose.md),
  [Kubernetes](content/deployment/kubernetes.md).
- **Operate:** [Set up the database](content/operate/set-up-the-database.md),
  [Secure Sounding](content/operate/secure-sounding.md), [Upgrade Sounding](content/operate/upgrade-sounding.md),
  [Remove Sounding](content/operate/remove-sounding.md), and the host and permission guides in
  [content/operate](content/operate/).
- **Concepts:** [How Sounding works](content/concepts/how-sounding-works.md),
  [Sounding on Kubernetes](content/concepts/kubernetes.md), and the other pages in
  [content/concepts](content/concepts/).
- **Reference:** [Configuration](content/reference/configuration.md), [REST API](content/reference/api.md), [Metrics](content/reference/metrics.md),
  [Events](content/reference/events.md).
- **Troubleshooting:** [Troubleshoot](content/troubleshooting/troubleshoot.md), [FAQ](content/troubleshooting/faq.md).

## Images

The agent and dashboard are published to [`famda/sounding`](https://hub.docker.com/r/famda/sounding) on Docker Hub as
`agent-latest` and `dashboard-latest`, plus `agent-<commit>` and `dashboard-<commit>` for each build. See
[Upgrade Sounding](content/operate/upgrade-sounding.md#choose-an-image-tag).
