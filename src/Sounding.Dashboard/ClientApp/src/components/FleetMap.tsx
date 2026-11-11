import dagre from '@dagrejs/dagre';
import { BaseEdge, ControlButton, Controls, type Edge, EdgeLabelRenderer, type EdgeProps, Handle, type Node, type NodeProps, Position, ReactFlow, type ReactFlowInstance, ViewportPortal } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { Fragment, type ReactNode, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { type Resolved, type Scope, type View, usePolled } from '../data';
import { fmtRate } from '../format';
import { HELP } from '../help';
import { nameOf } from '../models';
import { T } from '../theme';
import type { ContainerNetwork, ContainerState, HostSummary } from '../types';
import { IconNetwork } from './Icons';
import { Panel } from './Table';
import { StatusDot } from './ui';

// Every card the same size. A side of a card spreads its lines up to PORT apart.
const NODE_W = 172, NODE_H = 44, PORT = 12, HOST_GAP = 48, EPHEMERAL = 32768;
// The map is never fitted smaller than this, so the cards stay readable; past it, it pans.
const READABLE = 0.8, MAX_ZOOM = 1.25;
// Cards that talk to no one, in rows of this many inside their network.
const QUIET_ROW = 4;

type Kind = 'container' | 'host' | 'internet' | 'private' | 'address';
type CardData = { kind: Kind; title: string; detail: string; status?: ContainerState['status']; focused?: boolean; lit?: boolean; dim?: boolean; containerId?: string };
type BoxData = { label: string; detail?: string; host: boolean; online?: boolean };
type TrafficData = { rate: number; ports: number[]; path: string; mid: Pt; from: string; to: string; selected: boolean; hovered: boolean; dim: boolean };
type Pt = [number, number];

const glyphs: Record<Kind, ReactNode> = {
  container: <path d="M4 7.5 12 3l8 4.5v9L12 21l-8-4.5v-9ZM4 7.5 12 12l8-4.5M12 12v9" />,
  host: <><rect x="3" y="4" width="18" height="12" rx="2" /><path d="M8 20h8M12 16v4" /></>,
  internet: <><circle cx="12" cy="12" r="9" /><path d="M3 12h18M12 3c2.5 2.5 3.8 5.5 3.8 9s-1.3 6.5-3.8 9c-2.5-2.5-3.8-5.5-3.8-9S9.5 5.5 12 3Z" /></>,
  private: <><rect x="3" y="8" width="7" height="8" rx="1.5" /><rect x="14" y="8" width="7" height="8" rx="1.5" /><path d="M10 12h4" /></>,
  address: <><circle cx="12" cy="12" r="8" /><path d="M12 8v4l2.5 2.5" /></>,
};

// The internet and the host stand out from the containers in a colour of their own.
const accent: Partial<Record<Kind, string>> = { internet: 'var(--color-map-internet)', host: 'var(--color-map-host)' };

// Handles on both sides, for React Flow; the lines themselves are routed by build.
const handles = (
  <>
    <Handle id="l" type="source" position={Position.Left} className="!opacity-0" />
    <Handle id="r" type="source" position={Position.Right} className="!opacity-0" />
    <Handle id="tl" type="target" position={Position.Left} className="!opacity-0" />
    <Handle id="tr" type="target" position={Position.Right} className="!opacity-0" />
  </>
);

function CardNode({ data }: NodeProps<Node<CardData>>) {
  const color = accent[data.kind];
  const ring = data.focused || data.lit ? T.usage : color;
  return (
    <div
      className={`flex items-center gap-2 rounded-md border px-2.5 text-[11px] transition-opacity ${data.containerId ? 'cursor-pointer hover:border-text-2' : ''} ${data.kind === 'address' ? 'border-dashed' : ''}`}
      style={{
        width: NODE_W, height: NODE_H, opacity: data.dim ? 0.35 : 1,
        background: color ? `color-mix(in srgb, ${color} 10%, var(--color-surface))` : 'var(--color-surface)',
        borderColor: ring ?? 'var(--color-line-2)',
        boxShadow: data.focused || data.lit ? `0 0 0 3px color-mix(in srgb, ${T.usage} 18%, transparent)` : undefined,
      }}
    >
      {handles}
      <svg width={16} height={16} viewBox="0 0 24 24" fill="none" stroke={data.focused || data.lit ? T.usage : color ?? 'var(--color-text-2)'} strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round" className="shrink-0">
        {glyphs[data.kind]}
      </svg>
      <div className="min-w-0 leading-tight">
        <div className="flex items-center gap-1.5">
          <span className="truncate font-medium text-text">{data.title}</span>
          {data.status && data.status !== 'online' && <StatusDot status={data.status} />}
        </div>
        <div className="truncate font-mono text-[10px] text-muted">{data.detail}</div>
      </div>
    </div>
  );
}

// A host or one of its networks; the name sits on the border, so it takes no room from the cards.
function BoxNode({ data }: NodeProps<Node<BoxData>>) {
  return (
    <div
      className={`relative h-full w-full rounded-lg ${data.host ? 'bg-surface-2/30' : ''}`}
      style={{ border: data.host ? '1.5px solid var(--color-line-2)' : '1.5px dashed var(--color-muted)' }}
    >
      <div className="absolute -top-2.5 left-3 flex items-center gap-1.5 rounded bg-surface px-1.5 text-[10px] uppercase leading-4 tracking-wide text-text-2">
        {data.online != null && <StatusDot status={data.online} />}
        <span className="font-semibold">{data.label}</span>
        {data.detail && <span className="font-mono normal-case tracking-normal text-muted">{data.detail}</span>}
      </div>
    </div>
  );
}

interface Stop { x: number; y: number; angle: number; at: number }
interface Route { id: string; stops: Stop[]; shape: string; length: number; askerAtTarget: boolean; dim: boolean }

// A smooth line through points, level wherever it passes one: a cubic curve from each to the next that climbs or
// drops around its turn, half way along unless turns gives it a place of its own. The points are dagre's, so the
// line keeps to the room it left between the cards.
const handlesOf = (p: Pt, q: Pt, turn = (p[0] + q[0]) / 2): [Pt, Pt] => [[turn, p[1]], [turn, q[1]]];

const svgPath = (points: Pt[], at: (number | undefined)[]) =>
  points.slice(1).reduce((d, q, i) => {
    const [a, b] = handlesOf(points[i], q, at[i]);
    return `${d}C${a[0]},${a[1]} ${b[0]},${b[1]} ${q[0]},${q[1]}`;
  }, `M${points[0][0]},${points[0][1]}`);

interface Leg { id: string; sx: number; sy: number; tx: number; ty: number }

// Where each stretch between two points climbs or drops. Stretches in the same gap whose heights overlap each get a
// place of their own, so none runs along another, in the order that keeps them from crossing: going down, the lower
// start first; going up, the higher; then the longer first.
function turns(legs: Leg[]) {
  const out = new Map<string, number>();
  const gaps: Leg[][] = [];
  let edge = -Infinity;
  for (const leg of legs.filter(l => l.tx - l.sx > 4 && Math.abs(l.ty - l.sy) > 1).sort((a, b) => a.sx - b.sx)) {
    if (leg.sx >= edge) { gaps.push([]); edge = Infinity; }
    gaps[gaps.length - 1].push(leg);
    edge = Math.min(edge, leg.tx);
  }
  const span = (l: Leg) => [Math.min(l.sy, l.ty), Math.max(l.sy, l.ty)];
  for (const gap of gaps) {
    const order = gap.sort((a, b) => {
      const key = (l: Leg) => (l.ty > l.sy ? -l.sy : l.sy);
      return key(a) - key(b) || Math.abs(b.ty - b.sy) - Math.abs(a.ty - a.sy) || a.id.localeCompare(b.id);
    });
    const index = order.map(() => 0);
    order.forEach((leg, i) => {
      for (let j = 0; j < i; j++) {
        const [a, b] = [span(leg), span(order[j])];
        if (Math.max(a[0], b[0]) < Math.min(a[1], b[1]) + 2) index[i] = Math.max(index[i], index[j] + 1);
      }
    });
    const n = Math.max(...index) + 1;
    const left = Math.max(...order.map(l => l.sx)) + 8, right = Math.min(...order.map(l => l.tx)) - 8;
    order.forEach((leg, i) => out.set(leg.id, right > left ? left + (index[i] + 0.5) * (right - left) / n : (leg.sx + leg.tx) / 2));
  }
  return out;
}

// The packets' track along the same curves, as keyframes: where a packet is, which way it points, and when.
function track(path: Pt[], at: (number | undefined)[]) {
  const points: Pt[] = [path[0]];
  for (let i = 1; i < path.length; i++) {
    const [p, q] = [path[i - 1], path[i]];
    const [a, b] = handlesOf(p, q, at[i - 1]);
    for (let s = 1; s <= 10; s++) {
      const t = s / 10, u = 1 - t;
      points.push([u ** 3 * p[0] + 3 * u * u * t * a[0] + 3 * u * t * t * b[0] + t ** 3 * q[0], u ** 3 * p[1] + 3 * u * u * t * a[1] + 3 * u * t * t * b[1] + t ** 3 * q[1]]);
    }
  }
  const stops: Stop[] = [];
  let length = 0, angle = 0;
  for (let i = 1; i < points.length; i++) {
    const [p, q] = [points[i - 1], points[i]];
    const step = Math.hypot(q[0] - p[0], q[1] - p[1]);
    if (step < 0.01) continue;
    let a = Math.atan2(q[1] - p[1], q[0] - p[0]) * 180 / Math.PI;
    if (stops.length) a += Math.round((angle - a) / 360) * 360;
    angle = a;
    stops.push({ x: p[0], y: p[1], angle, at: length }, { x: q[0], y: q[1], angle, at: length + step });
    length += step;
  }
  for (const stop of stops) stop.at = length ? stop.at / length : 0;
  // Half way along, for the label.
  const half = stops.find(s => s.at >= 0.5) ?? stops[stops.length - 1] ?? { x: path[0][0], y: path[0][1] };
  return { stops, length, mid: [half.x, half.y] as Pt };
}

// A packet going round its track, moved by a transform alone, so the browser runs it off the page's main thread.
// Its place comes from the clock, not from when it was drawn: a refreshed map leaves every packet where it was.
function Packet({ route, seconds, phase, reverse, color }: { route: Route; seconds: number; phase: number; reverse: boolean; color: string }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const frames = route.stops.map(s => ({ offset: s.at, transform: `translate(${s.x - 4}px, ${s.y - 2}px) rotate(${s.angle}deg)` }));
    const animation = ref.current!.animate(frames, { duration: seconds * 1000, iterations: Infinity, direction: reverse ? 'reverse' : 'normal' });
    animation.startTime = -phase * seconds * 1000;
    return () => animation.cancel();
  }, [route.shape, seconds, phase, reverse]);
  return <div ref={ref} className="map-packet" style={{ background: color, opacity: route.dim ? 0.15 : 1 }} />;
}

// Packets along every link, from the side that opens the connection to the side it connects to. They live in a layer
// of their own: React Flow remounts edges as the data refreshes, which would restart anything drawn inside them.
function Packets({ routes }: { routes: Route[] }) {
  return (
    <ViewportPortal>
      {routes.map(route => {
        const seconds = Math.max(2.5, route.length / 60);
        return (
          <Fragment key={route.id}>
            {[0, 0.5].map(phase => <Packet key={phase} route={route} seconds={seconds} phase={phase} reverse={route.askerAtTarget} color={route.askerAtTarget ? T.second : T.usage} />)}
          </Fragment>
        );
      })}
    </ViewportPortal>
  );
}

// As wide as the traffic: from a hairline when idle to a few pixels at megabytes a second.
const strokeOf = (rate: number) => 1.25 + Math.min(2.25, Math.log10(1 + rate / 1000));

// A line between two cards; the packets on it are drawn by Packets. Its label shows while it is hovered or selected.
function TrafficEdge({ id, data }: EdgeProps<Edge<TrafficData>>) {
  const { rate, ports, path, mid, from, to, selected, hovered, dim } = data!;
  return (
    <>
      <BaseEdge
        id={id}
        path={path}
        interactionWidth={14}
        style={{
          stroke: selected ? T.usage : hovered ? 'var(--color-text-2)' : 'var(--color-muted)',
          strokeOpacity: dim ? 0.15 : selected || hovered ? 1 : 0.7,
          strokeWidth: strokeOf(rate) + (selected ? 1 : 0),
          cursor: 'pointer',
        }}
      />
      {(selected || hovered) && (
        <EdgeLabelRenderer>
          <div
            className="nodrag nopan pointer-events-none absolute whitespace-nowrap rounded-full border border-line bg-surface px-2 font-mono text-[10px] leading-5 text-text shadow-sm"
            style={{ transform: `translate(-50%, -100%) translate(${mid[0]}px, ${mid[1] - 6}px)` }}
          >
            {from} → {to}{ports.length ? ` :${ports.slice(0, 3).join(' :')}` : ''}{rate > 0 ? ` · ${fmtRate(rate, true)}` : ''}
          </div>
        </EdgeLabelRenderer>
      )}
    </>
  );
}

const nodeTypes = { card: CardNode, box: BoxNode };
const edgeTypes = { traffic: TrafficEdge };

interface Link { from: string; to: string; rate: number; bytes: number; ports: Set<number>; sure: boolean }
interface Peer { data: Omit<CardData, 'height'>; network?: string; hostId?: string }

// One talk between a container and someone, as either list reports it.
interface Seen { address: string; name: string | null; zone: string | null; internet: boolean; port: number; inbound: boolean; sure: boolean; rate: number; bytes: number }

// Who a container talked to in the period: its peers over the retention period, or, from an agent too old to keep
// them, the connections it still lists. A connection points from the side that asks to the side that answers: the
// side listening on the port, or else the side with the ephemeral port. Sounding's own traffic is left out.
function seen(x: ContainerNetwork, range: number, listening: Map<string, Set<number>>, owner: Map<string, string>): Seen[] {
  if (x.network.peers) {
    return x.network.peers.filter(p => p.lastSeenSecondsAgo <= range).map(p => ({
      address: p.address, name: p.name, zone: p.zone, internet: p.internet, port: p.port, inbound: p.inbound, sure: p.sure, rate: p.bytesPerSecond, bytes: p.bytes,
    }));
  }
  return x.network.connections.filter(c => c.process !== 'sounding-agent' && c.process !== 'net-meter' && c.lastSeenSecondsAgo <= range).map(c => {
    const other = owner.get(c.remoteAddress);
    const iServe = listening.get(x.containerId)?.has(c.localPort) ?? false;
    const theyServe = other ? listening.get(other)?.has(c.remotePort) ?? false : false;
    const inbound = iServe && !theyServe ? true : theyServe && !iServe ? false : c.localPort < EPHEMERAL && c.remotePort >= EPHEMERAL;
    return {
      address: c.remoteAddress, name: c.remoteName, zone: c.zone, internet: c.internet, port: inbound ? c.localPort : c.remotePort, inbound,
      sure: iServe || theyServe, rate: (c.outBytesPerSecond ?? 0) + (c.inBytesPerSecond ?? 0), bytes: (c.bytesOut ?? 0) + (c.bytesIn ?? 0),
    };
  });
}

// Who talks to whom: another container (by its addresses), an address on its network that runs no agent, its host
// (the network's gateway), or the LAN and the internet. Each pair is one link.
function links(network: ContainerNetwork[], range: number) {
  const owner = new Map<string, string>();
  for (const x of network) for (const address of x.network.addresses ?? []) owner.set(address, x.containerId);
  const listening = new Map(network.map(x => [x.containerId, new Set(x.network.listening.map(l => l.port))]));
  const pairs = new Map<string, Link[]>();
  const peers = new Map<string, Peer>();
  for (const x of network) {
    const mine = new Map<string, Link>();
    for (const c of seen(x, range, listening, owner)) {
      const other = c.internet ? undefined : owner.get(c.address);
      let peer: string;
      if (other) {
        if (other === x.containerId) continue;
        peer = other;
      } else if (c.zone === 'internet' || (!c.zone && c.internet)) {
        peer = 'peer:internet';
        peers.set(peer, { data: { kind: 'internet', title: 'Internet', detail: 'public addresses' } });
      } else if (c.zone === 'host') {
        peer = `peer:host:${x.hostId}`;
        peers.set(peer, { data: { kind: 'host', title: 'This host', detail: `gateway ${c.address}` }, hostId: x.hostId ?? undefined });
      } else if (c.zone === 'network') {
        peer = `peer:${x.hostId}:${c.address}`;
        const subnet = (x.network.networks ?? []).find(n => inSubnet(c.address, n));
        peers.set(peer, { data: { kind: 'address', title: c.name ?? c.address, detail: `${c.address} · no agent` }, network: subnet, hostId: x.hostId ?? undefined });
      } else {
        peer = 'peer:private';
        peers.set(peer, { data: { kind: 'private', title: 'LAN, other networks', detail: 'private addresses' } });
      }
      const [from, to] = c.inbound ? [peer, x.containerId] : [x.containerId, peer];
      const key = `${from}>${to}`;
      const link = mine.get(key) ?? { from, to, rate: 0, bytes: 0, ports: new Set<number>(), sure: false };
      link.rate += c.rate;
      link.bytes += c.bytes;
      link.ports.add(c.port);
      link.sure ||= c.sure;
      mine.set(key, link);
    }
    for (const link of mine.values()) {
      const pair = [link.from, link.to].sort().join('|');
      pairs.set(pair, [...(pairs.get(pair) ?? []), link]);
    }
  }

  // One link per pair, the way the most certain and busiest view says; both ends report it.
  const merged = [...pairs.values()].map(views => {
    const best = views.reduce((a, b) => (Number(b.sure) - Number(a.sure) || b.bytes - a.bytes || b.rate - a.rate) > 0 ? b : a);
    const same = views.filter(v => v.from === best.from);
    return { ...best, rate: Math.max(...views.map(v => v.rate)), ports: new Set(same.flatMap(v => [...v.ports])) };
  });
  return { links: merged, peers };
}

function inSubnet(address: string, cidr: string) {
  const [base, bits] = cidr.split('/');
  if (!base || !/^\d+\.\d+\.\d+\.\d+$/.test(address)) return false;
  const value = (ip: string) => ip.split('.').reduce((v, part) => v * 256 + Number(part), 0);
  const size = 2 ** (32 - Number(bits));
  return Math.floor(value(address) / size) === Math.floor(value(base) / size);
}

const outside = (id: string) => id === 'peer:internet' || id === 'peer:private' || id.startsWith('peer:host:');

// Each host laid out by dagre, left to right from the side that asks: the host a box, its Docker networks dashed
// boxes inside it, the host itself (its gateway) inside the box and the LAN and the internet outside it. dagre keeps
// each box's cards together and routes every line through the room it leaves between cards, so no line crosses a
// card and no two meet. Hosts stand one under another. On a container's page only the container and what it talks
// to are drawn.
function build(hosts: HostSummary[], network: ContainerNetwork[] | null, focus: string | null, range: number, hostId: string | null) {
  const found = links(network ?? [], range);
  const info = new Map((network ?? []).map(x => [x.containerId, x.network]));
  // The containers reporting now (one that went quiet has no network to draw), and the one whose page this is.
  const hostOf = new Map(hosts.flatMap(h => h.containers.filter(w => w.status === 'online' || w.containerId === focus).map(w => [w.containerId, h] as const)));
  let used = found.links.filter(l => hostOf.has(l.from) || hostOf.has(l.to));
  if (focus) used = used.filter(l => l.from === focus || l.to === focus);
  const linked = new Set(used.flatMap(l => [l.from, l.to]));
  // The outside always on the receiving end; each host has its own internet and LAN.
  const lines = used.map(l => {
    const flipped = outside(l.from);
    const [from, to] = flipped ? [l.to, l.from] : [l.from, l.to];
    const host = hostOf.get(from) ?? hostOf.get(to);
    const target = to === 'peer:internet' || to === 'peer:private' ? `${to}@${host?.hostId}` : to;
    return { ...l, from, to: target, id: `${from}>${target}`, askerAtTarget: flipped };
  });

  const peerData = (id: string) => found.peers.get(id.split('@')[0])?.data;
  const containerData = (id: string): CardData => {
    const w = hostOf.get(id)!.containers.find(x => x.containerId === id)!;
    const net = info.get(id);
    const ports = [...new Set(net?.listening.map(l => l.port) ?? [])].sort((a, b) => a - b);
    return {
      kind: 'container', title: nameOf(w), status: w.status, focused: id === focus, containerId: id,
      detail: [net?.addresses?.[0], ports.length ? `:${ports.slice(0, 3).join(' :')}` : null].filter(Boolean).join(' · ') || 'no address reported',
    };
  };
  const titleOf = (id: string) => (hostOf.has(id) ? nameOf(hostOf.get(id)!.containers.find(x => x.containerId === id)!) : peerData(id)?.title ?? id);

  const nodes: Node[] = [];
  const at = new Map<string, Pt>();
  const routed = new Map<string, Pt[]>();
  let top = 0;
  for (const h of hosts.filter(h => !hostId || h.hostId === hostId).sort((a, b) => a.displayName.localeCompare(b.displayName))) {
    const members = [...hostOf.entries()].filter(([id, host]) => host === h && (focus ? id === focus || linked.has(id) : true)).map(([id]) => id);
    if (!members.length) continue;
    const addresses = [...found.peers.entries()].filter(([id, p]) => p.data.kind === 'address' && p.hostId === h.hostId && linked.has(id)).map(([id]) => id);
    const inside = new Set([...members, ...addresses]);
    const mine = lines.filter(l => inside.has(l.from) || inside.has(l.to));
    const networkOf = (id: string) => (hostOf.has(id) ? info.get(id)?.networks?.[0] : found.peers.get(id)?.network) ?? 'unknown';

    const g = new dagre.graphlib.Graph({ compound: true });
    g.setGraph({ rankdir: 'LR', nodesep: 28, ranksep: 84, edgesep: 20, marginx: 0, marginy: 0 });
    g.setDefaultEdgeLabel(() => ({}));
    const hostBox = `box:${h.hostId}`;
    g.setNode(hostBox, {});
    // Sorted, so the same traffic always gives the same map.
    const ids = [...inside].sort((a, b) => titleOf(a).localeCompare(titleOf(b)) || a.localeCompare(b));
    for (const net of [...new Set(ids.map(networkOf))].sort()) {
      g.setNode(`${hostBox}:${net}`, {});
      g.setParent(`${hostBox}:${net}`, hostBox);
    }
    for (const id of ids) {
      g.setNode(id, { width: NODE_W, height: NODE_H });
      g.setParent(id, `${hostBox}:${networkOf(id)}`);
    }
    for (const l of mine) {
      for (const id of [l.from, l.to]) {
        if (!g.hasNode(id)) {
          g.setNode(id, { width: NODE_W, height: NODE_H });
          if (id.startsWith('peer:host:')) g.setParent(id, hostBox);
        }
      }
      g.setEdge(l.from, l.to);
    }
    // The quiet cards of each network side by side, chained by lines nobody sees, rather than in one tall column.
    const talking = new Set(mine.flatMap(l => [l.from, l.to]));
    const quiet = new Map<string, string[]>();
    for (const id of ids.filter(id => !talking.has(id))) quiet.set(networkOf(id), [...(quiet.get(networkOf(id)) ?? []), id]);
    for (const row of quiet.values()) row.forEach((id, i) => { if (i % QUIET_ROW) g.setEdge(row[i - 1], id, { weight: 0 }); });
    dagre.layout(g);

    const height = g.graph().height ?? 0;
    const place = (p: { x: number; y: number }): Pt => [p.x, top + p.y];
    for (const id of g.nodes()) {
      const n = g.node(id);
      if (id.startsWith('box:')) {
        const host = id === hostBox;
        const net = id.slice(hostBox.length + 1);
        const [x, y] = place({ x: n.x - n.width / 2, y: n.y - n.height / 2 });
        nodes.unshift({
          id, type: 'box', position: { x, y }, style: { width: n.width, height: n.height }, zIndex: host ? -2 : -1, draggable: false, selectable: false,
          data: host ? { label: h.displayName, host, online: h.online } : { label: 'network', detail: net === 'unknown' ? 'not reported' : net, host },
        });
        continue;
      }
      const [x, y] = place({ x: n.x - NODE_W / 2, y: n.y - NODE_H / 2 });
      at.set(id, [x, y]);
      nodes.push({
        id, type: 'card', position: { x, y }, measured: { width: NODE_W, height: NODE_H }, draggable: false, selectable: false,
        data: hostOf.has(id) ? containerData(id) : peerData(id)!,
      });
    }
    for (const l of mine) routed.set(l.id, (g.edge(l.from, l.to)?.points ?? []).map(place));
    top += Math.max(height, NODE_H) + HOST_GAP;
  }

  // A port for each line on each side of a card, in the order of where the lines go, so they part at the card.
  const drawn = lines.filter(l => at.has(l.from) && at.has(l.to));
  const middle = (id: string): Pt => [at.get(id)![0] + NODE_W / 2, at.get(id)![1] + NODE_H / 2];
  const sides = new Map<string, { node: string; end: string; toward: number }[]>();
  const side = (node: string, right: boolean, end: string, toward: number) => {
    const key = `${node} ${right ? 'r' : 'l'}`;
    sides.set(key, [...(sides.get(key) ?? []), { node, end, toward }]);
  };
  const via = new Map<string, Pt[]>();
  for (const l of drawn) {
    // dagre's own ends sit on the cards' outlines; the points between are where the line passes.
    const points = (routed.get(l.id) ?? []).slice(1, -1);
    const [first, last] = [points[0] ?? middle(l.to), points[points.length - 1] ?? middle(l.from)];
    via.set(l.id, points);
    side(l.from, first[0] >= middle(l.from)[0], `${l.id}<`, first[1]);
    side(l.to, last[0] > middle(l.to)[0], `${l.id}>`, last[1]);
  }
  const port = new Map<string, Pt>();
  for (const [key, ends] of sides) {
    ends.sort((a, b) => a.toward - b.toward || a.end.localeCompare(b.end));
    const [x, y] = at.get(ends[0].node)!;
    const right = key.endsWith(' r');
    const step = ends.length > 1 ? Math.min(PORT, (NODE_H - 12) / (ends.length - 1)) : 0;
    ends.forEach((e, i) => port.set(e.end, [right ? x + NODE_W : x, y + NODE_H / 2 + (i - (ends.length - 1) / 2) * step]));
  }

  const paths = new Map(drawn.map(l => [l.id, [port.get(`${l.id}<`)!, ...via.get(l.id)!, port.get(`${l.id}>`)!]]));
  const placed = turns([...paths].flatMap(([id, path]) =>
    path.slice(1).map((q, i) => ({ id: `${id}#${i}`, sx: path[i][0], sy: path[i][1], tx: q[0], ty: q[1] }))));

  const edges: Edge<TrafficData>[] = [];
  const routes: Route[] = [];
  for (const l of drawn) {
    const path = paths.get(l.id)!;
    const at = path.slice(1).map((_, i) => placed.get(`${l.id}#${i}`));
    const { stops, length, mid } = track(path, at);
    edges.push({
      id: l.id, source: l.from, target: l.to, type: 'traffic', sourceHandle: 'r', targetHandle: 'tl',
      data: { rate: l.rate, ports: [...l.ports].sort((a, b) => a - b), path: svgPath(path, at), mid, from: titleOf(l.from), to: titleOf(l.to), selected: false, hovered: false, dim: false },
    });
    routes.push({ id: l.id, stops, shape: JSON.stringify(stops), length, askerAtTarget: l.askerAtTarget, dim: false });
  }
  return { nodes, edges, routes };
}

type Drawing = ReturnType<typeof build> & { left: number; top: number; width: number; height: number };

function measure(drawing: ReturnType<typeof build>): Drawing {
  const xs = drawing.nodes.flatMap(n => [n.position.x, n.position.x + (Number(n.style?.width) || NODE_W)]);
  const ys = drawing.nodes.flatMap(n => [n.position.y - 10, n.position.y + (Number(n.style?.height) || NODE_H)]);
  const [left, top] = [Math.min(...xs, 0), Math.min(...ys, 0)];
  return { ...drawing, left, top, width: Math.max(Math.max(...xs, 0) - left, 1), height: Math.max(Math.max(...ys, 0) - top, 1) };
}

// The network map: the fleet's, a host's, or a container's and what it talks to.
export function NetworkMap({ r, scope, range, period, onScope, onOpen }: {
  r: Resolved; scope: Scope; range: number; period: string; onScope: (s: Scope) => void; onOpen: (view: View) => void;
}) {
  const network = usePolled<ContainerNetwork[]>('api/network', 5000);
  // Rebuilt only when what it draws changes: not on every second's fleet update.
  const drawnHosts = JSON.stringify(r.hosts.map(h => [h.hostId, h.displayName, h.online, h.containers.map(w => [w.containerId, w.name, w.status])]));
  const hosts = useMemo(() => r.hosts, [drawnHosts]);
  const area = useRef<HTMLDivElement>(null);
  const [flow, setFlow] = useState<ReactFlowInstance | null>(null);
  const [frame, setFrame] = useState({ width: 0, height: 0, maximized: false });
  const drawing = useMemo(() => measure(build(hosts, network, scope.containerId, range, scope.hostId)), [hosts, network, scope.containerId, scope.hostId, range]);
  const { left, top, width: drawingWidth, height: drawingHeight } = drawing;

  // A selected line stands out with its two cards; the rest fade. A hovered one shows its label.
  const [selected, setSelected] = useState<string | null>(null);
  const [hovered, setHovered] = useState<string | null>(null);
  const chosen = selected && drawing.edges.some(e => e.id === selected) ? selected : null;
  const ends = useMemo(() => {
    const edge = drawing.edges.find(e => e.id === chosen);
    return edge ? new Set([edge.source, edge.target]) : null;
  }, [drawing, chosen]);
  const nodes = useMemo(() => (ends
    ? drawing.nodes.map(n => (n.type === 'card' ? { ...n, data: { ...n.data, lit: ends.has(n.id), dim: !ends.has(n.id) } } : n))
    : drawing.nodes), [drawing, ends]);
  const edges = useMemo(() => drawing.edges.map(e => (e.id === chosen || e.id === hovered || chosen
    ? { ...e, zIndex: e.id === chosen ? 1 : 0, data: { ...e.data!, selected: e.id === chosen, hovered: e.id === hovered, dim: !!chosen && e.id !== chosen } }
    : e)), [drawing, chosen, hovered]);
  const routes = useMemo(() => (chosen ? drawing.routes.map(r => ({ ...r, dim: r.id !== chosen })) : drawing.routes), [drawing, chosen]);

  const tallest = Math.max(320, window.innerHeight * 0.9);
  const fitted = (w: number, h: number, floor = READABLE) => Math.min(MAX_ZOOM, Math.max(floor, Math.min((w - 24) / drawingWidth, (h - 24) / drawingHeight)));
  // On the page, as tall as the drawing needs at the width there is; maximized, the whole panel.
  const height = Math.round(Math.min(Math.max(200, drawingHeight * fitted(frame.width || 1200, Infinity) + 24), tallest));

  useEffect(() => {
    const el = area.current;
    if (!el) return;
    const observer = new ResizeObserver(([entry]) =>
      setFrame({ width: entry.contentRect.width, height: entry.contentRect.height, maximized: !!el.closest('.panel-maximized') }));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  // Fitted to both sides of the panel, never below the readable zoom unless all of it is asked for: centred when it
  // fits, from its top left when it doesn't. Once the map is moved by hand, refreshes leave it be until the panel
  // is resized.
  const moved = useRef(false);
  const fit = useCallback((all = false) => {
    if (!flow || !frame.width || !frame.height) return;
    const zoom = fitted(frame.width, frame.height, all ? 0.1 : READABLE);
    const [w, h] = [drawingWidth * zoom, drawingHeight * zoom];
    flow.setViewport({
      x: (w <= frame.width - 24 ? (frame.width - w) / 2 : 12) - left * zoom,
      y: (h <= frame.height - 24 ? (frame.height - h) / 2 : 12) - top * zoom,
      zoom,
    });
  }, [flow, frame.width, frame.height, drawingWidth, drawingHeight, left, top]);
  useEffect(() => { moved.current = false; }, [frame.width, frame.height, frame.maximized]);
  useEffect(() => { if (!moved.current) fit(); }, [fit]);

  const dark = document.documentElement.dataset.theme !== 'light';
  const subtitle = !network ? 'Loading…'
    : (scope.containerId ? `This container and what it talked to in the last ${period}.` : `Who talked to whom in the last ${period}, by host and Docker network.`)
      + ' Dots run from the side that opens the connection. Select a line to follow it; drag to move, pinch or Ctrl + scroll to zoom.';

  return (
    <Panel icon={<IconNetwork size={14} />} title="Map" help={HELP.map} wide fit subtitle={subtitle}>
      <div ref={area} style={{ height }} className="in-[.panel-maximized]:h-full! in-[.panel-maximized]:min-h-0 in-[.panel-maximized]:flex-1">
        <ReactFlow
          nodes={nodes}
          edges={edges}
          nodeTypes={nodeTypes}
          edgeTypes={edgeTypes}
          colorMode={dark ? 'dark' : 'light'}
          style={{ background: 'transparent' }}
          nodesConnectable={false}
          nodesDraggable={false}
          elementsSelectable={false}
          // On the page the wheel scrolls the page unless Ctrl is held; maximized, it zooms.
          zoomOnScroll={frame.maximized}
          preventScrolling={frame.maximized}
          zoomOnDoubleClick={false}
          zoomOnPinch
          panOnDrag
          proOptions={{ hideAttribution: true }}
          minZoom={0.1}
          maxZoom={2}
          onMoveStart={event => { if (event) moved.current = true; }}
          onInit={setFlow}
          onEdgeClick={(_, edge) => setSelected(s => (s === edge.id ? null : edge.id))}
          onEdgeMouseEnter={(_, edge) => setHovered(edge.id)}
          onEdgeMouseLeave={() => setHovered(null)}
          onPaneClick={() => setSelected(null)}
          onNodeClick={(_, node) => {
            const id = (node.data as CardData).containerId;
            const host = id && r.hosts.find(h => h.containers.some(w => w.containerId === id));
            if (id && host) {
              onScope({ hostId: host.hostId, containerId: id });
              onOpen('network');
            }
          }}
        >
          <Packets routes={routes} />
          <Controls
            position="bottom-right"
            showInteractive={false}
            showFitView={false}
            onZoomIn={() => { moved.current = true; }}
            onZoomOut={() => { moved.current = true; }}
          >
            <ControlButton title="Show the whole map" aria-label="Show the whole map" onClick={() => { moved.current = true; fit(true); }}>
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
                <path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5" />
              </svg>
            </ControlButton>
          </Controls>
        </ReactFlow>
      </div>
    </Panel>
  );
}
