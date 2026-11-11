import { useMemo, useState } from 'react';
import type { Scope } from '../data';
import { fmtBytes, fmtCores, fmtPct, fmtRate, pct, sum } from '../format';
import { busyCores, fullNameOf, nameOf, online, usageOf } from '../models';
import { levelColor, levelOf, THRESHOLDS } from '../theme';
import type { ContainerUsage, HostSummary, ContainerState } from '../types';
import { IconChevronRight, IconSearch } from './Icons';
import { StatusDot } from './ui';

type SortKey = 'name' | 'cpu' | 'memory' | 'gpu' | 'disk' | 'net';

export function ProcessTree({ hosts, scope, onScope }: {
  hosts: HostSummary[];
  scope: Scope;
  onScope: (s: Scope) => void;
}) {
  const [sortBy, setSortBy] = useState<SortKey>('name');
  const [sortAsc, setSortAsc] = useState(true);
  const [filter, setFilter] = useState('');
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});

  const handleSort = (key: SortKey) => {
    if (sortBy === key) {
      setSortAsc(!sortAsc);
    } else {
      setSortBy(key);
      setSortAsc(key === 'name');
    }
  };

  const fleetTotals = useMemo(() => {
    const on = online(hosts);
    const totalCores = sum(on, h => h.logicalCores);
    const usedCores = sum(on, busyCores);
    const totalMem = sum(on, h => h.memoryTotalBytes);
    const usedMem = sum(on, h => h.memoryUsedBytes);
    const gpus = on.flatMap(h => h.gpus);
    const gpuPct = gpus.length ? sum(gpus, g => g.utilizationPercent) / gpus.length : null;
    const vramUsed = sum(gpus, g => g.memoryUsedBytes);
    const diskRate = sum(on, h => (h.diskReadBytesPerSecond ?? 0) + (h.diskWriteBytesPerSecond ?? 0));
    const netUp = sum(on, h => h.netTransmitBytesPerSecond);
    const netDown = sum(on, h => h.netReceiveBytesPerSecond);
    const totalContainers = hosts.reduce((acc, h) => acc + h.containers.length, 0);
    const activeContainers = hosts.reduce((acc, h) => acc + h.containers.filter(w => w.status === 'online').length, 0);
    return {
      cpuPct: pct(usedCores, totalCores),
      usedCores,
      totalCores,
      memPct: pct(usedMem, totalMem),
      usedMem,
      totalMem,
      gpuPct,
      vramUsed,
      hasGpus: gpus.length > 0,
      diskRate,
      netUp,
      netDown,
      activeContainers,
      totalContainers,
    };
  }, [hosts]);

  const sortedHosts = useMemo(() => {
    const dir = sortAsc ? 1 : -1;
    const hostVal = (h: HostSummary): number => {
      if (!h.online) return -1;
      switch (sortBy) {
        case 'cpu': return h.cpuPercent ?? 0;
        case 'memory': return h.memoryPercent ?? 0;
        case 'gpu': return h.gpus.length ? sum(h.gpus, g => g.utilizationPercent) / h.gpus.length : -1;
        case 'disk': return (h.diskReadBytesPerSecond ?? 0) + (h.diskWriteBytesPerSecond ?? 0);
        case 'net': return (h.netReceiveBytesPerSecond ?? 0) + (h.netTransmitBytesPerSecond ?? 0);
        default: return 0;
      }
    };
    return [...hosts].sort((a, b) => {
      if (sortBy === 'name') return dir * a.displayName.localeCompare(b.displayName);
      return dir * (hostVal(a) - hostVal(b)) || a.displayName.localeCompare(b.displayName);
    });
  }, [hosts, sortBy, sortAsc]);

  const sortContainers = (containers: ContainerState[], h: HostSummary) => {
    const dir = sortAsc ? 1 : -1;
    const statusRank = (w: ContainerState) => (w.status === 'online' ? 0 : w.status === 'offline' ? 1 : 2);
    const containerVal = (w: ContainerState): number => {
      if (w.status !== 'online' || !w.usage) return -1;
      switch (sortBy) {
        case 'cpu': return cpuOf(w.usage) ?? 0;
        case 'memory': return memoryOf(w.usage, h) ?? 0;
        case 'gpu': return -1;
        case 'disk': return w.usage.ioReadBytesPerSecond + w.usage.ioWriteBytesPerSecond;
        case 'net': return w.usage.netReceiveBytesPerSecond + w.usage.netTransmitBytesPerSecond;
        default: return 0;
      }
    };
    return [...containers].sort((a, b) => {
      const rankDiff = statusRank(a) - statusRank(b);
      if (rankDiff !== 0) return rankDiff;
      if (sortBy === 'name') return dir * nameOf(a).localeCompare(nameOf(b));
      const valDiff = containerVal(a) - containerVal(b);
      if (Math.abs(valDiff) > 1e-6) return dir * valDiff;
      return a.containerId.localeCompare(b.containerId);
    });
  };

  const q = filter.trim().toLowerCase();
  const isFleetSelected = !scope.hostId;

  const firstMatch = (): Scope | null => {
    for (const h of sortedHosts) {
      const container = sortContainers(h.containers.filter(w => (w.containerId.toLowerCase().includes(q) || fullNameOf(w).toLowerCase().includes(q))), h)[0];
      if (container) return { hostId: h.hostId, containerId: container.containerId };
      if (h.displayName.toLowerCase().includes(q) || h.hostId.toLowerCase().includes(q)) return { hostId: h.hostId, containerId: null };
    }
    return null;
  };

  const cols =
    'grid-cols-[minmax(0,1fr)_64px_72px] gap-x-3 @min-[560px]:grid-cols-[minmax(0,1fr)_64px_72px_80px] @min-[740px]:grid-cols-[minmax(220px,1fr)_76px_84px_76px_80px_80px] @min-[740px]:gap-x-5';

  return (
    <aside className="@container flex max-h-[70vh] flex-col bg-surface">
      <div className="border-b border-line p-2">
        <div className="relative">
          <IconSearch size={12} className="pointer-events-none absolute left-2 top-1/2 -translate-y-1/2 text-muted" />
          <input
            type="text"
            autoFocus
            value={filter}
            onChange={e => setFilter(e.target.value)}
            onKeyDown={e => {
              if (e.key !== 'Enter' || !q) return;
              const match = firstMatch();
              if (match) onScope(match);
            }}
            placeholder="Find a host or container…"
            className="h-7 w-full rounded border border-line bg-bg pl-6 pr-2 text-xs text-text placeholder:text-muted outline-none focus:border-line-2"
          />
        </div>
      </div>

      <div className={`grid ${cols} items-center border-b border-line bg-surface-2/60 px-3 py-1.5 text-[11px] font-medium text-muted select-none`}>
        <button type="button" onClick={() => handleSort('name')} className="text-left hover:text-text">
          Name {sortBy === 'name' && (sortAsc ? '↑' : '↓')}
        </button>
        <button type="button" onClick={() => handleSort('cpu')} className="text-right hover:text-text">
          CPU {sortBy === 'cpu' && (sortAsc ? '↑' : '↓')}
        </button>
        <button type="button" onClick={() => handleSort('memory')} className="text-right hover:text-text">
          Memory {sortBy === 'memory' && (sortAsc ? '↑' : '↓')}
        </button>
        <button type="button" onClick={() => handleSort('gpu')} className={`${WIDE} text-right hover:text-text`}>
          GPU {sortBy === 'gpu' && (sortAsc ? '↑' : '↓')}
        </button>
        <button type="button" onClick={() => handleSort('disk')} className={`${WIDE} text-right hover:text-text`}>
          Disk {sortBy === 'disk' && (sortAsc ? '↑' : '↓')}
        </button>
        <button type="button" onClick={() => handleSort('net')} className={`${MID} text-right hover:text-text`}>
          Net {sortBy === 'net' && (sortAsc ? '↑' : '↓')}
        </button>
      </div>

      <div className="flex-1 overflow-y-auto divide-y divide-line/50 text-xs">
        <div
          role="button"
          tabIndex={0}
          onClick={() => onScope({ hostId: null, containerId: null })}
          onKeyDown={e => {
            if (e.key === 'Enter' || e.key === ' ') {
              e.preventDefault();
              onScope({ hostId: null, containerId: null });
            }
          }}
          className={`grid ${cols} items-center px-3 py-2 cursor-pointer transition-colors ${
            isFleetSelected ? 'bg-usage/15 text-text' : 'hover:bg-surface-2/60 text-text-2'
          }`}
        >
          <div className="min-w-0 pr-2">
            <div className="truncate font-semibold text-text">All Hosts ({hosts.length})</div>
            <div className="truncate text-[10px] text-muted">
              {fleetTotals.activeContainers}/{fleetTotals.totalContainers} containers active
            </div>
          </div>
          <MetricCell
            primary={fmtPct(fleetTotals.cpuPct, true)}
            secondary={`${fleetTotals.usedCores.toFixed(0)}/${fleetTotals.totalCores}c`}
            pctValue={fleetTotals.cpuPct}
            thresholds={THRESHOLDS.cpu}
          />
          <MetricCell
            primary={fmtPct(fleetTotals.memPct, true)}
            secondary={fmtBytes(fleetTotals.usedMem, 0)}
            pctValue={fleetTotals.memPct}
            thresholds={THRESHOLDS.memory}
          />
          {fleetTotals.hasGpus ? (
            <MetricCell
              primary={fmtPct(fleetTotals.gpuPct, true)}
              secondary={fmtBytes(fleetTotals.vramUsed, 0)}
              pctValue={fleetTotals.gpuPct}
              thresholds={[101, 101]}
              className={WIDE}
            />
          ) : (
            <div className={`${WIDE} text-right font-mono text-[11px] text-muted`}>–</div>
          )}
          <div className={`${WIDE} text-right font-mono text-[11px] text-text-2`}>
            {fmtRate(fleetTotals.diskRate, true)}
          </div>
          <NetCell up={fleetTotals.netUp} down={fleetTotals.netDown} />
        </div>

        {sortedHosts.map(h => {
          const matchingContainers = q
            ? h.containers.filter(w => (w.containerId.toLowerCase().includes(q) || fullNameOf(w).toLowerCase().includes(q)))
            : h.containers;
          const hostMatches = !q || h.displayName.toLowerCase().includes(q) || h.hostId.toLowerCase().includes(q);
          if (!hostMatches && matchingContainers.length === 0) return null;

          const isHostSelected = scope.hostId === h.hostId && !scope.containerId;
          const isCollapsed = !q && Boolean(collapsed[h.hostId]);
          const containers = sortContainers(matchingContainers, h);
          const hostDisk = (h.diskReadBytesPerSecond ?? 0) + (h.diskWriteBytesPerSecond ?? 0);
          const hostGpuPct = h.gpus.length ? sum(h.gpus, g => g.utilizationPercent) / h.gpus.length : null;
          const hostVramUsed = sum(h.gpus, g => g.memoryUsedBytes);

          return (
            <div key={h.hostId}>
              <div
                role="button"
                tabIndex={0}
                onClick={() => onScope({ hostId: h.hostId, containerId: null })}
                onKeyDown={e => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    onScope({ hostId: h.hostId, containerId: null });
                  }
                }}
                className={`grid ${cols} items-center bg-surface-2/30 px-3 py-2 cursor-pointer transition-colors ${
                  isHostSelected ? 'bg-usage/15 text-text' : 'hover:bg-surface-2/80'
                }`}
              >
                <div className="flex min-w-0 items-center gap-1.5 pr-2">
                  <button
                    type="button"
                    aria-label={`Expand ${h.displayName}`}
                    onClick={e => {
                      e.stopPropagation();
                      setCollapsed(prev => ({ ...prev, [h.hostId]: !prev[h.hostId] }));
                    }}
                    className="rounded p-0.5 text-muted hover:bg-surface-3 hover:text-text"
                  >
                    <IconChevronRight
                      size={12}
                      className={`transition-transform ${!isCollapsed ? 'rotate-90' : ''}`}
                    />
                  </button>
                  <StatusDot status={h.online} />
                  <div className="min-w-0">
                    <div className="truncate font-medium text-text">{h.displayName}</div>
                    <div className="truncate text-[10px] text-muted">
                      {h.online
                        ? [
                            h.logicalCores && `${h.logicalCores} cores`,
                            h.memoryTotalBytes && fmtBytes(h.memoryTotalBytes, 0),
                            h.gpus.length && `${h.gpus.length} GPU`,
                          ]
                            .filter(Boolean)
                            .join(' · ')
                        : 'Not reporting'}
                    </div>
                  </div>
                </div>

                {h.online ? (
                  <>
                    <MetricCell
                      primary={fmtPct(h.cpuPercent, true)}
                      secondary={`${busyCores(h)?.toFixed(1) ?? 0}c`}
                      pctValue={h.cpuPercent}
                      thresholds={THRESHOLDS.cpu}
                    />
                    <MetricCell
                      primary={fmtPct(h.memoryPercent, true)}
                      secondary={fmtBytes(h.memoryUsedBytes, 0)}
                      pctValue={h.memoryPercent}
                      thresholds={THRESHOLDS.memory}
                    />
                    {h.gpus.length ? (
                      <MetricCell
                        primary={fmtPct(hostGpuPct, true)}
                        secondary={fmtBytes(hostVramUsed, 0)}
                        pctValue={hostGpuPct}
                        thresholds={[101, 101]}
                        className={WIDE}
                      />
                    ) : (
                      <div className={`${WIDE} text-right font-mono text-[11px] text-muted`}>–</div>
                    )}
                    <div className={`${WIDE} text-right font-mono text-[11px] text-text-2`}>
                      {fmtRate(hostDisk, true)}
                    </div>
                    <NetCell up={h.netTransmitBytesPerSecond} down={h.netReceiveBytesPerSecond} />
                  </>
                ) : (
                  <div className="col-[2/-1] text-right text-[11px] text-crit">Not reporting</div>
                )}
              </div>

              {!isCollapsed &&
                containers.map(w => {
                  const isContainerSelected = scope.hostId === h.hostId && scope.containerId === w.containerId;
                  const u = usageOf(w);
                  const wCpuPct = u ? cpuOf(u) : null;
                  const wMemPct = u ? memoryOf(u, h) : null;
                  const wDisk = u ? u.ioReadBytesPerSecond + u.ioWriteBytesPerSecond : null;

                  return (
                    <div
                      key={w.containerId}
                      role="button"
                      tabIndex={0}
                      onClick={() =>
                        onScope(
                          isContainerSelected
                            ? { hostId: h.hostId, containerId: null }
                            : { hostId: h.hostId, containerId: w.containerId }
                        )
                      }
                      onKeyDown={e => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault();
                          onScope(
                            isContainerSelected
                              ? { hostId: h.hostId, containerId: null }
                              : { hostId: h.hostId, containerId: w.containerId }
                          );
                        }
                      }}
                      className={`grid ${cols} items-center px-3 py-1.5 cursor-pointer transition-colors ${
                        isContainerSelected ? 'bg-usage/15 text-text' : 'hover:bg-surface-2/50'
                      }`}
                    >
                      <div className="flex min-w-0 items-center gap-1.5 pl-9 pr-2" title={`${fullNameOf(w)} (${w.containerId})`}>
                        <StatusDot status={w.status} />
                        <div className="min-w-0">
                          <div
                            className={`truncate text-[11px] ${
                              w.status === 'online' ? 'text-text' : w.status === 'offline' ? 'text-crit' : 'text-muted'
                            }`}
                          >
                            {nameOf(w)}
                          </div>
                          {w.group && <div className="truncate text-[10px] text-muted">{w.group}</div>}
                        </div>
                      </div>

                      {u ? (
                        <>
                          <MetricCell
                            primary={fmtPct(wCpuPct, false)}
                            secondary={u.cpuLimitCores ? `${fmtCores(u.cpuCores, true)}/${u.cpuLimitCores}c` : `${fmtCores(u.cpuCores, true)}c`}
                            pctValue={wCpuPct}
                            thresholds={THRESHOLDS.cpu}
                          />
                          <MetricCell
                            primary={fmtBytes(u.memoryBytes, 0)}
                            secondary={fmtPct(wMemPct, false)}
                            pctValue={wMemPct}
                            thresholds={THRESHOLDS.memory}
                          />
                          {h.gpus.length && (u.gpuPercent != null || u.gpuMemoryBytes != null) ? (
                            <MetricCell
                              primary={fmtPct(u.gpuPercent, true)}
                              secondary={u.gpuMemoryBytes ? fmtBytes(u.gpuMemoryBytes, 0) : '0 B'}
                              pctValue={u.gpuPercent}
                              thresholds={[101, 101]}
                              className={WIDE}
                            />
                          ) : (
                            <div className={`${WIDE} text-right font-mono text-[11px] text-muted`}>–</div>
                          )}
                          <div className={`${WIDE} text-right font-mono text-[11px] text-muted`}>
                            {fmtRate(wDisk, true)}
                          </div>
                          <NetCell up={u.netTransmitBytesPerSecond} down={u.netReceiveBytesPerSecond} />
                        </>
                      ) : (
                        <div className="col-[2/-1] text-right text-[11px] text-muted">{w.status}</div>
                      )}
                    </div>
                  );
                })}
            </div>
          );
        })}
      </div>
    </aside>
  );
}

const WIDE = 'hidden @min-[740px]:block';
const MID = 'hidden @min-[560px]:block';

const cpuOf = (u: ContainerUsage) => (u.cpuLimitCores ? pct(u.cpuCores, u.cpuLimitCores) : u.cpuPercentOfHost);
const memoryOf = (u: ContainerUsage, h: HostSummary) => pct(u.memoryBytes, u.memoryLimitBytes ?? h.memoryTotalBytes);

function NetCell({ up, down }: { up: number | null | undefined; down: number | null | undefined }) {
  return (
    <div className={`${MID} text-right font-mono text-[11px] leading-tight text-text-2`}>
      <div>{fmtRate(up, true)} <span className="text-muted">↑</span></div>
      <div>{fmtRate(down, true)} <span className="text-muted">↓</span></div>
    </div>
  );
}

function MetricCell({ primary, secondary, pctValue, thresholds, className = '' }: {
  primary: string;
  secondary?: string;
  pctValue: number | null | undefined;
  thresholds: readonly [number, number];
  className?: string;
}) {
  const lvl = levelOf(pctValue, thresholds);
  const w = Math.max(0, Math.min(100, pctValue ?? 0));
  return (
    <div className={`pl-1.5 text-right ${className}`}>
      <div className="font-mono text-[11px] font-medium" style={lvl === 'ok' ? undefined : { color: levelColor(lvl) }}>
        {primary}
      </div>
      {secondary && <div className="font-mono text-[10px] text-muted">{secondary}</div>}
      <div className="mt-0.5 h-0.5 w-full overflow-hidden rounded-full bg-surface-3">
        <div className="h-full" style={{ width: `${w}%`, background: levelColor(lvl) }} />
      </div>
    </div>
  );
}
