---
title: Try the Docker demo
description: Watch a client call a web server, follow the traffic in Sounding, then stop it and see what changes.
---

See real traffic before touching your own application. This disposable demo runs a web server and a client that
calls it every two seconds, with Sounding watching both.

You'll find the containers, follow a request from client to server, then stop the client and watch the traffic
change. The example includes its own dashboard and PostgreSQL database; no application code or GPU is needed.

Ready to monitor your own application instead? Go straight to
[add Sounding to your application](../deployment/docker-compose.md).

**You need:** Docker Compose 2.17 or later and x86-64 Linux containers, on Linux or Docker Desktop.

> [!NOTE]
> Already using port 8080? Change the dashboard's port mapping below to `127.0.0.1:8081:8080` and open
> `http://localhost:8081` instead. You don't need to stop your existing dashboard.

## 1. Create the demo

In a new, empty folder, create a `.env` file with a password of your choice using letters and numbers. Keep the file
out of version control.

```dotenv title=".env"
SOUNDING_DB_PASSWORD=replacewithastrongpassword
```

Save this as `compose.yaml` beside the `.env` file:

```yaml title="compose.yaml"
name: sounding-demo

x-sounding: &sounding
  SOUNDING_CONNECTION_STRING: "Host=db;Database=sounding;Username=sounding;Password=${SOUNDING_DB_PASSWORD}"

services:
  db:
    image: postgres:18-alpine
    environment:
      POSTGRES_DB: sounding
      POSTGRES_USER: sounding
      POSTGRES_PASSWORD: ${SOUNDING_DB_PASSWORD}
    volumes:
      - db:/var/lib/postgresql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U sounding -d sounding"]
      interval: 2s
      timeout: 3s
      retries: 30

  dashboard:
    image: famda/sounding:dashboard-latest
    ports: ["127.0.0.1:8080:8080"]
    environment:
      <<: *sounding
    depends_on:
      db: { condition: service_healthy }

  web:
    build:
      dockerfile_inline: |
        FROM nginx:1.29
        COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
        ENTRYPOINT ["/opt/sounding/run", "/docker-entrypoint.sh"]
        CMD ["nginx", "-g", "daemon off;"]
    environment:
      <<: *sounding
      SOUNDING_HOST_DISPLAY_NAME: demo-host

  client:
    build:
      dockerfile_inline: |
        FROM busybox:1.37
        COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
        ENTRYPOINT ["/opt/sounding/run"]
        CMD ["sh", "-c", "while true; do wget -q -O /dev/null http://web/; sleep 2; done"]
    environment:
      <<: *sounding
      SOUNDING_HOST_DISPLAY_NAME: demo-host

volumes:
  db:
```

## 2. Start and explore

From the same folder, build and start the demo:

```sh
docker compose up -d --build
```

Open <http://localhost:8080>. Once the agents connect, the dashboard shows **demo-host** and the **web** and
**client** containers. The client requests the web server every two seconds.

### Follow your first request

1. Select the **client** container and open **Network**. In **Connections** and **Requests**, look for calls to
   `web`.
2. Open **web** and its **Network** page. **Listening ports** shows the web server accepting connections on port
   **80**.
3. Choose **1m** in the header to focus on the traffic you just generated. Hover over a chart to read its figures.

![The demo client's Network page, with Connections and Requests showing HTTP GET / calls to web on port 80.](../../assets/standalone-demo-request.png)

In the client's **Requests** table, `GET /` goes to `web` on port **80**. A request every two seconds appears as
about **0.5/s**.

Your container names may include the Compose project name, such as `sounding-demo-client-1`. The charts only have
history from when you started the demo; an empty earlier period is expected.

### Watch the packets move

Open **demo-host**, choose **Network**, and scroll down to **Map**. Select **Use the whole page** to see how the
containers connect. The moving dots follow the traffic from `client` to `web`:

![Animated network map of demo-host, with packets moving from the client container to the web container.](../../assets/standalone-demo-map.gif)

The dots show the direction of the traffic Sounding observed, not a packet-by-packet replay. You can follow the
same paths in a larger application to see which containers talk to each other.

### Watch the traffic stop

From the demo folder, stop only the request generator:

```sh
docker compose stop client
```

Keep **web** open in the dashboard. Once the last client request leaves the one-minute window, **Requests** stops showing calls
from the client. Earlier traffic remains in the chart.

Start the client again and watch calls resume:

```sh
docker compose start client
```

[Find your way around the dashboard](../dashboard/find-your-way-around.md) shows how to follow the same clues in
your own application.

If neither container appears, see
[My container doesn't appear](../troubleshooting/troubleshoot.md#my-container-doesnt-appear).

> [!WARNING]
> The dashboard has no built-in authentication. This example binds it to localhost; keep it private.

## 3. Remove the demo

From the demo folder, stop the demo and remove its database volume:

```sh
docker compose down --volumes
```

This permanently deletes the demo's stored metrics. It does not remove the images.
To keep the database for another run, leave out `--volumes`.

## Next steps

- [Add Sounding to your application](../deployment/docker-compose.md)
- [Find your way around the dashboard](../dashboard/find-your-way-around.md)
- [Find a memory leak with an AI assistant](../ai-assistants/tutorial-find-a-memory-leak.md)
