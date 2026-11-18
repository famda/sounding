---
title: Set up Sounding on Kubernetes
description: Deploy Sounding, add the agent to your application, and find your containers in the dashboard.
---

Deploy Sounding, add the agent to your application, and find your containers in the dashboard. At the end, a workload
of yours reports its CPU, memory, disk, network, and processes in the dashboard.

You'll deploy three parts:

- **Sounding itself:** the dashboard and, unless you bring your own, PostgreSQL, in a `sounding` namespace (step 1).
- **The agent:** inside each container you monitor, built into its image or copied in when the pod starts (steps 2
  and 3).
- **The node agent, if you want it:** one pod per node, for the node's network, disks, and GPUs (step 5).

To see it working first, [try the Kubernetes demo](../get-started/kubernetes-demo.md). For how Sounding names your
nodes and pods, see [Sounding on Kubernetes](../concepts/kubernetes.md).

**You need:**

- A cluster with **x86-64** or **ARM64** Linux nodes using **cgroup v2**, and `kubectl` connected to it.
- PostgreSQL 14 or later: your own, or the one the sample runs, which needs a default StorageClass (step 1).
- A shell: **bash** with `openssl`, or **PowerShell** (Windows PowerShell 5.1 or PowerShell 7). Where the commands
  differ, choose yours above the commands.

The manifests are in the sample. Download and unzip it, and run the commands on this page from its folder:

{% download href="downloads/sounding-kubernetes.zip" title="Download Kubernetes sample (.zip)" description="Includes sounding.yaml, node-agent.yaml, and the demo's demo.yaml." /%}

## 1. Deploy Sounding

`sounding.yaml` creates the `sounding` namespace with the dashboard and PostgreSQL. The dashboard and every agent read
the database's connection string from a secret named `sounding-agent`, which you create first.

The manifest already follows common security and availability practices: non-root pods, two dashboard replicas,
health checks, and NetworkPolicies. [Before production](#before-production) lists what it sets up, and the few
decisions left to you.

### With the bundled PostgreSQL

The sample runs one PostgreSQL instance in a StatefulSet with a 5 GiB volume, without backups: use it to evaluate
Sounding, and your own PostgreSQL in production. Your cluster needs a default StorageClass to provide the volume:
`kubectl get storageclass` lists one marked `(default)`.

Create the namespace, a database password, and the secret:

{% shell %}

```sh
password=$(openssl rand -hex 16)
connection="Host=db.sounding.svc.cluster.local;Database=sounding;Username=sounding;Password=$password"
kubectl create namespace sounding --save-config
kubectl -n sounding create secret generic sounding-db --from-literal=password=$password
kubectl -n sounding create secret generic sounding-agent --from-literal=SOUNDING_CONNECTION_STRING="$connection"
```

```powershell
$bytes = New-Object byte[] 16
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = -join ($bytes | ForEach-Object { $_.ToString('x2') })
$connection = "Host=db.sounding.svc.cluster.local;Database=sounding;Username=sounding;Password=$password"
kubectl create namespace sounding --save-config
kubectl -n sounding create secret generic sounding-db --from-literal=password=$password
kubectl -n sounding create secret generic sounding-agent --from-literal=SOUNDING_CONNECTION_STRING="$connection"
```

{% /shell %}

### Or with your own PostgreSQL

Prepare the database as in [set up the database](../operate/set-up-the-database.md). Then:

1. Delete the three objects marked **Bundled PostgreSQL** from `sounding.yaml`: the `db` Service, StatefulSet, and
   NetworkPolicy.
2. Create the namespace and the secret, with your server's address, user, and password:

   {% shell %}

   ```sh
   connection="Host=postgres.example.com;Database=sounding;Username=sounding;Password=your-password;SSL Mode=VerifyFull"
   kubectl create namespace sounding --save-config
   kubectl -n sounding create secret generic sounding-agent --from-literal=SOUNDING_CONNECTION_STRING="$connection"
   ```

   ```powershell
   $connection = "Host=postgres.example.com;Database=sounding;Username=sounding;Password=your-password;SSL Mode=VerifyFull"
   kubectl create namespace sounding --save-config
   kubectl -n sounding create secret generic sounding-agent --from-literal=SOUNDING_CONNECTION_STRING="$connection"
   ```

   {% /shell %}

   See [protect the connection](../operate/set-up-the-database.md#protect-the-connection) for the `SSL Mode`.

### Deploy

```shell
kubectl apply -f sounding.yaml
kubectl -n sounding rollout status deployment/dashboard
kubectl -n sounding get pods
```

Both `dashboard` pods and, with the bundled database, `db-0` show **Running**. If `db-0` stays **Pending**,
`kubectl -n sounding get pvc` shows its volume **Pending** too: the cluster has no default StorageClass. Set one, or
use your own PostgreSQL.

## 2. Add the agent to your workload

Build the agent into an image you maintain. For an existing image, the init container below can supply the agent
at startup if your cluster permits it. Under the restricted Pod Security Standard, use the image-build option.

### Build it into the image

Add the agent to your `Dockerfile` and start your application through it, as for
[Docker Compose](docker-compose.md#1-add-the-agent-to-your-image):

```dockerfile title="Dockerfile"
COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
ENTRYPOINT ["/opt/sounding/run", "node", "server.js"]
```

Build the image and push it to a registry your cluster pulls from:

```shell
docker build -t registry.example.com/my-app/web:1.5 .
docker push registry.example.com/my-app/web:1.5
```

### Or copy it in when the pod starts

For an image you don't build, an init container copies the agent into a shared volume. Start the container through
`/opt/sounding/run`, followed by the image's own entrypoint. Merge these lines into your Deployment's pod template;
this is not a complete manifest:

```yaml title="deployment.yaml (excerpt)"
spec:
  template:
    spec:
      initContainers:
        - name: sounding-agent
          image: famda/sounding:agent-latest
          command: [cp, -a, /opt/sounding/., /sounding/]
          securityContext: { runAsUser: 0 }
          volumeMounts: [{ name: sounding, mountPath: /sounding }]
      containers:
        - name: cache
          image: redis:8.2
          command: [/opt/sounding/run, docker-entrypoint.sh]
          args: [redis-server]
          volumeMounts: [{ name: sounding, mountPath: /opt/sounding }]
      volumes:
        - { name: sounding, emptyDir: {} }
```

To find an image's entrypoint, run `docker inspect --format '{{json .Config.Entrypoint}}' redis:8.2`. The init
container runs as root so that the copy keeps a permission Sounding's network reader needs; see
[Pod Security Standards](#pod-security-standards).

## 3. Configure the agents and deploy

The commands below use the namespace `my-app` and a container named `web`; use yours.

Each monitored container needs these settings:

| Setting | Value |
|---|---|
| `SOUNDING_CONNECTION_STRING` | From the `sounding-agent` secret, in the workload's namespace. |
| `SOUNDING_HOST_ID` | The node's name, from `spec.nodeName`. |
| `SOUNDING_POD_NAMESPACE`, `SOUNDING_POD_NAME`, `SOUNDING_POD_UID` | The pod's namespace, name, and UID, from `metadata.namespace`, `metadata.name`, and `metadata.uid`. |
| `SOUNDING_POD_CONTAINER` | The container's name, as in the pod spec. |
| `SOUNDING_CLUSTER_NETWORKS` | Your cluster's pod and Service ranges. |
| `SOUNDING_SAMPLE_HOST` | `false` if you add the node agent (step 5), which then reports the node. |

### Copy the secret

Copy the `sounding-agent` secret into the workload's namespace. With the bundled PostgreSQL, also label the namespace
so its pods may connect to the database:

{% shell %}

```sh
encoded=$(kubectl -n sounding get secret sounding-agent -o jsonpath='{.data.SOUNDING_CONNECTION_STRING}')
connection=$(echo "$encoded" | base64 -d)
kubectl -n my-app create secret generic sounding-agent --from-literal=SOUNDING_CONNECTION_STRING="$connection"
kubectl label namespace my-app sounding-agents=true
```

```powershell
$encoded = kubectl -n sounding get secret sounding-agent -o jsonpath='{.data.SOUNDING_CONNECTION_STRING}'
$connection = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded))
kubectl -n my-app create secret generic sounding-agent --from-literal=SOUNDING_CONNECTION_STRING="$connection"
kubectl label namespace my-app sounding-agents=true
```

{% /shell %}

### Set the cluster's ranges

Find your cluster's pod and Service ranges. On clusters set up with kubeadm, including Docker Desktop's:

{% shell %}

```sh
kubectl cluster-info dump | grep -m 2 -oE -- '--(cluster-cidr|service-cluster-ip-range)=[^"]*'
```

```powershell
kubectl cluster-info dump | Select-String -Pattern '--(cluster-cidr|service-cluster-ip-range)=[^"]*' | Select-Object -First 2 | ForEach-Object { $_.Matches.Value }
```

{% /shell %}

On a managed cluster, your provider's console shows them. Put them in a ConfigMap for the namespace's agents, and
apply it:

```yaml title="sounding-agent.yaml"
apiVersion: v1
kind: ConfigMap
metadata: { name: sounding-agent, namespace: my-app }
data:
  SOUNDING_CLUSTER_NETWORKS: "10.244.0.0/16,10.96.0.0/16"
```

```shell
kubectl apply -f sounding-agent.yaml
```

### Add the settings and deploy

Merge these lines into the container in your Deployment. `SOUNDING_POD_CONTAINER` repeats the container's `name`:

```yaml title="deployment.yaml (excerpt)"
      containers:
        - name: web
          image: registry.example.com/my-app/web:1.5
          envFrom:
            - secretRef: { name: sounding-agent }
            - configMapRef: { name: sounding-agent }
          env:
            - { name: SOUNDING_HOST_ID, valueFrom: { fieldRef: { fieldPath: spec.nodeName } } }
            - { name: SOUNDING_POD_NAMESPACE, valueFrom: { fieldRef: { fieldPath: metadata.namespace } } }
            - { name: SOUNDING_POD_NAME, valueFrom: { fieldRef: { fieldPath: metadata.name } } }
            - { name: SOUNDING_POD_UID, valueFrom: { fieldRef: { fieldPath: metadata.uid } } }
            - { name: SOUNDING_POD_CONTAINER, value: web }
```

Deploy it and wait until it's running:

```shell
kubectl apply -f deployment.yaml
kubectl -n my-app rollout status deployment/web
```

If two containers of the same pod have the agent, set `SOUNDING_SAMPLE_NETWORK=false` on all of them but one: they
share the pod's network, which should be counted once. The one left on shows the whole pod's traffic. It can name the
other containers' processes only if the pod shares its process namespace (`shareProcessNamespace: true`).

## 4. Open the dashboard and check

The dashboard has no sign-in. Open it from your computer with:

```shell
kubectl -n sounding port-forward svc/dashboard 8080:8080
```

Keep it running and go to <http://localhost:8080>. Within a few seconds:

- **Hosts** lists your nodes by name.
- Press **/** to list the containers: `my-app/web` appears, once per replica.
- Its **Events** show **Started**, and no `identity` warning. A warning there names the settings from step 3 that are
  missing.

`sounding.yaml` includes a NetworkPolicy meant to keep other pods from calling the dashboard. A cluster accepts the
policy whether or not it enforces it: that takes a network plugin that supports NetworkPolicies. Check yours from
another namespace:

{% shell %}

```sh
kubectl -n my-app run policy-check --rm -i --restart=Never --image=busybox:1.37 -- \
  sh -c 'wget -q -T 5 -O /dev/null http://dashboard.sounding:8080/ && echo reachable || echo blocked'
```

```powershell
kubectl -n my-app run policy-check --rm -i --restart=Never --image=busybox:1.37 -- `
  sh -c 'wget -q -T 5 -O /dev/null http://dashboard.sounding:8080/ && echo reachable || echo blocked'
```

{% /shell %}

**blocked** means the policy is enforced for pods. Traffic from the nodes themselves can still reach the dashboard. If
it prints **reachable**, anyone who can run a pod can read the dashboard: put it behind authentication instead. See
[security](#security).

## 5. Add the node agent (optional)

Without it, a host's network and GPUs show **Not available**, and its disks are limited to the one your containers run
from. The node agent reads the node's `/proc`, `/sys`, and disk, so it runs in a namespace of its own,
`sounding-node`, that allows that. See [the parts](../concepts/kubernetes.md#the-parts).

1. Copy the `sounding-agent` secret into `sounding-node`, as in [step 3](#copy-the-secret) with `-n sounding-node`, then deploy:

   ```shell
   kubectl apply -f node-agent.yaml
   ```

2. Add `SOUNDING_SAMPLE_HOST: "false"` to the `sounding-agent` ConfigMap of each workload namespace, apply it, and
   restart the workloads: `kubectl -n my-app rollout restart deployment/web`.

To show the nodes' NVIDIA GPUs, see [monitor GPUs on Kubernetes](../dashboard/monitor-gpus.md#on-kubernetes).

## Security

> [!NOTE]
> The guidance below is a starting point for a typical cluster, not a rule set. Your organization's security policies
> and your own assessment of the risks come first: adapt it to fit them.

Two things need protecting. Anyone who can reach the dashboard can read your monitoring data and start packet
captures, and anyone with the connection string can write to Sounding's database.

### Dashboard access

The dashboard has no sign-in. Its [REST API](../reference/api.md) and the
[MCP server](../ai-assistants/connect-an-ai-assistant.md) use the same port, so whatever protects the dashboard
protects them too. Choose how people reach it:

- **Administrators only:** keep the NetworkPolicy from `sounding.yaml` and open the dashboard with
  `kubectl port-forward`. Only people allowed to create `pods/portforward` in `sounding` can then reach it, so grant
  that permission sparingly.
- **A team:** publish it through an ingress with TLS and an authenticating proxy.
  [OAuth2 Proxy](https://oauth2-proxy.github.io/oauth2-proxy/) is a good fit: it signs people in with your identity
  provider, such as Microsoft Entra ID, Google, GitHub, or Keycloak, before any request reaches the dashboard. AI
  assistants then connect through the proxy too.
- **Not directly:** never give the dashboard Service a public `LoadBalancer` or `NodePort` without authentication in
  front of it.

The NetworkPolicy in `sounding.yaml` blocks every pod, the proxy included. When you publish the dashboard, let only the
proxy through. For a proxy in the `ingress-nginx` namespace, add this to `sounding.yaml`:

```yaml title="sounding.yaml (addition)"
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata: { name: dashboard-from-proxy, namespace: sounding }
spec:
  podSelector: { matchLabels: { app: dashboard } }
  policyTypes: [Ingress]
  ingress:
    - from: [{ namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: ingress-nginx } } }]
      ports: [{ port: 8080 }]
```

### Database and connection string

- **Who can connect:** with the bundled PostgreSQL, its NetworkPolicy admits only the dashboard and pods in namespaces
  labelled `sounding-agents=true`. With your own PostgreSQL, allow only your cluster's addresses in its firewall, and
  require TLS with `SSL Mode=VerifyFull`.
- **Who can read the secret:** the `sounding-agent` secret holds the full connection string, password included, in
  every namespace you copy it to. Limit who can read secrets in those namespaces.
- **How it's managed:** in production, keep the connection string in your secret store and sync it into each namespace,
  for example with [External Secrets Operator](https://external-secrets.io), instead of creating the secrets by hand.

### Pod Security Standards

| Namespace standard | What Sounding shows |
|---|---|
| **restricted** | Everything except packet details: no requests, DNS lookups, or packet captures. Connections and traffic totals remain. Build the agent into the image: the init container above runs as root, which restricted doesn't allow. |
| **baseline** | Everything, including packet details, which need the `NET_RAW` capability that baseline allows. |
| **privileged** | Everything. Only the node agent needs it. |

The `sounding` namespace itself enforces restricted. Keep restricted for your workloads where its limits are
acceptable. See [allow packet inspection](../operate/allow-packet-inspection-and-process-details.md#network-traffic).

The node agent runs as root with read-only access to each node's `/proc`, `/sys`, and disk, in the privileged
`sounding-node` namespace. Allow only cluster administrators to create pods there.

For what Sounding collects and why it can be sensitive, see [secure Sounding](../operate/secure-sounding.md).

## Before production

The sample runs as it is, and already covers the groundwork. A few decisions are yours before you rely on it.

### Already in the sample

`sounding.yaml` takes care of the groundwork:

- Every pod runs as a non-root user, with a read-only root filesystem, no added capabilities, and no Kubernetes API
  token. The `sounding` namespace enforces this with the restricted Pod Security Standard.
- Two dashboard replicas, spread across nodes, keep it answering while a node is drained or the dashboard is updated.
- Health checks send traffic only to a dashboard that is reading the database, and restart one that stops answering.
- Requests and limits suit a small cluster. There are no CPU limits, so a busy moment slows down rather than being
  throttled.
- NetworkPolicies keep every pod away from the dashboard, and all but the dashboard and labelled namespaces away from
  the bundled database.

### Decide before you rely on it

- **Database:** use a managed or replicated PostgreSQL that you back up, instead of the bundled single instance, and
  size its storage for your fleet. See [limits and capacity](../concepts/limits-and-capacity.md).
- **Image versions:** pin a version, such as `dashboard-1.2.0` and `agent-1.2.0`, instead of the `-latest` tags, and set
  `imagePullPolicy: IfNotPresent`, so you choose when to upgrade. See
  [choose an image tag](../operate/upgrade-sounding.md#choose-an-image-tag).
- **Access:** choose how people reach the dashboard, as in [dashboard access](#dashboard-access), and run the
  [NetworkPolicy check](#4-open-the-dashboard-and-check) to confirm your cluster enforces the policies.
- **Secrets:** keep the connection string in your secret store, as in
  [database and connection string](#database-and-connection-string).
- **Resources:** the dashboard and database work harder with more containers, a longer history, and more people
  watching. Check their usage with `kubectl top pods -n sounding` and raise the requests and limits as you grow.

## If something doesn't work

Start with the agent's own messages. They say whether it started, and why it can't reach the database:

```shell
kubectl -n my-app logs <pod> -c web
```

For more detail, set `SOUNDING_LOGGING__LOGLEVEL__DEFAULT=Information` on the container and restart the workload with
`kubectl -n my-app rollout restart deployment/web`. Then check the usual causes:

- **`CreateContainerConfigError` on a pod**: its namespace is missing the `sounding-agent` secret or ConfigMap.
  `kubectl -n my-app describe pod <pod>` names which one.
- **Your container doesn't appear**: with the bundled PostgreSQL, its namespace needs the `sounding-agents=true`
  label, as in [step 3](#copy-the-secret).
- **An `identity` warning in the container's events**: add the missing settings from step 3. Until then, a restart
  can start a new history.
- **Packet capture off on the Network page**: the namespace's Pod Security Standard doesn't allow packet details. See
  [Pod Security Standards](#pod-security-standards).
- **`db-0` stays Pending**: the cluster has no default StorageClass. Set one, or use your own PostgreSQL.
- **A dashboard pod isn't Ready**: it can't read the database. `kubectl -n sounding logs deployment/dashboard` says
  why.

## Next steps

- [Sounding on Kubernetes](../concepts/kubernetes.md): names, history, and what Sounding can't see
- [Upgrade Sounding](../operate/upgrade-sounding.md#on-kubernetes) and [remove Sounding](../operate/remove-sounding.md#on-kubernetes)
- [Configuration settings](../reference/configuration.md)
