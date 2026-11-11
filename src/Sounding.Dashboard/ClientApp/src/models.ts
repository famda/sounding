import type { Resolved, Scope } from './data';
import { fmtBytes, fmtCores, fmtDuration, fmtPct, fmtRate, pct, sum } from './format';
import { levelOf, THRESHOLDS, type Level, T } from './theme';
import type { Column, Timeline } from './timeline';
import type { HostSummary, ContainerState } from './types';

export type Unit = 'cores' | 'bytes' | 'percent' | 'rate' | 'ops' | 'watts' | 'ms' | 'packets' | 'perSecond' | 'count' | 'celsius' | 'latency';

export const fmt = (unit: Unit, v: number | null | undefined, compact = false) =>
  unit === 'cores' ? fmtCores(v, compact)
    : unit === 'bytes' ? fmtBytes(v, compact ? 0 : 1)
    : unit === 'rate' ? fmtRate(v, compact)
    : unit === 'ops' ? (v == null ? '–' : `${v.toFixed(compact || v >= 10 ? 0 : 1)} IOPS`)
    : unit === 'watts' ? (v == null ? '–' : `${v.toFixed(compact || v >= 10 ? 0 : 1)} W`)
    : unit === 'ms' ? (v == null ? '–' : `${v.toFixed(compact || v >= 10 ? 0 : 1)} ms/s`)
    : unit === 'packets' ? (v == null ? '–' : compact ? `${v >= 1000 ? `${(v / 1000).toFixed(0)}k` : v.toFixed(0)}/s` : `${v.toFixed(v >= 10 ? 0 : 1)} pkt/s`)
    : unit === 'perSecond' ? (v == null ? '–' : `${v >= 1000 ? `${(v / 1000).toFixed(v >= 10000 || compact ? 0 : 1)}k` : v.toFixed(compact || v >= 10 ? 0 : 1)}/s`)
    : unit === 'count' ? (v == null ? '–' : v >= 10000 ? `${(v / 1000).toFixed(v >= 100000 || compact ? 0 : 1)}k` : v.toFixed(compact || v >= 10 || Number.isInteger(v) ? 0 : 1))
    : unit === 'celsius' ? (v == null ? '–' : `${v.toFixed(compact || v >= 10 ? 0 : 1)} °C`)
    : unit === 'latency' ? (v == null ? '–' : `${v.toFixed(compact || v >= 10 ? 0 : 1)} ms`)
    : fmtPct(v, compact);

export const busyCores = (h: HostSummary) => (h.cpuPercent != null && h.logicalCores ? (h.cpuPercent / 100) * h.logicalCores : null);
export const gpuAverage = (h: HostSummary) => (h.gpus.length ? sum(h.gpus, g => g.utilizationPercent) / h.gpus.length : null);
export const vram = (h: HostSummary) => ({ used: sum(h.gpus, g => g.memoryUsedBytes), total: sum(h.gpus, g => g.memoryTotalBytes) });
export const hostVolumes = (h: HostSummary) =>
  h.volumes.length > 1 && h.volumes.some(v => v.path !== '/') ? h.volumes.filter(v => v.path !== '/') : h.volumes;
export const storage = (h: HostSummary) => {
  const vols = hostVolumes(h);
  return { used: sum(vols, v => v.usedBytes), total: sum(vols, v => v.totalBytes) };
};
export const online = (hosts: HostSummary[]) => hosts.filter(h => h.online);
export const running = (h: HostSummary) => h.containers.filter(w => w.status === 'online');
export const usageOf = (w: ContainerState) => (w.status === 'online' ? w.usage : null);
export const nameOf = (w: ContainerState) => w.shortName ?? w.name ?? w.containerId;
export const fullNameOf = (w: ContainerState) => w.name ?? w.containerId;

// Groups (such as compose projects) are found across the fleet: a project can span hosts.
export function nameContainers(hosts: HostSummary[]) {
  const names = [...new Set(hosts.flatMap(h => h.containers.map(w => w.name)).filter((n): n is string => Boolean(n)))];
  for (const h of hosts) {
    for (const w of h.containers) {
      const group = w.name ? groupOf(w.name, names) : null;
      w.group = group?.slice(0, -1) ?? null;
      w.shortName = group ? w.name!.slice(group.length) : undefined;
    }
  }
}

function groupOf(name: string, names: string[]): string | null {
  for (let i = name.length - 2; i > 0; i--) {
    if (!'-_.'.includes(name[i])) continue;
    const prefix = name.slice(0, i + 1);
    const rest = name.slice(i + 1);
    if (/^\d+$/.test(rest)) continue;
    if (names.filter(n => n.startsWith(prefix)).length < 2) continue;
    if (names.some(n => n.startsWith(prefix) && /^\d+$/.test(n.slice(prefix.length)))) continue;
    return prefix;
  }
  return null;
}
export const wslVm = (h: HostSummary) => /WSL2/i.test(h.kernel ?? '');
export const containerVolume = (h: HostSummary) => hostVolumes(h)[0] ?? null;
export const allocated = (h: HostSummary) => ({
  cpu: sum(running(h), w => w.usage?.cpuLimitCores),
  memory: sum(running(h), w => w.usage?.memoryLimitBytes),
});
export const ago = (iso: string) => `${fmtDuration((Date.now() - Date.parse(iso)) / 1000)} ago`;

export interface Bullet { used: number; cap: number; limit: number | null; level: Level; unit: Unit }

export interface Card { id: string; label: string; value: string; subValue?: string; lines: string[]; bullet: Bullet | null }

// The tiles of a host or container that isn't reporting, with the reason in place of figures.
const placeholders = (why: string): Card[] =>
  ([['cpu', 'CPU'], ['memory', 'Memory'], ['gpu', 'GPU'], ['storage', 'Disk'], ['network', 'Network']] as const)
    .map(([id, label]) => ({ id, label, value: '–', lines: [why], bullet: null }));

export function cards(r: Resolved, scope: Scope): Card[] {
  const bullet = (used: number | null | undefined, cap: number | null | undefined, thresholds: readonly [number, number], unit: Unit): Bullet | null =>
    used == null || !cap ? null : { used, cap, limit: null, level: levelOf(pct(used, cap), thresholds), unit };

  if (!scope.hostId) {
    const hosts = online(r.hosts);
    const cores = sum(hosts, h => h.logicalCores), busy = sum(hosts, busyCores);
    const containersCpu = sum(hosts, h => h.containerTotals?.cpuCores ?? sum(running(h), w => w.usage?.cpuCores));
    const memTotal = sum(hosts, h => h.memoryTotalBytes), memUsed = sum(hosts, h => h.memoryUsedBytes);
    const containersMem = sum(hosts, h => h.containerTotals?.memoryBytes ?? sum(running(h), w => w.usage?.memoryBytes));
    const gpus = hosts.flatMap(h => h.gpus);
    const gpuUtil = gpus.length ? sum(gpus, g => g.utilizationPercent) / gpus.length : null;
    const vramUsed = sum(gpus, g => g.memoryUsedBytes), vramTotal = sum(gpus, g => g.memoryTotalBytes);
    const disk = { used: sum(hosts, h => storage(h).used), total: sum(hosts, h => storage(h).total) };
    const read = sum(hosts, h => h.diskReadBytesPerSecond), write = sum(hosts, h => h.diskWriteBytesPerSecond);
    const rx = sum(hosts, h => h.netReceiveBytesPerSecond), tx = sum(hosts, h => h.netTransmitBytesPerSecond);
    return [
      { id: 'cpu', label: 'CPU', value: fmtPct(pct(busy, cores)), subValue: `${busy.toFixed(1)} / ${cores} cores`, lines: [`${busy.toFixed(1)} of ${cores} cores active`, `Containers: ${fmtCores(containersCpu)}`], bullet: bullet(busy, cores, THRESHOLDS.cpu, 'cores') },
      { id: 'memory', label: 'Memory', value: fmtPct(pct(memUsed, memTotal)), subValue: `${fmtBytes(memUsed)} / ${fmtBytes(memTotal)}`, lines: [`${fmtBytes(memTotal - memUsed)} available`, `Containers: ${fmtBytes(containersMem)}`], bullet: bullet(memUsed, memTotal, THRESHOLDS.memory, 'bytes') },
      { id: 'gpu', label: 'GPU', value: gpus.length ? fmtPct(gpuUtil) : '–', subValue: gpus.length ? `${gpus.length} GPU${gpus.length > 1 ? 's' : ''}` : 'None', lines: gpus.length ? [`${gpus.length === 1 ? gpus[0].name : `avg across ${gpus.length} GPUs`}`, `VRAM ${fmtBytes(vramUsed)} / ${fmtBytes(vramTotal)}`] : ['no GPUs in the fleet'], bullet: bullet(gpuUtil, 100, [101, 101], 'percent') },
      { id: 'storage', label: 'Disk', value: fmtPct(pct(disk.used, disk.total)), subValue: `${fmtBytes(disk.used)} / ${fmtBytes(disk.total)}`, lines: [`${fmtBytes(disk.total - disk.used)} free`, `R ${fmtRate(read, true)} · W ${fmtRate(write, true)}`], bullet: bullet(disk.used, disk.total, THRESHOLDS.disk, 'bytes') },
      { id: 'network', label: 'Network', value: fmtRate(rx + tx), subValue: 'throughput', lines: [`↓ ${fmtRate(rx)} in`, `↑ ${fmtRate(tx)} out`], bullet: null },
    ];
  }

  const h = r.host;
  if (!h || !h.online) return placeholders(h ? 'host not reporting' : 'not reported');

  if (!scope.containerId) {
    const busy = busyCores(h);
    const containersCpu = h.containerTotals?.cpuCores ?? sum(running(h), w => w.usage?.cpuCores);
    const containersMem = h.containerTotals?.memoryBytes ?? sum(running(h), w => w.usage?.memoryBytes);
    const gpuUtil = gpuAverage(h);
    const disk = storage(h);
    const v = vram(h);
    const availMem = Math.max(0, (h.memoryTotalBytes ?? 0) - (h.memoryUsedBytes ?? 0));
    return [
      { id: 'cpu', label: 'CPU', value: fmtPct(h.cpuPercent), subValue: `${busy?.toFixed(1)} / ${h.logicalCores} cores`, lines: [`${busy?.toFixed(1)} of ${h.logicalCores} cores active`, `Containers: ${fmtCores(containersCpu)}`], bullet: bullet(busy, h.logicalCores, THRESHOLDS.cpu, 'cores') },
      { id: 'memory', label: 'Memory', value: fmtPct(h.memoryPercent), subValue: `${fmtBytes(h.memoryUsedBytes)} / ${fmtBytes(h.memoryTotalBytes)}`, lines: [`${fmtBytes(availMem)} available${wslVm(h) ? ' in the Docker VM (WSL2)' : ''}`, `Containers: ${fmtBytes(containersMem)}`], bullet: bullet(h.memoryUsedBytes, h.memoryTotalBytes, THRESHOLDS.memory, 'bytes') },
      { id: 'gpu', label: 'GPU', value: h.gpus.length ? fmtPct(gpuUtil) : '–', subValue: h.gpus.length ? `VRAM ${fmtPct(pct(v.used, v.total))}` : 'None', lines: h.gpus.length ? [`${h.gpus.length > 1 ? `${h.gpus.length} GPUs` : h.gpus[0].name}`, `VRAM ${fmtBytes(v.used)} / ${fmtBytes(v.total)}`] : ['no GPU installed'], bullet: bullet(gpuUtil, 100, [101, 101], 'percent') },
      { id: 'storage', label: 'Disk', value: fmtPct(pct(disk.used, disk.total)), subValue: `${fmtBytes(disk.used)} / ${fmtBytes(disk.total)}`, lines: [`${fmtBytes(Math.max(0, disk.total - disk.used))} free`, `R ${fmtRate(h.diskReadBytesPerSecond, true)} · W ${fmtRate(h.diskWriteBytesPerSecond, true)}`], bullet: bullet(disk.used, disk.total, THRESHOLDS.disk, 'bytes') },
      { id: 'network', label: 'Network', value: fmtRate((h.netReceiveBytesPerSecond ?? 0) + (h.netTransmitBytesPerSecond ?? 0)), subValue: 'host interfaces', lines: [`↓ ${fmtRate(h.netReceiveBytesPerSecond)} in`, `↑ ${fmtRate(h.netTransmitBytesPerSecond)} out`], bullet: null },
    ];
  }

  const u = r.container ? usageOf(r.container) : null;
  if (!u) return placeholders(r.container ? `container ${r.container.status === 'offline' ? 'not reporting' : r.container.status}` : 'not reported');
  const cpuCap = u.cpuLimitCores ?? h.logicalCores, memCap = u.memoryLimitBytes ?? h.memoryTotalBytes;
  const hostVramTotal = vram(h).total;
  const hasContainerGpu = h.gpus.length > 0 && (u.gpuPercent != null || u.gpuMemoryBytes != null);
  return [
    { id: 'cpu', label: 'CPU', value: fmtPct(pct(u.cpuCores, cpuCap)), subValue: `${fmtCores(u.cpuCores, true)} / ${cpuCap} cores`, lines: [`${fmtCores(u.cpuCores, true)} of ${u.cpuLimitCores ? `${u.cpuLimitCores}-core limit` : `${h.logicalCores} host cores`}`, `${fmtPct(u.cpuPercentOfHost)} of total host CPU`], bullet: bullet(u.cpuCores, cpuCap, THRESHOLDS.cpu, 'cores') },
    { id: 'memory', label: 'Memory', value: fmtPct(pct(u.memoryBytes, memCap)), subValue: `${fmtBytes(u.memoryBytes)} / ${fmtBytes(memCap)}`, lines: [`${fmtBytes(u.memoryBytes)} of ${u.memoryLimitBytes ? `${fmtBytes(u.memoryLimitBytes)} limit` : 'host memory'}`, `${fmtPct(pct(u.memoryBytes, h.memoryTotalBytes))} of total host RAM`], bullet: bullet(u.memoryBytes, memCap, THRESHOLDS.memory, 'bytes') },
    {
      id: 'gpu',
      label: 'GPU',
      value: hasContainerGpu ? fmtPct(u.gpuPercent) : '–',
      subValue: hasContainerGpu && u.gpuMemoryBytes != null ? `${fmtBytes(u.gpuMemoryBytes)} VRAM` : h.gpus.length ? '0% active' : 'No GPU',
      lines: hasContainerGpu
        ? [`${fmtPct(u.gpuPercent)} of host GPU compute`, `VRAM ${fmtBytes(u.gpuMemoryBytes)} of ${fmtBytes(hostVramTotal)}`]
        : [h.gpus.length ? 'no active GPU processes in container' : 'no GPU on host'],
      bullet: hasContainerGpu ? bullet(u.gpuPercent, 100, [101, 101], 'percent') : null,
    },
    { id: 'storage', label: 'Disk', value: fmtRate(u.ioReadBytesPerSecond + u.ioWriteBytesPerSecond), subValue: 'this container', lines: [`R ${fmtRate(u.ioReadBytesPerSecond)} read`, `W ${fmtRate(u.ioWriteBytesPerSecond)} write`], bullet: null },
    { id: 'network', label: 'Network', value: fmtRate(u.netReceiveBytesPerSecond + u.netTransmitBytesPerSecond), subValue: 'this container', lines: [`↓ ${fmtRate(u.netReceiveBytesPerSecond)} in`, `↑ ${fmtRate(u.netTransmitBytesPerSecond)} out`], bullet: null },
  ];
}

export interface Ref { value: number; label: string; kind: 'capacity' | 'limit' }
export interface TrendSeries { key: string; label: string; color: string; fill: boolean }
export interface Trend { id: string; title: string; subtitle?: string; unit: Unit; series: TrendSeries[]; refs: Ref[]; fixedMax: number | null; empty: string | null; percentOf?: number | null; floor?: number }

export interface TrendData { t: number[]; columns: Record<string, Column>; trends: Trend[] }

export function trends(r: Resolved, tl: Timeline, scope: Scope): TrendData {
  const usage = (key: string, label: string): TrendSeries => ({ key, label, color: T.usage, fill: true });
  const second = (key: string, label: string): TrendSeries => ({ key, label, color: T.second, fill: false });
  const all = (prefix: string) => tl.ids(prefix).map(id => prefix + id);
  const columns: Record<string, Column> = {};
  const io = (prefixRead: string, prefixWrite: string, fleet: boolean) => {
    columns.read = fleet ? tl.sum(all(prefixRead)) : tl.col(prefixRead.replace(':', ''));
    columns.write = fleet ? tl.sum(all(prefixWrite)) : tl.col(prefixWrite.replace(':', ''));
  };

  const disk: Trend = { id: 'disk', title: 'Disk', subtitle: 'Read & write throughput', unit: 'rate', series: [usage('read', 'Read'), second('write', 'Write')], refs: [], fixedMax: null, empty: null };
  const network: Trend = { id: 'network', title: 'Network', subtitle: 'Traffic in & out', unit: 'rate', series: [usage('rx', 'In'), second('tx', 'Out')], refs: [], fixedMax: null, empty: null };

  if (!scope.hostId) {
    const hosts = online(r.hosts);
    const cores = sum(hosts, h => h.logicalCores), memTotal = sum(hosts, h => h.memoryTotalBytes);
    const vramTotal = sum(hosts, h => vram(h).total);
    columns.cpu = tl.sum(all('cpu:'));
    columns.wcpu = tl.sum(all('wcpu:'));
    columns.mem = tl.sum(all('mem:'));
    columns.gpu = tl.mean(all('gpu:').filter(k => tl.col(k).some(v => v != null)));
    columns.vram = vramTotal ? tl.sum(all('vram:')).map(v => (v == null ? null : (100 * v) / vramTotal)) : [];
    io('read:', 'write:', true);
    columns.rx = tl.sum(all('rx:'));
    columns.tx = tl.sum(all('tx:'));
    const hasWcpu = columns.wcpu.some(v => v != null);
    return {
      t: tl.t, columns, trends: [
        { id: 'cpu', title: 'CPU', subtitle: 'Active cores across fleet vs containers', unit: 'cores', series: hasWcpu ? [usage('cpu', 'Host total'), second('wcpu', 'Containers')] : [usage('cpu', 'In use')], refs: cores ? [{ value: cores, label: `capacity ${cores} cores`, kind: 'capacity' }] : [], fixedMax: null, empty: null, percentOf: cores },
        { id: 'memory', title: 'Memory', subtitle: 'Physical memory consumption', unit: 'bytes', series: [usage('mem', 'In use')], refs: memTotal ? [{ value: memTotal, label: `capacity ${fmtBytes(memTotal)}`, kind: 'capacity' }] : [], fixedMax: null, empty: null, percentOf: memTotal },
        { id: 'gpu', title: 'GPU & VRAM', subtitle: 'Compute utilization & VRAM %', unit: 'percent', series: [usage('gpu', 'Compute'), second('vram', 'VRAM')], refs: [], fixedMax: 100, empty: vramTotal ? null : 'No GPUs in the fleet.' },
        disk, network,
      ],
    };
  }

  const h = r.host;
  const cores = h?.logicalCores ?? 0;

  if (!scope.containerId) {
    columns.cpu = tl.col('cpu').map(v => (v == null ? null : (v / 100) * cores));
    const wcpuPct = tl.sum(all('wcpu:'));
    columns.wcpu = wcpuPct.map(v => (v == null ? null : (v / 100) * cores));
    columns.mem = tl.col('mem');
    columns.wmem = tl.sum(all('wmem:'));
    columns.gpu = tl.mean(all('gpu:'));
    columns.vram = tl.mean(all('vram:'));
    io('read:', 'write:', false);
    columns.rx = tl.col('rx');
    columns.tx = tl.col('tx');
    const memTotal = h?.memoryTotalBytes ?? 0;
    const hasContainers = all('wcpu:').length > 0;
    return {
      t: tl.t, columns, trends: [
        { id: 'cpu', title: 'CPU', subtitle: 'Host total vs containers share', unit: 'cores', series: hasContainers ? [usage('cpu', 'Host total'), second('wcpu', 'Containers')] : [usage('cpu', 'In use')], refs: cores ? [{ value: cores, label: `capacity ${cores} cores`, kind: 'capacity' }] : [], fixedMax: null, empty: null, percentOf: cores },
        { id: 'memory', title: 'Memory', subtitle: 'Host total vs containers share', unit: 'bytes', series: hasContainers ? [usage('mem', 'Host total'), second('wmem', 'Containers')] : [usage('mem', 'In use')], refs: memTotal ? [{ value: memTotal, label: `${h && wslVm(h) ? 'VM size' : 'capacity'} ${fmtBytes(memTotal)}`, kind: 'capacity' }] : [], fixedMax: null, empty: null, percentOf: memTotal },
        { id: 'gpu', title: 'GPU & VRAM', subtitle: 'Compute utilization & VRAM %', unit: 'percent', series: [usage('gpu', 'Compute'), second('vram', 'VRAM')], refs: [], fixedMax: 100, empty: h?.gpus.length ? null : 'This host has no GPU.' },
        disk, network,
      ],
    };
  }

  const u = r.container ? usageOf(r.container) : null;
  const hostVramTotal = h ? vram(h).total : 0;
  columns.cpu = tl.col('cpu');
  columns.mem = tl.col('mem');
  columns.gpu = tl.col('gpu');
  columns.vram = hostVramTotal ? tl.col('vram').map(v => (v == null ? null : (100 * v) / hostVramTotal)) : [];
  io('read:', 'write:', false);
  columns.rx = tl.col('rx');
  columns.tx = tl.col('tx');
  const limit = (value: number | null | undefined, label: string): Ref[] => (value ? [{ value, label, kind: 'limit' }] : []);
  const hasGpuHistory = Boolean(h?.gpus.length) && (columns.gpu.some(v => v != null) || u?.gpuPercent != null);
  return {
    t: tl.t, columns, trends: [
      { id: 'cpu', title: 'CPU', subtitle: 'Container core usage', unit: 'cores', series: [usage('cpu', 'In use')], refs: limit(u?.cpuLimitCores, `limit ${u?.cpuLimitCores} cores`), fixedMax: null, empty: null, percentOf: u?.cpuLimitCores ?? cores },
      { id: 'memory', title: 'Memory', subtitle: 'Container memory', unit: 'bytes', series: [usage('mem', 'In use')], refs: limit(u?.memoryLimitBytes, `limit ${fmtBytes(u?.memoryLimitBytes)}`), fixedMax: null, empty: null, percentOf: u?.memoryLimitBytes ?? h?.memoryTotalBytes },
      { id: 'gpu', title: 'GPU & VRAM', subtitle: 'GPU use & VRAM share', unit: 'percent', series: [usage('gpu', 'Compute'), second('vram', 'VRAM %')], refs: [], fixedMax: 100, empty: hasGpuHistory ? null : h?.gpus.length ? 'No GPU processes reported in this container.' : 'This host has no GPU.' },
      disk, network,
    ],
  };
}
