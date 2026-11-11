import { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import { clock, Timeline, type Column } from './timeline';
import { nameContainers } from './models';
import type { FleetHistory, FleetUpdate, HostHistory, HostSummary, ContainerHistory, ContainerState } from './types';

export interface Scope {
  hostId: string | null;
  containerId: string | null;
}

export const RANGES = [
  { label: '1m', seconds: 60 },
  { label: '5m', seconds: 300 },
  { label: '15m', seconds: 900 },
  { label: '1h', seconds: 3600 },
  { label: '6h', seconds: 21600 },
  { label: '24h', seconds: 86400 },
];

export interface Resolved {
  hosts: HostSummary[];
  host: HostSummary | null;
  container: ContainerState | null;
}

export function resolve(scope: Scope, fleet: FleetUpdate | null): Resolved {
  const hosts = fleet?.hosts ?? [];
  const host = scope.hostId ? hosts.find(h => h.hostId === scope.hostId) ?? null : null;
  const container = host && scope.containerId ? host.containers.find(w => w.containerId === scope.containerId) ?? null : null;
  return { hosts, host, container };
}

function readScope(): Scope {
  const params = new URLSearchParams(location.search);
  const hostId = params.get('host');
  return { hostId, containerId: hostId ? params.get('container') : null };
}

export function useScope(): [Scope, (scope: Scope) => void] {
  const [scope, setScope] = useState<Scope>(readScope);
  const update = useCallback((next: Scope) => {
    history.replaceState(null, '', query(next, readView()));
    setScope(next);
  }, []);
  useEffect(() => {
    const onPop = () => setScope(readScope());
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);
  return [scope, update];
}

export type View = 'cpu' | 'memory' | 'gpu' | 'disk' | 'network' | 'events';
export const VIEWS: View[] = ['cpu', 'memory', 'gpu', 'disk', 'network', 'events'];
export const VIEW_LABELS: Record<View, string> = { cpu: 'CPU', memory: 'Memory', gpu: 'GPU', disk: 'Disk', network: 'Network', events: 'Events' };

// The pages a scope has: the fleet has the overview and its network (the map).
export const viewsOf = (scope: Scope): View[] => (scope.hostId ? VIEWS : []);

function readView(): View | null {
  const view = new URLSearchParams(location.search).get('view');
  return VIEWS.includes(view as View) ? (view as View) : null;
}

function query(scope: Scope, view: View | null) {
  const params = new URLSearchParams();
  if (scope.hostId) params.set('host', scope.hostId);
  if (scope.hostId && scope.containerId) params.set('container', scope.containerId);
  if (view) params.set('view', view);
  const text = params.toString();
  return text ? `?${text}` : location.pathname;
}

export function useView(): [View | null, (view: View | null) => void] {
  const [view, setView] = useState<View | null>(readView);
  const update = useCallback((next: View | null) => {
    if (next === readView()) return;
    history.pushState(null, '', query(readScope(), next));
    setView(next);
  }, []);
  useEffect(() => {
    const onPop = () => setView(readView());
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);
  return [view, update];
}

export function usePolled<T>(url: string | null, everyMs: number): T | null {
  const [data, setData] = useState<{ url: string; value: T } | null>(null);
  useEffect(() => {
    if (!url) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    const controller = new AbortController();
    const load = () =>
      fetch(url, { signal: controller.signal })
        .then(response => (response.ok ? response.json() : Promise.reject(new Error(String(response.status)))))
        .then(value => { if (!stopped) setData({ url, value }); })
        .catch(() => { /* keep the last data; retry on the next tick */ })
        .finally(() => { if (!stopped) timer = setTimeout(load, everyMs); });
    load();
    return () => { stopped = true; clearTimeout(timer); controller.abort(); };
  }, [url, everyMs]);
  return data && data.url === url ? data.value : null;
}

export function useStored<T extends string | number>(key: string, fallback: T, valid: (v: T) => boolean): [T, (v: T) => void] {
  const [value, setValue] = useState<T>(() => {
    try {
      const raw = localStorage.getItem(key);
      const parsed = (typeof fallback === 'number' ? Number(raw) : raw) as T;
      return raw != null && valid(parsed) ? parsed : fallback;
    } catch {
      return fallback;
    }
  });
  const update = useCallback((v: T) => {
    setValue(v);
    try { localStorage.setItem(key, String(v)); } catch { /* storage unavailable */ }
  }, [key]);
  return [value, update];
}

export function useNow(): number {
  const [now, setNow] = useState(clock.now);
  useEffect(() => {
    const id = setInterval(() => setNow(clock.now()), 1000);
    return () => clearInterval(id);
  }, []);
  return now;
}

export type LiveStatus = 'connecting' | 'live' | 'stale' | 'error';

export function useFleet(hostId: string | null, containerId: string | null) {
  const [fleet, setFleet] = useState<FleetUpdate | null>(null);
  const [status, setStatus] = useState<LiveStatus>('connecting');
  const [reconnects, setReconnects] = useState(0);

  useEffect(() => {
    setStatus('connecting');
    const params = new URLSearchParams();
    if (hostId) params.set('hosts', hostId);
    if (hostId && containerId) params.set('containers', containerId);
    const source = new EventSource(`api/fleet/stream?${params}`);
    let failed = false;
    let staleTimer: ReturnType<typeof setTimeout>;
    let lastTimestamp: string | null = null;
    const stale = () => { failed = true; setStatus('stale'); };
    staleTimer = setTimeout(stale, 10_000);
    source.addEventListener('fleet', event => {
      const update = JSON.parse((event as MessageEvent<string>).data) as FleetUpdate;
      const age = Math.max(0, update.ageMilliseconds ?? 0);
      if (update.timestamp !== lastTimestamp) {
        lastTimestamp = update.timestamp;
        clearTimeout(staleTimer);
        if (age >= 10_000) stale();
        else {
          setStatus('live');
          if (failed) { failed = false; setReconnects(n => n + 1); }
          staleTimer = setTimeout(stale, 10_000 - age);
        }
      }
      clock.offset = Date.parse(update.timestamp) + age - Date.now();
      nameContainers(update.hosts);
      setFleet(update);
    });
    source.onerror = () => { clearTimeout(staleTimer); failed = true; setStatus('error'); };
    return () => { clearTimeout(staleTimer); source.close(); };
  }, [hostId, containerId]);

  return { fleet, status, reconnects };
}

function historyRequest(scope: Scope, seconds: number): { url: string; convert: (json: unknown) => [number[], Record<string, Column>, number] } {
  if (!scope.hostId) {
    return {
      url: `api/fleet/history?seconds=${seconds}`,
      convert: json => {
        const h = json as FleetHistory;
        const columns: Record<string, Column> = {};
        for (const s of h.hosts) {
          Object.assign(columns, {
            [`cpu:${s.hostId}`]: s.cpuCores, [`wcpu:${s.hostId}`]: s.containersCpuCores, [`mem:${s.hostId}`]: s.memoryUsed, [`gpu:${s.hostId}`]: s.gpuUtilization, [`vram:${s.hostId}`]: s.vramUsed,
            [`read:${s.hostId}`]: s.diskRead, [`write:${s.hostId}`]: s.diskWrite, [`rx:${s.hostId}`]: s.netReceive, [`tx:${s.hostId}`]: s.netTransmit,
          });
        }
        return [h.t, columns, h.bucketSeconds];
      },
    };
  }
  if (!scope.containerId) {
    return {
      url: `api/hosts/${encodeURIComponent(scope.hostId)}/history?seconds=${seconds}`,
      convert: json => {
        const h = json as HostHistory;
        const columns: Record<string, Column> = {
          cpu: h.cpu, mem: h.memoryUsed, read: h.diskRead, write: h.diskWrite, rx: h.netReceive, tx: h.netTransmit,
        };
        for (const g of h.gpus) { columns[`gpu:${g.index}`] = g.utilization; columns[`vram:${g.index}`] = g.memoryUsage; }
        for (const w of h.containers) {
          columns[`wcpu:${w.containerId}`] = w.cpuPercentOfHost;
          columns[`wmem:${w.containerId}`] = w.memoryBytes;
        }
        return [h.t, columns, h.bucketSeconds];
      },
    };
  }
  return {
    url: `api/containers/${encodeURIComponent(scope.containerId)}/history?seconds=${seconds}`,
    convert: json => {
      const h = json as ContainerHistory;
      return [
        h.t,
        {
          cpu: h.cpuCores,
          mem: h.memoryBytes,
          read: h.ioRead,
          write: h.ioWrite,
          rx: h.netReceive,
          tx: h.netTransmit,
          gpu: h.gpuPercent ?? [],
          vram: h.gpuMemoryBytes ?? [],
        },
        h.bucketSeconds,
      ];
    },
  };
}

function liveRow(scope: Scope, fleet: FleetUpdate): { t: number; row: Record<string, number | null> } | null {
  if (!scope.hostId) {
    const row: Record<string, number | null> = {};
    for (const h of fleet.hosts) {
      if (!h.online) continue;
      row[`cpu:${h.hostId}`] = h.cpuPercent != null && h.logicalCores ? (h.cpuPercent / 100) * h.logicalCores : null;
      row[`wcpu:${h.hostId}`] = h.containerTotals?.cpuCores ?? null;
      row[`mem:${h.hostId}`] = h.memoryUsedBytes;
      row[`vram:${h.hostId}`] = h.gpus.length ? h.gpus.reduce((s, g) => s + (g.memoryUsedBytes ?? 0), 0) : null;
      row[`gpu:${h.hostId}`] = h.gpus.length ? h.gpus.reduce((s, g) => s + (g.utilizationPercent ?? 0), 0) / h.gpus.length : null;
      row[`read:${h.hostId}`] = h.diskReadBytesPerSecond;
      row[`write:${h.hostId}`] = h.diskWriteBytesPerSecond;
      row[`rx:${h.hostId}`] = h.netReceiveBytesPerSecond;
      row[`tx:${h.hostId}`] = h.netTransmitBytesPerSecond;
    }
    return { t: Date.parse(fleet.timestamp), row };
  }

  const host = fleet.hosts.find(h => h.hostId === scope.hostId);
  if (!host) return null;

  if (!scope.containerId) {
    const detail = fleet.details[scope.hostId];
    if (!detail) return null;
    const s = detail.snapshot;
    const row: Record<string, number | null> = {
      cpu: s.cpu?.usagePercent ?? null, mem: s.memory?.usedBytes ?? null,
      read: s.disk.readBytesPerSecond, write: s.disk.writeBytesPerSecond,
      rx: s.network.available ? s.network.receiveBytesPerSecond : null, tx: s.network.available ? s.network.transmitBytesPerSecond : null,
    };
    for (const d of s.gpu.devices) { row[`gpu:${d.index}`] = d.utilizationPercent; row[`vram:${d.index}`] = d.memoryUsagePercent; }
    for (const w of host.containers) {
      if (w.status !== 'online' || !w.usage) continue;
      row[`wcpu:${w.containerId}`] = w.usage.cpuPercentOfHost;
      row[`wmem:${w.containerId}`] = w.usage.memoryBytes;
    }
    return { t: Date.parse(detail.timestamp), row };
  }

  const container = host.containers.find(w => w.containerId === scope.containerId);
  const u = container?.status === 'online' ? container.usage : null;
  if (!container?.sampledAt || !u) return null;
  return {
    t: Date.parse(container.sampledAt),
    row: {
      cpu: u.cpuCores,
      mem: u.memoryBytes,
      read: u.ioReadBytesPerSecond,
      write: u.ioWriteBytesPerSecond,
      rx: u.netReceiveBytesPerSecond,
      tx: u.netTransmitBytesPerSecond,
      gpu: u.gpuPercent,
      vram: u.gpuMemoryBytes,
    },
  };
}

export function useScopeTimeline(scope: Scope, seconds: number, fleet: FleetUpdate | null, reconnects: number) {
  const timeline = useRef(new Timeline());
  const [version, bump] = useReducer((x: number) => x + 1, 0);
  const [historyStatus, setHistoryStatus] = useState<'loading' | 'retrying' | 'ready' | 'empty' | 'error'>('loading');
  const [retry, retryHistory] = useReducer((x: number) => x + 1, 0);
  const key = `${scope.hostId ?? ''}\n${scope.containerId ?? ''}`;
  const cutoff = () => clock.now() - seconds * 1000 - 60_000;

  useEffect(() => {
    const tl = new Timeline();
    timeline.current = tl;
    bump();
    const controller = new AbortController();
    const { url, convert } = historyRequest(scope, seconds);
    let timer: ReturnType<typeof setTimeout>;
    let attempts = 0;
    setHistoryStatus('loading');
    const load = async () => {
      attempts++;
      try {
        const response = await fetch(url, { signal: AbortSignal.any([controller.signal, AbortSignal.timeout(10_000)]) });
        if (!response.ok) throw new Error(String(response.status));
        const json = await response.json();
        if (controller.signal.aborted) return;
        const [t, columns, bucket] = convert(json);
        tl.load(t, columns, bucket, cutoff());
        setHistoryStatus(t.length ? 'ready' : 'empty');
        bump();
        // Over hours the live points since the load would add up to thousands: the history is read again once a
        // bucket, which takes their place.
        if (seconds > 3600) {
          attempts = 0;
          timer = setTimeout(load, Math.max(60, bucket) * 1000);
        }
      } catch {
        if (controller.signal.aborted) return;
        if (attempts < 4) {
          setHistoryStatus('retrying');
          timer = setTimeout(load, 1000 * 2 ** (attempts - 1));
        } else setHistoryStatus('error');
      }
    };
    void load();
    return () => { clearTimeout(timer); controller.abort(); };
  }, [key, seconds, reconnects, retry]);

  useEffect(() => {
    if (!fleet) return;
    const live = liveRow(scope, fleet);
    if (live && timeline.current.push(live.t, live.row, cutoff(), scope.hostId ? 0 : 900)) bump();
  }, [fleet]);

  return { timeline: timeline.current, version, historyStatus, retryHistory };
}
