import { lazy, type ReactNode, Suspense, useEffect, useMemo, useState } from 'react';
import { type Resolved, type Scope, type View, usePolled, useStored } from '../data';
import { fmtBytes, fmtCores, fmtDuration, fmtPct, fmtRate, pct, sum } from '../format';
import { busyCores, fullNameOf, nameOf, type Card, containerVolume, fmt, type Ref, type Trend, type TrendData, type Unit, usageOf, wslVm } from '../models';
import { HEAT_RAMP, heat, PALETTE, T } from '../theme';
import type { Column } from '../timeline';
import type { CaptureInfo, ContainerDetail, HostEvent, HostSample, HostSnapshot, HostSummary, NetworkInterface, ProcessUsage, ProcessWindow, SeriesHistory, ContainerNetwork, ContainerProcesses, ContainerProcessWindow, ContainerState } from '../types';
import { IconCpu, IconEvents, IconGpu, IconMemory, IconNetwork, IconStorage, IconTopology } from './Icons';
import { type ChartRow, chartRows, liveContainers, PerfTile, timeTicks } from './PerformanceWorkspace';
import { Badge, type Col, Count, Facts, Panel, Table } from './Table';
import { PacketViewer } from './PacketViewer';
import { StatusDot } from './ui';

const NetworkMap = lazy(() => import('./FleetMap').then(m => ({ default: m.NetworkMap })));
import { type Help, HELP, overviewHelp } from '../help';

interface Props {
  view: View;
  r: Resolved;
  scope: Scope;
  onScope: (s: Scope) => void;
  onOpen: (view: View) => void;
  cards: Card[];
  data: TrendData;
  detail?: HostSample;
  bucketSeconds: number;
  version: number;
  start: number;
  end: number;
  range: number;
}

interface Ctx extends Props {
  h: HostSummary;
  w: ContainerState | null;
  hist: SeriesHistory | null;
  hostHist: SeriesHistory | null;
  snapshot: HostSnapshot | undefined;
  containers: ContainerState[];
  processes: ContainerProcesses[] | null;
  processWindow: ContainerProcessWindow[] | null;
  processesAt: ContainerProcesses[] | null;
  processMode: ProcessMode;
  onProcessMode: (mode: ProcessMode) => void;
  network: ContainerNetwork[] | null;
  events: HostEvent[] | null;
  frame: Frame;
}

interface Frame { start: number; end: number; range: number; ticks: number[]; hover: number | null; onHover: (t: number | null) => void }

export function DetailPage(props: Props) {
  const { view, r, scope, range } = props;
  const [hover, setHover] = useState<number | null>(null);
  const h = r.host;
  const every = range <= 300 ? 2000 : range <= 900 ? 5000 : range <= 3600 ? 20000 : range <= 21600 ? 60000 : 120000;
  const hostHist = usePolled<SeriesHistory>(h ? `api/hosts/${encodeURIComponent(h.hostId)}/detail?seconds=${range}` : null, every);
  const containerHist = usePolled<SeriesHistory>(scope.containerId ? `api/containers/${encodeURIComponent(scope.containerId)}/detail?seconds=${range}` : null, every);
  const withProcesses = Boolean(h && (view === 'cpu' || view === 'memory' || view === 'gpu' || view === 'disk'));
  const processes = usePolled<ContainerProcesses[]>(withProcesses ? `api/hosts/${encodeURIComponent(h!.hostId)}/processes` : null, 2000);
  const network = usePolled<ContainerNetwork[]>(h && view === 'network' ? `api/hosts/${encodeURIComponent(h.hostId)}/network` : null, 2000);
  const events = usePolled<HostEvent[]>(h && view === 'events' ? `api/hosts/${encodeURIComponent(h.hostId)}/events?seconds=${range}` : null, 5000);
  const ticks = useMemo(() => timeTicks(props.start, props.end, range), [props.start, props.end, range]);
  const [processMode, setProcessMode] = useStored<ProcessMode>('sounding:processes', 'window', v => v === 'window' || v === 'now');
  const processWindow = usePolled<ContainerProcessWindow[]>(
    withProcesses && processMode === 'window' ? `api/hosts/${encodeURIComponent(h!.hostId)}/processes/window?seconds=${range}` : null, range <= 3600 ? 5000 : every);
  const at = useDebounced(withProcesses && hover != null ? Math.round(hover / 5000) * 5000 : null, 150);
  const processesAt = useFetched<ContainerProcesses[]>(withProcesses && at != null ? `api/hosts/${encodeURIComponent(h!.hostId)}/processes/at?t=${at}` : null);

  if (!h) return <Empty>This host is not reporting.</Empty>;

  const ctx: Ctx = {
    ...props, h, w: r.container, hist: scope.containerId ? containerHist : hostHist, hostHist, snapshot: props.detail?.snapshot,
    containers: liveContainers(h), processes, processWindow, processesAt: hover != null ? processesAt : null, processMode, onProcessMode: setProcessMode,
    network: network?.filter(x => !scope.containerId || x.containerId === scope.containerId) ?? null,
    events: events?.filter(x => !scope.containerId || x.containerId === scope.containerId) ?? null,
    frame: { start: props.start, end: props.end, range, ticks, hover, onHover: setHover },
  };

  return (
    <main className="grid min-h-0 flex-1 auto-rows-min grid-cols-1 gap-2.5 overflow-y-auto p-2.5 md:grid-cols-2">
      {view === 'cpu' && <CpuDetail {...ctx} />}
      {view === 'memory' && <MemoryDetail {...ctx} />}
      {view === 'gpu' && <GpuDetail {...ctx} />}
      {view === 'disk' && <DiskDetail {...ctx} />}
      {view === 'network' && <NetworkDetail {...ctx} />}
      {view === 'events' && <EventsDetail {...ctx} />}
    </main>
  );
}

function CpuDetail(c: Ctx) {
  const { h, scope, hist, containers, snapshot } = c;
  const u = c.w ? usageOf(c.w) : null;
  const d = u?.detail;
  const icon = <IconCpu size={14} />;
  const atLimit = containers.filter(x => (x.usage?.detail?.throttling?.throttledPercent ?? 0) > 0);
  const limits = containers.filter(x => x.usage?.cpuLimitCores);
  const load = snapshot?.system.loadAverage;
  const activity = snapshot?.cpu?.activity;

  return (
    <>
      <MainChart c={c} id="cpu" icon={icon} />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="CPU pressure" help={HELP.cpuPressure} unit="percent"
        info={['Time tasks waited for a CPU']}
        series={scope.containerId
          ? [{ label: 'Some waiting', values: col(hist, 'cpuPressure') }, { label: 'All waiting', values: col(hist, 'cpuPressureFull') }]
          : [{ label: 'Some waiting', values: col(hist, 'cpuPressure') }]}
      />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="CPU throttling" help={HELP.cpuThrottling} unit="ms"
        info={[scope.containerId ? `Held back at its ${u?.cpuLimitCores ?? ''} core limit` : 'Containers held back at their CPU limits']}
        series={scope.containerId
          ? [{ label: 'Throttled', values: col(hist, 'throttledMs') }]
          : [{ label: 'Containers', values: across(hist, 'throttledMs', 'sum'), color: T.second, fill: false }]}
        empty={scope.containerId && u && !d?.throttling ? 'No CPU limit, so nothing to throttle.' : undefined}
      />
      <Panel icon={icon} title="Limits" help={HELP.cpuLimits} subtitle={scope.containerId ? 'This container' : 'The host and its containers'}>
        <Facts rows={scope.containerId ? [
          ['CPU limit', u?.cpuLimitCores ? `${u.cpuLimitCores} cores` : 'none'],
          ['In use', u ? `${fmtCores(u.cpuCores)}${u.cpuLimitCores ? ` (${fmtPct(pct(u.cpuCores, u.cpuLimitCores))} of the limit)` : ''}` : '–'],
          ['Throttled now', d?.throttling ? `${fmtPct(d.throttling.throttledPercent)} of periods, ${d.throttling.throttledMillisecondsPerSecond.toFixed(1)} ms/s` : 'no limit', (d?.throttling?.throttledPercent ?? 0) > 0],
          ['Throttled periods since start', d?.throttling ? d.throttling.throttledPeriodsTotal.toLocaleString() : '–'],
          ['CPU pressure now', fmtPct(d?.pressure?.cpuSome)],
          ['Allowed cores', d?.cpuset ? `${d.cpuset} (${parseCpuset(d.cpuset)?.size ?? 0} of ${h.logicalCores ?? '?'})` : '–'],
          ['Tasks (processes and threads)', d?.tasks != null ? `${d.tasks}${d.tasksLimit ? ` of ${d.tasksLimit}` : ', no limit'}` : '–'],
        ] : [
          ['Logical cores', String(h.logicalCores ?? '–')],
          ['In use', h.online ? `${fmtCores(busyCores(h))} (${fmtPct(h.cpuPercent)})` : '–'],
          ['Load average (1, 5, 15 min)', load ? load.map(v => v.toFixed(2)).join(' / ') : '–'],
          ['CPU pressure now', fmtPct(snapshot?.pressure?.cpuSome)],
          ['Containers’ CPU limits', `${sum(limits, x => x.usage?.cpuLimitCores)} cores across ${limits.length} containers${containers.length > limits.length ? `, ${containers.length - limits.length} unlimited` : ''}`],
          ['Containers at their limit now', `${atLimit.length} of ${containers.length}`, atLimit.length > 0],
          ['Containers’ tasks', String(sum(containers, x => x.usage?.detail?.tasks))],
          ['Threads running / waiting on I/O now', activity ? `${activity.running} / ${activity.blocked}` : NA, (activity?.running ?? 0) > (h.logicalCores ?? Infinity)],
          ['Context switches now', activity?.contextSwitchesPerSecond != null ? fmt('perSecond', activity.contextSwitchesPerSecond) : NA],
        ]} />
      </Panel>
      <Panel icon={icon} title="Cores" help={HELP.cores} wide subtitle={coresSubtitle(c, scope.containerId ? d?.cpuset : null)}>
        <CoreGrid cores={snapshot?.cpu?.coreUsagePercent ?? []} allowed={parseCpuset(scope.containerId ? d?.cpuset : null)} />
      </Panel>
      {!scope.containerId && (
        <DetailChart
          frame={c.frame} hist={hist} icon={icon} label="CPU time split" help={HELP.cpuSplit} unit="percent" wide
          info={['Time outside the applications’ own code']}
          series={[
            { label: 'System', values: col(hist, 'cpuSystem') },
            { label: 'I/O wait', values: col(hist, 'cpuIoWait') },
            { label: 'Interrupts', values: col(hist, 'cpuIrq'), color: PALETTE[2], fill: false },
            { label: 'Stolen by VM', values: col(hist, 'cpuSteal'), color: PALETTE[3], fill: false },
          ]}
        />
      )}
      {!scope.containerId && (
        <DetailChart
          frame={c.frame} hist={hist} icon={icon} label="CPU queue" help={HELP.runQueue} unit="count"
          info={['Threads that want a CPU']}
          series={[{ label: 'Running', values: col(hist, 'running') }, { label: 'Waiting on I/O', values: col(hist, 'blocked') }]}
          refs={h.logicalCores ? [{ value: h.logicalCores, label: `${h.logicalCores} cores`, kind: 'capacity' }] : []}
          empty={snapshot && !activity ? `${NA}: this kernel doesn’t report it.` : undefined}
        />
      )}
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Processes started" help={HELP.processesStarted} unit="perSecond" wide={!!scope.containerId}
        info={[scope.containerId ? 'Processes and threads' : `Context switches: ${fmt('perSecond', activity?.contextSwitchesPerSecond)}`]}
        series={scope.containerId
          ? [{ label: 'This container', values: col(hist, 'tasksStarted') }]
          : [{ label: 'Host total', values: col(hist, 'processesStarted') }, { label: 'Containers', values: across(hist, 'tasksStarted', 'sum') }]}
        empty={scope.containerId && d && d.tasksStartedPerSecond == null && !col(hist, 'tasksStarted')?.some(v => v != null)
          ? `${NA}: this container shares the host’s process IDs (pid: host), so its own can’t be counted apart.`
          : undefined}
      />
      {!scope.containerId && <Temperatures c={c} icon={icon} />}
      <ProcessPanel c={c} sort={p => p.cpuCores} title="Processes by CPU" cols={CPU_COLS} window={CPU_WINDOW} />
    </>
  );
}

function Temperatures({ c, icon }: { c: Ctx; icon: ReactNode }) {
  const sensors = c.snapshot?.temperatures;
  const missing = !c.snapshot ? undefined
    : sensors == null ? `${NA}: the machine’s sensors couldn’t be read.`
    : sensors.length === 0 ? `${NA}: this machine exposes no temperature sensors${wslVm(c.h) ? ' (WSL2 is a virtual machine)' : ' (virtual machines usually don’t)'}`
    : undefined;
  const hottest = [...(sensors ?? [])].sort((a, b) => b.celsius - a.celsius).slice(0, 6);
  const name = (s: { source: string; label: string }) => `${s.source} ${s.label}`;
  if (missing) {
    return (
      <Panel icon={icon} title="Temperatures" help={HELP.temperatures} wide subtitle="CPU, drives and board">
        <div className="py-3 text-center text-muted">{missing}</div>
      </Panel>
    );
  }

  return (
    <>
      <DetailChart
        frame={c.frame} hist={c.hostHist} icon={icon} label="Temperatures" help={HELP.temperatures} unit="celsius"
        info={[hottest.length ? `Hottest: ${name(hottest[0])}` : 'CPU, drives and board']}
        series={hottest.map((s, i) => ({ label: name(s), values: col(c.hostHist, `temp:${name(s)}`), color: i === 0 ? T.usage : PALETTE[i % PALETTE.length], fill: i === 0 }))}
        refs={hottest[0]?.criticalCelsius ? [{ value: hottest[0].criticalCelsius, label: `critical ${hottest[0].criticalCelsius} °C`, kind: 'limit' }] : []}
      />
      <Panel icon={icon} title="Sensors" help={HELP.temperatures} subtitle="Now">
        <Table
          id="sensors"
          rows={[...(sensors ?? [])]}
          rowKey={name}
          empty="No sensors."
          cols={[
            { label: 'Sensor', sort: s => s.label, cell: s => <span className="text-text">{s.label}</span>, title: name },
            { label: 'Device', sort: s => s.source, cell: s => s.source },
            { label: 'Now', sort: s => s.celsius, right: true, width: '5rem', cell: s => <span style={{ color: s.highCelsius && s.celsius >= s.highCelsius ? T.warn : undefined }}>{fmt('celsius', s.celsius)}</span> },
            { label: 'High', sort: s => s.highCelsius, right: true, width: '5rem', cell: s => fmt('celsius', s.highCelsius) },
            { label: 'Critical', sort: s => s.criticalCelsius, right: true, width: '5rem', cell: s => fmt('celsius', s.criticalCelsius) },
          ]}
        />
      </Panel>
    </>
  );
}

function coresSubtitle(c: Ctx, cpuset: string | null | undefined) {
  const total = c.snapshot?.cpu?.coreUsagePercent.length ?? c.h.logicalCores ?? 0;
  const allowed = parseCpuset(cpuset);
  if (!allowed) return `${total} logical cores, current load`;
  return allowed.size >= total
    ? `Current load; this container may use all ${total} cores (${cpuset})`
    : `Current load; this container may use ${cpuset} (${allowed.size} of ${total}), the other cores are dimmed`;
}

function MemoryDetail(c: Ctx) {
  const { h, scope, hist, containers, snapshot } = c;
  const u = c.w ? usageOf(c.w) : null;
  const d = u?.detail;
  const m = snapshot?.memory;
  const icon = <IconMemory size={14} />;
  const events = (pick: (e: NonNullable<ContainerDetail['memoryEvents']>) => number) =>
    sum(containers, x => (x.usage?.detail?.memoryEvents ? pick(x.usage.detail.memoryEvents) : 0));

  return (
    <>
      <MainChart c={c} id="memory" icon={icon} />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Memory pressure" help={HELP.memoryPressure} unit="percent"
        info={['Time tasks stalled on memory']}
        series={[{ label: 'Some waiting', values: col(hist, 'memoryPressure') }, { label: 'All waiting', values: col(hist, 'memoryPressureFull') }]}
      />
      {scope.containerId ? (
        <DetailChart
          frame={c.frame} hist={hist} icon={icon} label="Memory composition" help={HELP.memoryCompositionContainer} unit="bytes"
          info={['Application, cache and kernel memory']}
          series={[{ label: 'Program data', values: col(hist, 'anon') }, { label: 'Cache', values: col(hist, 'file') }, { label: 'Kernel', values: col(hist, 'kernel'), color: PALETTE[2] }]}
          refs={u?.memoryLimitBytes ? [{ value: u.memoryLimitBytes, label: `limit ${fmtBytes(u.memoryLimitBytes)}`, kind: 'limit' }] : []}
        />
      ) : (
        <DetailChart
          frame={c.frame} hist={hist} icon={icon} label="Memory composition" help={HELP.memoryCompositionHost} unit="bytes"
          info={['In use and page cache']}
          series={[{ label: 'In use', values: col(hist, 'memoryUsed') }, { label: 'Cache', values: col(hist, 'memoryCached') }]}
          refs={h.memoryTotalBytes ? [{ value: h.memoryTotalBytes, label: `${wslVm(h) ? 'VM size' : 'capacity'} ${fmtBytes(h.memoryTotalBytes)}`, kind: 'capacity' }] : []}
        />
      )}
      <Panel icon={icon} title="Breakdown and limit events" help={HELP.memoryBreakdown} subtitle={scope.containerId ? 'Since the container started' : 'The host, and its containers’ totals'}>
        <Facts rows={scope.containerId ? [
          ['In use (excl. inactive cache)', fmtBytes(u?.memoryBytes)],
          ['Limit', u?.memoryLimitBytes ? fmtBytes(u.memoryLimitBytes) : 'none'],
          ['Program data', fmtBytes(d?.memory?.anon)],
          ['Cache', fmtBytes(d?.memory?.file)],
          ['  of which shared memory', fmtBytes(d?.memory?.shmem)],
          ['Kernel', fmtBytes(d?.memory?.kernel)],
          ['Network buffers', fmtBytes(d?.memory?.sock)],
          ['Peak (incl. cache)', fmtBytes(d?.memory?.peakBytes)],
          ['Swapped out', d?.memory?.swapBytes != null ? fmtBytes(d.memory.swapBytes) : '–', (d?.memory?.swapBytes ?? 0) > 0],
          ['With reclaimable cache', d?.memory?.currentBytes != null ? fmtBytes(d.memory.currentBytes) : NA],
          ['  of which inactive cache', d?.memory?.inactiveFileBytes != null ? fmtBytes(d.memory.inactiveFileBytes) : NA],
          ['Unsaved / being written', d?.memory?.dirtyBytes != null ? `${fmtBytes(d.memory.dirtyBytes)} / ${fmtBytes(d.memory.writebackBytes)}` : NA],
          ['Out-of-memory kills', d?.memoryEvents ? String(d.memoryEvents.oomKill) : '–', (d?.memoryEvents?.oomKill ?? 0) > 0],
          ['Hit the limit', d?.memoryEvents ? String(d.memoryEvents.max) : '–', (d?.memoryEvents?.max ?? 0) > 0],
          ['Over the soft limit', d?.memoryEvents ? String(d.memoryEvents.high) : '–'],
        ] : [
          [wslVm(h) ? 'Total (Docker VM)' : 'Total', fmtBytes(m?.totalBytes)],
          ['In use', fmtBytes(m?.usedBytes)],
          ['Cache', fmtBytes(m?.cachedBytes)],
          ['Available', fmtBytes(m?.availableBytes)],
          ['Swap', m?.swapTotalBytes ? `${fmtBytes(m.swapUsedBytes)} of ${fmtBytes(m.swapTotalBytes)}` : 'none'],
          ['Containers’ memory', fmtBytes(sum(containers, x => x.usage?.memoryBytes))],
          ['Containers’ limits', fmtBytes(sum(containers, x => x.usage?.memoryLimitBytes))],
          ['Containers’ out-of-memory kills', String(events(e => e.oomKill)), events(e => e.oomKill) > 0],
          ['Containers hit their limit', String(events(e => e.max)), events(e => e.max) > 0],
          ['Committed', m?.committedBytes != null ? `${fmtBytes(m.committedBytes)} of ${fmtBytes(m.commitLimitBytes)}` : NA,
            m?.committedBytes != null && m.commitLimitBytes != null && m.committedBytes > m.commitLimitBytes * 0.9],
          ['Kernel caches, freeable / not', m?.slabReclaimableBytes != null ? `${fmtBytes(m.slabReclaimableBytes)} / ${fmtBytes(m.slabUnreclaimableBytes)}` : NA],
          ['Unsaved / being written', m?.dirtyBytes != null ? `${fmtBytes(m.dirtyBytes)} / ${fmtBytes(m.writebackBytes)}` : NA],
        ]} />
      </Panel>
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Memory read from disk" help={HELP.pageFaults} unit="perSecond"
        info={['Pages per second']}
        series={scope.containerId
          ? [{ label: 'From disk', values: col(hist, 'majorFaults') }, { label: 'Re-read', values: col(hist, 'refaults') }]
          : [{ label: 'From disk', values: col(hist, 'majorFaults') }]}
      />
      {!scope.containerId && (
        <DetailChart
          frame={c.frame} hist={hist} icon={icon} label="Swap activity" help={HELP.swap} unit="rate"
          info={[m?.swapTotalBytes ? `${fmtBytes(m.swapUsedBytes)} of ${fmtBytes(m.swapTotalBytes)} swap in use` : 'No swap']}
          series={[{ label: 'Swapped in', values: col(hist, 'swapIn') }, { label: 'Swapped out', values: col(hist, 'swapOut') }]}
        />
      )}
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Memory being freed" help={HELP.reclaim} unit="rate"
        info={['By the system, to make room']}
        series={[{ label: 'Checked', values: col(hist, 'reclaimScanned') }, { label: 'Freed', values: col(hist, 'reclaimed') }]}
        empty={notReported(c, scope.containerId ? d?.memory?.reclaimScannedBytesPerSecond : m?.reclaimScannedBytesPerSecond, col(hist, 'reclaimScanned'), 'this kernel doesn’t report reclaim for the cgroup (kernel 5.x and later do)')}
      />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Unsaved writes" help={HELP.dirty} unit="bytes" wide={!!scope.containerId} floor={1024 * 1024}
        info={['Written, not yet on disk']}
        series={[{ label: 'Waiting', values: col(hist, 'dirty') }, { label: 'Being written', values: col(hist, 'writeback') }]}
        empty={notReported(c, scope.containerId ? d?.memory?.dirtyBytes : m?.dirtyBytes, col(hist, 'dirty'), 'this kernel doesn’t report it')}
      />
      {!scope.containerId && (
        <DetailChart
          frame={c.frame} hist={hist} icon={icon} label="Committed memory" help={HELP.committed} unit="bytes" wide
          info={[m?.commitLimitBytes ? `Limit ${fmtBytes(m.commitLimitBytes)}` : 'Promised to programs']}
          series={[{ label: 'Committed', values: col(hist, 'committed') }]}
          refs={m?.commitLimitBytes ? [{ value: m.commitLimitBytes, label: `commit limit ${fmtBytes(m.commitLimitBytes)}`, kind: 'limit' }] : []}
          empty={notReported(c, m?.committedBytes, col(hist, 'committed'), 'this kernel doesn’t report it')}
        />
      )}
      <ProcessPanel c={c} sort={p => p.memoryBytes} title="Processes by memory" cols={MEMORY_COLS} window={MEMORY_WINDOW} />
    </>
  );
}

function GpuDetail(c: Ctx) {
  const { h, scope, hist, hostHist, containers, snapshot } = c;
  const devices = snapshot?.gpu.devices ?? [];
  const icon = <IconGpu size={14} />;
  const u = c.w ? usageOf(c.w) : null;

  if (!h.gpus.length) {
    return <><MainChart c={c} id="gpu" icon={icon} /><Panel icon={icon} title="GPU">This host has no GPU.</Panel></>;
  }

  const counted = scope.containerId ? (c.w ? [c.w] : []) : containers;
  const label = scope.containerId ? 'This container' : 'Containers';
  const failures = counted.map(x => usageOf(x)?.detail?.gpuAllocationFailures).filter((v): v is number => v != null);
  const energy = (index: number) => {
    const power = col(hostHist, `power:${index}`)?.filter((v): v is number => v != null);
    return power?.length && hostHist ? sum(power, v => v) * hostHist.bucketSeconds / 3600 : null;
  };

  return (
    <>
      <MainChart c={c} id="gpu" icon={icon} />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Host ↔ GPU transfers" help={HELP.transfers} unit="rate"
        info={['Copies over PCIe']}
        series={scope.containerId
          ? [{ label: 'To GPU', values: col(hist, 'toDevice') }, { label: 'To host', values: col(hist, 'toHost') }]
          : [{ label: 'To GPU', values: across(hist, 'toDevice', 'sum') }, { label: 'To host', values: across(hist, 'toHost', 'sum') }]}
      />
      <DetailChart
        frame={c.frame} hist={hostHist} icon={icon} label="Power draw" help={HELP.power} unit="watts"
        info={[scope.containerId ? 'Whole card, not per container' : 'Per GPU']}
        series={devices.map((g, i) => ({ label: `GPU ${g.index}`, values: col(hostHist, `power:${g.index}`), color: i === 0 ? T.usage : PALETTE[i % PALETTE.length], fill: i === 0 }))}
        refs={devices.filter(g => g.powerLimitWatts).map(g => ({ value: g.powerLimitWatts!, label: `GPU ${g.index} limit ${g.powerLimitWatts!.toFixed(0)} W`, kind: 'limit' as const }))}
      />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="GPU kernel launches" help={HELP.launches} unit="perSecond"
        info={['GPU work items started']}
        series={[{ label, values: scope.containerId ? col(hist, 'launches') : across(hist, 'launches', 'sum') }]}
      />
      <Panel icon={icon} title={devices.length > 1 ? 'Cards' : 'Card'} help={HELP.card} subtitle="Now" wide fit>
        <div className="grid gap-3">
          {devices.map(g => {
            const shares = counted.flatMap(x => (usageOf(x)?.detail?.gpus ?? []).filter(s => s.pciBusId === g.pciBusId));
            const launches = sum(shares, s => s.launchesPerSecond);
            const kernelUs = launches ? sum(shares, s => (s.launchesPerSecond ?? 0) * (s.averageKernelMicroseconds ?? 0)) / launches : null;
            const card: [string, string, boolean?][] = [
                  ['Compute', fmtPct(g.utilizationPercent)],
                  ['VRAM', `${fmtBytes(g.memoryUsedBytes)} of ${fmtBytes(g.memoryTotalBytes)}`],
                  ['Power', g.powerDrawWatts != null ? `${g.powerDrawWatts.toFixed(1)} W${g.powerLimitWatts ? ` of ${g.powerLimitWatts.toFixed(0)} W` : ''}` : '–'],
                  ['GPU clock', g.clockSmMhz != null ? `${g.clockSmMhz} MHz${g.clockSmMaxMhz ? ` of ${g.clockSmMaxMhz} MHz` : ''}` : '–'],
                  ['Temperature', g.temperatureCelsius != null ? `${g.temperatureCelsius} °C` : '–'],
                  ['Performance state', g.performanceState ?? '–'],
                  ['PCIe link', g.pcieGeneration ? `Gen ${g.pcieGeneration} x${g.pcieWidth}` : '–'],
                  ['Clock limited by', g.clockEventReasons ? (g.clockEventReasons.length ? g.clockEventReasons.join(', ') : 'nothing') : '–',
                    Boolean(g.clockEventReasons?.some(x => x !== 'GPU idle'))],
                  ['Memory clock', g.clockMemoryMhz != null ? `${g.clockMemoryMhz} MHz${g.clockMemoryMaxMhz ? ` of ${g.clockMemoryMaxMhz} MHz` : ''}` : '–'],
                  ['Video encoder / decoder', g.encoderPercent != null ? `${fmtPct(g.encoderPercent)} / ${fmtPct(g.decoderPercent)}` : '–'],
                  ['Temperature headroom', g.temperatureHeadroomCelsius != null ? `${g.temperatureHeadroomCelsius} °C before it slows down` : NA],
                  [`Energy, last ${span(c.range)}`, energy(g.index) != null ? `${energy(g.index)!.toFixed(energy(g.index)! >= 10 ? 0 : 2)} Wh` : NA],
                  ['Memory errors (ECC), corrected / not', g.eccCorrected != null ? `${g.eccCorrected} / ${g.eccUncorrected ?? 0}` : `${NA}: this card has no ECC memory`, (g.eccUncorrected ?? 0) > 0],
            ];
            const mine: [string, string, boolean?][] = [
                  [`${label}: compute`, fmtPct(sum(shares, s => s.percent))],
                  [`${label}: VRAM`, fmtBytes(sum(shares, s => s.memoryBytes))],
                  [`${label}: to GPU / to host`, `${fmtRate(sum(shares, s => s.hostToDeviceBytesPerSecond))} / ${fmtRate(sum(shares, s => s.deviceToHostBytesPerSecond))}`],
                  ...(scope.containerId ? [['This container: peak VRAM', fmtBytes(u?.detail?.gpuMemoryPeakBytes)] as [string, string]] : []),
                  [`${label}: GPU kernel launches`, fmt('perSecond', launches)],
                  [`${label}: average GPU kernel`, kernelUs != null ? `${kernelUs.toFixed(kernelUs >= 100 ? 0 : 1)} µs` : '–'],
                  [`${label}: failed allocations`, failures.length ? sum(failures, v => v).toLocaleString() : 'no CUDA process yet', sum(failures, v => v) > 0],
            ];
            return (
              <div key={g.index}>
                <div className="mb-1 text-[11px] font-medium text-text">GPU {g.index} · {g.name}</div>
                <div className="grid items-start gap-x-8 gap-y-1 md:grid-cols-2"><Facts rows={card} /><Facts rows={mine} /></div>
              </div>
            );
          })}
        </div>
      </Panel>
      <KernelsPanel c={c} counted={counted} icon={icon} />
      <ProcessPanel c={c} sort={p => p.gpuPercent ?? -1} title="GPU processes" gpu window={GPU_WINDOW} />
    </>
  );
}

function KernelsPanel({ c, counted, icon }: { c: Ctx; counted: ContainerState[]; icon: ReactNode }) {
  const rows = counted
    .flatMap(x => (usageOf(x)?.detail?.gpuKernels ?? []).map(k => ({ ...k, containerId: x.containerId })))
    .sort((a, b) => b.busyPercent - a.busyPercent)
    .slice(0, 30);
  const reporting = counted.some(x => usageOf(x)?.detail?.gpuKernels);
  return (
    <Panel icon={icon} title="Top GPU kernels" help={HELP.gpuKernels} wide subtitle={c.scope.containerId ? 'This container, last interval' : 'Every container, last interval'}>
      <Table
        id="gpu-kernels"
        rows={rows}
        rowKey={x => `${x.containerId}/${x.name}`}
        empty={reporting ? 'No GPU work in the last interval.' : 'No CUDA process has run yet.'}
        cols={[
          { label: 'Kernel', sort: x => x.name, cell: x => <span className="font-mono text-text">{x.name}</span>, title: x => x.name },
          ...(c.scope.containerId ? [] : [{ label: 'Container', sort: (x: (typeof rows)[number]) => containerName(c, x.containerId), width: '13rem', cell: (x: (typeof rows)[number]) => <ContainerCell c={c} id={x.containerId} /> }]),
          { label: 'Busy', sort: x => x.busyPercent, right: true, width: '5.5rem', cell: x => fmtPct(x.busyPercent), title: () => 'Run time as a share of one GPU' },
          { label: 'Launches', sort: x => x.launchesPerSecond, right: true, cell: x => fmt('perSecond', x.launchesPerSecond) },
          { label: 'Average', sort: x => x.averageMicroseconds, right: true, cell: x => `${x.averageMicroseconds.toFixed(x.averageMicroseconds >= 100 ? 0 : 1)} µs` },
        ]}
      />
    </Panel>
  );
}

function DiskDetail(c: Ctx) {
  const { h, scope, hist, containers, snapshot } = c;
  const u = c.w ? usageOf(c.w) : null;
  const icon = <IconStorage size={14} />;
  const volume = containerVolume(h);
  const devices = scope.containerId
    ? (u?.detail?.ioDevices ?? []).map(x => ({ name: x.device, read: x.readBytesPerSecond, write: x.writeBytesPerSecond, readOps: x.readOpsPerSecond, writeOps: x.writeOpsPerSecond, busy: null, readMs: null, writeMs: null, queue: null }))
    : (snapshot?.disk.devices ?? []).map(x => ({ name: x.name, read: x.readBytesPerSecond, write: x.writeBytesPerSecond, readOps: x.readOpsPerSecond, writeOps: x.writeOpsPerSecond, busy: x.busyPercent, readMs: x.readLatencyMs, writeMs: x.writeLatencyMs, queue: x.queueDepth }));
  type Device = (typeof devices)[number];
  const counted = scope.containerId ? (c.w ? [c.w] : []) : containers;
  const folders = counted
    .flatMap(x => (usageOf(x)?.detail?.largestPaths ?? []).map(f => ({ ...f, containerId: x.containerId })))
    .sort((a, b) => b.bytes - a.bytes)
    .slice(0, 20);
  const growth = u?.detail?.diskGrowthBytesPerHour ?? null;
  // Docker Desktop keeps the containers' disk in a virtual disk file that grows into the host's disk: it can't
  // take more than either has free. On Linux both are the same disk.
  const free = u?.detail?.diskFreeBytes != null ? Math.min(u.detail.diskFreeBytes, volume ? volume.totalBytes - volume.usedBytes : Infinity) : null;
  const inodes = snapshot?.disk.volumes.find(v => v.path === volume?.path);
  const mounts = counted.flatMap(x => (usageOf(x)?.detail?.mounts ?? []).map(m => ({ ...m, containerId: x.containerId })));

  return (
    <>
      <MainChart c={c} id="disk" icon={icon} />
      <HostVsChart c={c} icon={icon} help={HELP.diskHostVs} unit="rate" host={['diskRead', 'diskWrite']} container={['read', 'write']} what="Read + write" />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="I/O pressure" help={HELP.ioPressure} unit="percent"
        info={['Time tasks waited on disk I/O']}
        series={[{ label: 'Some waiting', values: col(hist, 'ioPressure') }, { label: 'All waiting', values: col(hist, 'ioPressureFull') }]}
      />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Disk operations" help={HELP.operations} unit="ops"
        info={['Requests per second']}
        series={[{ label: 'Read', values: col(hist, 'readOps') }, { label: 'Write', values: col(hist, 'writeOps') }]}
      />
      <Panel icon={icon} title="Storage" help={HELP.storage} subtitle={scope.containerId ? 'This container’s files' : 'Disk holding Docker’s data'}>
        <Facts rows={scope.containerId ? [
          ['Space used', fmtBytes(u?.diskUsedBytes)],
          ['Files', u?.detail?.fileCount != null ? u.detail.fileCount.toLocaleString() : '–'],
          ['On', volume?.name ?? '–'],
          ['Free on Docker’s disk', free != null ? fmtBytes(free) : NA],
          ['Growing', growth == null ? 'measuring (a few minutes)' : growth > 0 ? `${fmtBytes(growth)} per hour` : 'not growing'],
          ['Disk full in', growth == null ? 'measuring (a few minutes)' : growth <= 0 ? 'not filling up' : free == null ? NA
            : free / growth > 24 * 365 ? 'more than a year at this rate' : `about ${fmtDuration(free / growth * 3600)} at this rate`,
            Boolean(growth && growth > 0 && free != null && free / growth < 24 * 7)],
          ['Held by deleted files', u?.detail?.deletedFiles ? fmtBytes(sum(u.detail.deletedFiles, f => f.bytes)) : '–', sum(u?.detail?.deletedFiles ?? [], f => f.bytes) > 0],
          ['Open files', u?.detail?.openFiles != null
            ? `${u.detail.openFiles.toLocaleString()}${u.detail.openFilesUncounted ? ` (${u.detail.openFilesUncounted} processes of other users not counted)` : ''}`
            : NA],
        ] : [
          ['Disk', volume?.name ?? '–'],
          ['Used', volume ? `${fmtBytes(volume.usedBytes)} of ${fmtBytes(volume.totalBytes)} (${fmtPct(volume.usagePercent)})` : '–'],
          ['Free', volume ? fmtBytes(volume.totalBytes - volume.usedBytes) : '–'],
          ['Containers’ space', fmtBytes(sum(containers, x => x.usage?.diskUsedBytes))],
          ['Containers’ files', sum(containers, x => x.usage?.detail?.fileCount).toLocaleString()],
          ['Inodes used', inodes?.inodesTotal != null && inodes.inodesFree != null
            ? `${(inodes.inodesTotal - inodes.inodesFree).toLocaleString()} of ${inodes.inodesTotal.toLocaleString()}`
            : `${NA} on this filesystem`, inodes?.inodesTotal != null && inodes.inodesFree != null && inodes.inodesFree < inodes.inodesTotal * 0.1],
          ['Open files', snapshot?.files ? `${snapshot.files.open.toLocaleString()} of ${snapshot.files.max.toLocaleString()}` : NA],
        ]} />
      </Panel>
      <Panel icon={icon} title="Devices" help={HELP.devices} subtitle={scope.containerId ? 'Disks its I/O reaches' : 'Block devices'}>
        <Table
          id="devices"
          rows={devices}
          rowKey={x => x.name}
          empty={scope.containerId ? 'No disk I/O from this container yet.' : 'No block devices reported.'}
          cols={[
            { label: 'Device', sort: x => x.name, cell: x => <span className="font-mono text-text">{x.name}</span> },
            { label: 'Read', sort: x => x.read, right: true, cell: x => fmtRate(x.read) },
            { label: 'Write', sort: x => x.write, right: true, cell: x => fmtRate(x.write) },
            { label: 'Read IOPS', sort: x => x.readOps, right: true, cell: x => x.readOps.toFixed(1) },
            { label: 'Write IOPS', sort: x => x.writeOps, right: true, cell: x => x.writeOps.toFixed(1) },
            ...(scope.containerId ? [] : [{ label: 'Busy', sort: (x: Device) => x.busy, right: true, width: '5rem', cell: (x: Device) => fmtPct(x.busy) }]),
            ...(!scope.containerId ? [
              { label: 'Read time', sort: (x: Device) => x.readMs, right: true, width: '5.5rem', cell: (x: Device) => fmtMs(x.readMs), title: () => 'Average time per read' },
              { label: 'Write time', sort: (x: Device) => x.writeMs, right: true, width: '5.5rem', cell: (x: Device) => fmtMs(x.writeMs), title: () => 'Average time per write' },
              { label: 'Queue', sort: (x: Device) => x.queue, right: true, width: '4.5rem', cell: (x: Device) => (x.queue != null ? x.queue.toFixed(2) : '–'), title: () => 'Requests in flight, on average' },
            ] : []),
          ]}
        />
      </Panel>
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Disk space used" help={HELP.storageGrowth} unit="bytes" wide
        info={[scope.containerId ? (growth != null ? `${growth >= 0 ? '+' : '−'}${fmtBytes(Math.abs(growth))} per hour` : 'Image plus what it wrote') : 'Every container’s files']}
        series={[scope.containerId ? { label: 'This container', values: col(hist, 'diskUsed') } : { label: 'Containers', values: across(hist, 'diskUsed', 'sum') }]}
        floor={1024 * 1024}
      />
      <Panel icon={icon} title="Volumes and mounts" help={HELP.mounts} wide subtitle={scope.containerId ? 'Mounted in this container' : 'Mounted in the containers'}>
        <Table
          id="mounts"
          rows={mounts}
          rowKey={x => `${x.containerId}${x.mountPoint}`}
          empty={counted.some(x => usageOf(x)?.detail?.mounts) ? 'No volumes or bind mounts.' : 'Not measured yet (every 30 seconds).'}
          cols={[
            { label: 'Mounted at', sort: x => x.mountPoint, cell: x => <span className="font-mono text-text">{x.mountPoint}</span>, title: x => x.mountPoint },
            ...(scope.containerId ? [] : [{ label: 'Container', sort: (x: (typeof mounts)[number]) => containerName(c, x.containerId), width: '13rem', cell: (x: (typeof mounts)[number]) => <ContainerCell c={c} id={x.containerId} /> }]),
            { label: 'Source', sort: x => x.source, cell: x => x.source, title: x => `${x.source} (${x.fileSystem}${x.readOnly ? ', read-only' : ''})` },
            { label: 'Type', sort: x => x.fileSystem, width: '6rem', cell: x => `${x.fileSystem}${x.readOnly ? ' · ro' : ''}` },
            { label: 'Used', sort: x => x.usedBytes, right: true, cell: x => (x.usedBytes != null ? fmtBytes(x.usedBytes) : x.readOnly ? 'read-only' : '–'), title: () => 'The files under this mount' },
            { label: 'Disk free', sort: x => x.freeBytes, right: true, cell: x => (x.freeBytes != null ? fmtBytes(x.freeBytes) : NA), title: x => (x.totalBytes != null ? `of ${fmtBytes(x.totalBytes)}` : '') },
            {
              label: 'Inodes used',
              sort: x => (x.inodesTotal != null && x.inodesFree != null ? pct(x.inodesTotal - x.inodesFree, x.inodesTotal) : null),
              right: true,
              width: '7.5rem',
              cell: x => (x.inodesTotal != null && x.inodesFree != null ? fmtPct(pct(x.inodesTotal - x.inodesFree, x.inodesTotal)) : NA),
              title: x => (x.inodesTotal != null && x.inodesFree != null ? `${(x.inodesTotal - x.inodesFree).toLocaleString()} of ${x.inodesTotal.toLocaleString()}` : 'This filesystem doesn’t count inodes'),
            },
          ]}
        />
      </Panel>
      <Panel icon={icon} title="Largest folders" help={HELP.largestPaths} sounding wide subtitle={scope.containerId ? 'In this container’s files' : 'In the containers’ files'}>
        <Table
          id="folders"
          sounding={x => x.path.startsWith('/opt/sounding')}
          rows={folders}
          rowKey={x => `${x.containerId}${x.path}`}
          empty={counted.some(x => usageOf(x)?.detail?.largestPaths) ? 'No files.' : 'Not measured yet (every 30 seconds).'}
          cols={[
            { label: 'Folder', sort: x => x.path, cell: x => <span className="font-mono text-text">{x.path}</span>, title: x => x.path },
            ...(scope.containerId ? [] : [{ label: 'Container', sort: (x: (typeof folders)[number]) => containerName(c, x.containerId), width: '13rem', cell: (x: (typeof folders)[number]) => <ContainerCell c={c} id={x.containerId} /> }]),
            { label: 'Size', sort: x => x.bytes, right: true, cell: x => fmtBytes(x.bytes) },
            { label: 'Files', sort: x => x.files, right: true, cell: x => x.files.toLocaleString() },
          ]}
        />
      </Panel>
      <FilesPanels c={c} counted={counted} icon={icon} />
      <ProcessPanel
        c={c} title="Processes by disk I/O" help={HELP.diskProcesses} cols={DISK_COLS} window={DISK_WINDOW}
        sort={p => (p.readBytesPerSecond ?? 0) + (p.writeBytesPerSecond ?? 0)}
      />
    </>
  );
}

function FilesPanels({ c, counted, icon }: { c: Ctx; counted: ContainerState[]; icon: ReactNode }) {
  const pick = <T,>(of: (d: ContainerDetail) => T[] | null) =>
    counted.flatMap(x => (usageOf(x)?.detail ? of(usageOf(x)!.detail!) ?? [] : []).map(row => ({ ...row, containerId: x.containerId })));
  const measured = counted.some(x => usageOf(x)?.detail?.largestFiles);
  const files = pick(d => d.largestFiles).sort((a, b) => b.bytes - a.bytes).slice(0, 20);
  const growing = pick(d => d.growingPaths).sort((a, b) => b.bytes - a.bytes).slice(0, 20);
  const deleted = pick(d => d.deletedFiles).sort((a, b) => b.bytes - a.bytes);
  const container = c.scope.containerId ? [] : [{ label: 'Container', width: '13rem', sort: (x: { containerId: string }) => containerName(c, x.containerId), cell: (x: { containerId: string }) => <ContainerCell c={c} id={x.containerId} /> }];
  const where = c.scope.containerId ? 'In this container’s files' : 'In the containers’ files';
  return (
    <>
      <Panel icon={icon} title="Largest files" help={HELP.largestFiles} sounding subtitle={where}>
        <Table
          id="largest-files"
          sounding={x => x.path.startsWith('/opt/sounding')}
          rows={files}
          rowKey={x => `${x.containerId}${x.path}`}
          empty={measured ? 'No files.' : 'Not measured yet (every 30 seconds).'}
          cols={[
            { label: 'File', sort: x => x.path, cell: x => <span className="font-mono text-text">{x.path}</span>, title: x => x.path },
            ...container,
            { label: 'Size', sort: x => x.bytes, right: true, cell: x => fmtBytes(x.bytes) },
          ]}
        />
      </Panel>
      <Panel icon={icon} title="Growing folders" help={HELP.growingPaths} sounding subtitle="Since the measurement 30 seconds earlier">
        <Table
          id="growing-folders"
          sounding={x => x.path.startsWith('/opt/sounding')}
          rows={growing}
          rowKey={x => `${x.containerId}${x.path}`}
          empty={measured ? 'Nothing grew.' : 'Not measured yet (every 30 seconds).'}
          cols={[
            { label: 'Folder', sort: x => x.path, cell: x => <span className="font-mono text-text">{x.path}</span>, title: x => x.path },
            ...container,
            { label: 'Grew', sort: x => x.bytes, right: true, cell: x => `+${fmtBytes(x.bytes)}` },
            { label: 'New files', sort: x => x.files, right: true, cell: x => (x.files > 0 ? `+${x.files.toLocaleString()}` : '–') },
          ]}
        />
      </Panel>
      <Panel icon={icon} title="Deleted but still open" help={HELP.deletedFiles} wide subtitle="Space a process keeps using after its file was deleted">
        <Table
          id="deleted-files"
          rows={deleted}
          rowKey={x => `${x.containerId}/${x.pid}${x.path}`}
          empty={measured ? 'None: no process holds a deleted file open.' : 'Not measured yet (every 30 seconds).'}
          cols={[
            { label: 'File', sort: x => x.path, cell: x => <span className="font-mono text-text">{x.path}</span>, title: x => x.path },
            ...container,
            { label: 'Process', sort: x => x.process, cell: x => `${x.process} (${x.pid})` },
            { label: 'Size', sort: x => x.bytes, right: true, cell: x => <span style={{ color: T.warn }}>{fmtBytes(x.bytes)}</span> },
          ]}
        />
      </Panel>
    </>
  );
}

function NetworkDetail(c: Ctx) {
  const { scope, hist, snapshot } = c;
  const u = c.w ? usageOf(c.w) : null;
  const icon = <IconNetwork size={14} />;

  return (
    <>
      <MainChart c={c} id="network" icon={icon} />
      <HostVsChart
        c={c} icon={icon} help={HELP.networkHostVs} unit="rate" host={['netRx', 'netTx']} container={['rx', 'tx']}
        what="In + out"
      />
      <ConnectionsPanel c={c} />
      <RequestsPanel c={c} />
      <ListeningPanel c={c} />
      <SocketsPanel c={c} />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Internet vs internal" help={HELP.internet} unit="rate"
        info={['By where the other end is']}
        series={scope.containerId
          ? [
              { label: 'To internet', values: col(hist, 'internetOut') },
              { label: 'From internet', values: col(hist, 'internetIn') },
              { label: 'Not internet', values: add(col(hist, 'internalOut'), col(hist, 'internalIn')), color: PALETTE[2], fill: false },
              { label: 'Same network', values: add(col(hist, 'networkOut'), col(hist, 'networkIn')), color: PALETTE[3], fill: false },
            ]
          : [
              { label: 'To internet', values: across(hist, 'internetOut', 'sum') },
              { label: 'From internet', values: across(hist, 'internetIn', 'sum') },
              { label: 'Not internet', values: add(across(hist, 'internalOut', 'sum'), across(hist, 'internalIn', 'sum')), color: PALETTE[2], fill: false },
              { label: 'Same network', values: add(across(hist, 'networkOut', 'sum'), across(hist, 'networkIn', 'sum')), color: PALETTE[3], fill: false },
            ]}
        empty={captureOff(c) ? `Packet capture is off: ${captureOff(c)}` : undefined}
      />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Packets" help={HELP.packets} unit="packets"
        info={[scope.containerId ? 'This container’s interfaces' : 'The host’s interfaces']}
        series={[{ label: 'In', values: col(hist, 'rxPackets') }, { label: 'Out', values: col(hist, 'txPackets') }]}
      />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="TCP health" help={HELP.tcp} unit="perSecond"
        info={[scope.containerId ? 'This container’s TCP' : 'The host’s own TCP']}
        series={[
          { label: 'Resent', values: col(hist, 'retransmits') },
          { label: 'Connections reset', values: col(hist, 'resets') },
          { label: 'Failed connections', values: col(hist, 'failedConnects'), color: PALETTE[2], fill: false },
        ]}
        empty={hostCountersGap(c)}
      />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="UDP errors" help={HELP.udp} unit="perSecond"
        info={[scope.containerId ? 'This container’s UDP' : 'The host’s own UDP']}
        series={[
          { label: 'Receive errors', values: col(hist, 'udpErrors') },
          { label: 'Buffer full', values: col(hist, 'udpBufferErrors') },
          { label: 'No listener', values: col(hist, 'udpNoPort'), color: PALETTE[2], fill: false },
        ]}
        empty={hostCountersGap(c) ?? notReported(c, scope.containerId ? u?.detail?.udp : snapshot?.network.udp, col(hist, 'udpErrors'), 'the UDP counters couldn’t be read')}
      />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="DNS" help={HELP.dns} unit="perSecond"
        info={['Replies, and the ones that failed']}
        series={scope.containerId
          ? [{ label: 'Lookups', values: col(hist, 'dnsLookups') }, { label: 'Failures', values: col(hist, 'dnsFailures') }]
          : [{ label: 'Lookups', values: across(hist, 'dnsLookups', 'sum') }, { label: 'Failures', values: across(hist, 'dnsFailures', 'sum') }]}
        empty={captureOff(c) ? `${NA}: needs packet capture (${captureOff(c)})` : undefined}
      />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="DNS reply time" help={HELP.dnsReplyTime} unit="latency"
        info={['Query to answer']}
        series={scope.containerId
          ? [{ label: 'Average reply', values: col(hist, 'dnsReplyMs') }]
          : [{ label: 'Slowest container', values: across(c.hist, 'dnsReplyMs', 'max') }]}
        empty={captureOff(c) ? `${NA}: needs packet capture (${captureOff(c)})` : undefined}
      />
      <LookupsPanel c={c} />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Connection tracking" help={HELP.conntrack} unit="count"
        info={[scope.containerId ? 'This container’s network' : 'The host’s network']}
        series={[{ label: 'Entries', values: col(hist, 'conntrack') }]}
        refs={(() => {
          const max = (scope.containerId ? u?.detail?.conntrack : snapshot?.network.conntrack)?.max;
          return max ? [{ value: max, label: `limit ${max.toLocaleString()}`, kind: 'limit' as const }] : [];
        })()}
        empty={hostCountersGap(c) ?? notReported(c, scope.containerId ? u?.detail?.conntrack : snapshot?.network.conntrack, col(hist, 'conntrack'), 'connection tracking isn’t loaded for this network (nothing NATs or filters it)')}
      />
      {scope.containerId && <CapturePanel c={c} />}
      <Suspense fallback={null}>
        <NetworkMap r={c.r} scope={scope} range={c.range} period={span(c.range)} onScope={c.onScope} onOpen={c.onOpen} />
      </Suspense>
    </>
  );
}

const LEVEL_COLORS = { error: T.crit, warning: T.warn, info: undefined } as const;

function EventsDetail(c: Ctx) {
  const { scope, hist, containers } = c;
  const icon = <IconEvents size={14} />;
  const counted = scope.containerId ? (c.w ? [c.w] : []) : containers;
  const relaying = counted.some(x => usageOf(x)?.detail?.logs);
  const lines = counted
    .flatMap(x => (usageOf(x)?.detail?.recentLogs ?? []).filter(line => line.text.trim()).map(line => ({ ...line, containerId: x.containerId })))
    .sort((a, b) => Date.parse(b.time) - Date.parse(a.time));
  const noLogs = counted.some(x => usageOf(x)?.detail) && !relaying
    ? `${NA}: the output isn’t passed through Sounding’s launcher (the container has a terminal, docker run -t, or runs the agent alone)`
    : undefined;
  const container = scope.containerId ? [] : [{ label: 'Container', width: '13rem', sort: (x: { containerId: string | null }) => (x.containerId ? containerName(c, x.containerId) : ''), cell: (x: { containerId: string | null }) => (x.containerId ? <ContainerCell c={c} id={x.containerId} /> : '–') }];

  return (
    <>
      <Panel icon={icon} title="Events" help={HELP.events} wide subtitle={`${scope.containerId ? 'This container' : 'Every container on this host'}, last ${span(c.range)}`}>
        <Table
          id="events"
          rows={c.events ?? []}
          rowKey={x => `${x.time}/${x.containerId}/${x.kind}/${x.message}`}
          empty={c.events ? 'Nothing happened in this window.' : 'Loading…'}
          cols={[
            { label: 'Time', sort: x => Date.parse(x.time), width: '6.5rem', cell: x => new Date(x.time).toLocaleTimeString(), title: x => new Date(x.time).toLocaleString() },
            ...container,
            { label: 'Kind', sort: x => x.kind, width: '7rem', cell: x => <Badge warn={x.level !== 'info'}>{x.kind}</Badge> },
            { label: 'What happened', sort: x => x.message, cell: x => <span className="text-text" style={{ color: LEVEL_COLORS[x.level] }}>{x.message}</span>, title: x => x.message },
          ]}
        />
      </Panel>
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Log lines" help={HELP.logs} unit="perSecond"
        info={['Everything the containers wrote']}
        series={scope.containerId
          ? [{ label: 'Lines', values: col(hist, 'logLines') }]
          : [{ label: 'Lines', values: across(hist, 'logLines', 'sum') }]}
        empty={noLogs}
      />
      <DetailChart
        frame={c.frame} hist={hist} icon={icon} label="Errors and warnings" help={HELP.logs} unit="perSecond"
        info={['Lines that look like them']}
        series={scope.containerId
          ? [{ label: 'Errors', values: col(hist, 'logErrors'), color: T.crit }, { label: 'Warnings', values: col(hist, 'logWarnings'), color: T.warn, fill: false }]
          : [{ label: 'Errors', values: across(hist, 'logErrors', 'sum'), color: T.crit }, { label: 'Warnings', values: across(hist, 'logWarnings', 'sum'), color: T.warn, fill: false }]}
        empty={noLogs}
      />
      <Panel icon={icon} title="Latest output" help={HELP.logs} wide subtitle={noLogs ?? 'The last lines each container wrote'}>
        <Table
          id="output"
          rows={lines}
          rowKey={x => `${x.containerId}/${x.time}/${x.stream}/${x.text}`}
          empty={noLogs ?? 'Nothing written yet.'}
          cols={[
            { label: 'Time', sort: x => Date.parse(x.time), width: '6.5rem', cell: x => new Date(x.time).toLocaleTimeString() },
            ...container,
            { label: 'Stream', sort: x => x.stream, width: '5rem', cell: x => x.stream },
            { label: 'Line', sort: x => x.text, cell: x => <span className="font-mono text-text" style={{ color: LEVEL_COLORS[x.level] }}>{x.text}</span>, title: x => x.text },
          ]}
        />
      </Panel>
    </>
  );
}

function HostVsChart({ c, icon, help, unit, host, container, what }: { c: Ctx; icon: ReactNode; help: Help; unit: Unit; host: [string, string]; container: [string, string]; what: string }) {
  const { hostHist, scope } = c;
  return (
    <DetailChart
      frame={c.frame} hist={hostHist} icon={icon} help={help} label={scope.containerId ? 'Host vs this container' : 'Host vs containers'} unit={unit}
      info={[what]}
      series={[
        { label: 'Host total', values: add(col(hostHist, host[0]), col(hostHist, host[1])) },
        scope.containerId
          ? { label: 'This container', values: add(col(hostHist, `${container[0]}:${scope.containerId}`), col(hostHist, `${container[1]}:${scope.containerId}`)) }
          : { label: 'Containers', values: add(across(hostHist, container[0], 'sum'), across(hostHist, container[1], 'sum')) },
      ]}
    />
  );
}

function captureOff(c: Ctx): string | null {
  const off = (c.network ?? []).find(x => x.network.source !== 'packets');
  return off ? off.network.captureError ?? 'socket tables only' : null;
}

function rowsOf<T>(c: Ctx, pick: (n: ContainerNetwork['network']) => T[]) {
  return (c.network ?? []).flatMap(x => pick(x.network).map(row => ({ ...row, containerId: x.containerId })));
}

const span = (seconds: number) => (seconds >= 3600 ? `${seconds / 3600} hour${seconds > 3600 ? 's' : ''}` : `${seconds / 60} minute${seconds > 60 ? 's' : ''}`);

// Connections and lookups are the agent's own record, which goes back an hour whatever the range.
const heldSpan = (seconds: number) => span(Math.min(seconds, 3600));

const ago = (seconds: number) => (seconds < 2 ? 'now' : `${fmtDuration(seconds)} ago`);

const owner = (x: { process: string | null; pid: number | null; user: string | null }) =>
  x.process ? `${x.process} (${x.pid})${x.user ? ` · ${x.user}` : ''}` : x.user ? `user ${x.user}` : '–';

function containerName(c: Ctx, id: string) {
  const w = c.h.containers.find(x => x.containerId === id);
  return w ? nameOf(w) : id;
}

function ContainerCell({ c, id }: { c: Ctx; id: string }) {
  const w = c.h.containers.find(x => x.containerId === id);
  return w ? <ContainerId h={c.h} w={w} /> : <span className="font-mono">{id}</span>;
}

function ConnectionsPanel({ c }: { c: Ctx }) {
  const all = rowsOf(c, n => n.connections).filter(x => x.lastSeenSecondsAgo <= c.range).sort((a, b) =>
    Number(b.newDestination) - Number(a.newDestination)
    || Number(b.internet) - Number(a.internet)
    || ((b.outBytesPerSecond ?? 0) + (b.inBytesPerSecond ?? 0)) - ((a.outBytesPerSecond ?? 0) + (a.inBytesPerSecond ?? 0))
    || a.lastSeenSecondsAgo - b.lastSeenSecondsAgo);
  type Row = (typeof all)[number];
  const truncated = (c.network ?? []).some(x => x.network.connectionsTotal > x.network.connections.length);
  const off = captureOff(c);
  // Traffic of connections beyond those net-meter follows at once: in no row below.
  const untracked = sum(c.network ?? [], x => (x.network.untracked ? x.network.untracked.outBytesPerSecond + x.network.untracked.inBytesPerSecond : null));
  const anyUntracked = (c.network ?? []).some(x => x.network.untracked);

  return (
    <Panel
      icon={<IconNetwork size={14} />}
      title="Connections" help={HELP.connections} sounding
      wide
      subtitle={`${off ? `Packet capture off (${off}): open connections only, no byte counts` : `Last ${heldSpan(c.range)}`}${truncated ? ' · most recent per container' : ''}${anyUntracked ? ` · too many connections to follow them all: ${fmtRate(untracked)} is in none of these` : ''}`}
    >
      <Table
        id="connections"
        sounding={x => x.process === 'sounding-agent' || x.process === 'net-meter'}
        rows={all}
        rowKey={x => `${x.containerId}/${x.protocol}/${x.localPort}/${x.remoteAddress}/${x.remotePort}`}
        empty={c.network ? 'No connections.' : 'Loading…'}
        cols={[
          {
            label: 'Destination',
            sort: (x: Row) => x.remoteName ?? x.remoteAddress,
            width: '14rem',
            title: x => (x.remoteName ? `${x.remoteName} (${x.remoteAddress})` : x.remoteAddress),
            cell: x => (
              <span>
                <span className="text-text">{x.remoteName ?? x.remoteAddress}</span>
                {x.remoteName && <span className="ml-1.5 font-mono text-muted">{x.remoteAddress}</span>}
              </span>
            ),
          },
          { label: 'Port', sort: x => x.remotePort, width: '6.5rem', cell: x => <span className="font-mono">{x.remotePort ? `${x.remotePort}/${x.protocol}` : x.protocol}</span> },
          ...(c.scope.containerId ? [] : [{ label: 'Container', sort: (x: (typeof all)[number]) => containerName(c, x.containerId), width: '13rem', cell: (x: (typeof all)[number]) => <ContainerCell c={c} id={x.containerId} /> }]),
          { label: 'Process', sort: x => x.process, width: '12rem', cell: x => owner(x), title: x => (x.state ? `${x.state}, local port ${x.localPort}` : `local port ${x.localPort}`) },
          {
            label: 'Asked for',
            sort: (x: Row) => x.httpRequest ?? x.tlsName,
            cell: (x: Row) => (x.httpRequest ? <><Badge>HTTP</Badge>{x.httpRequest}</>
              : x.tlsName || x.tlsVersion ? <><Badge>TLS{x.tlsVersion ? ` ${x.tlsVersion}` : ''}</Badge>{x.tlsName ?? 'no server name'}{(x.alpn ?? x.alpnOffered) && <span className="ml-1.5 text-muted">{x.alpn ?? x.alpnOffered}</span>}</>
              : '–'),
            title: (x: Row) => [
              x.tlsName && `TLS server name: ${x.tlsName}`,
              x.tlsVersion && `TLS version: ${x.tlsVersion}`,
              x.alpn ? `Application protocol: ${x.alpn}` : x.alpnOffered && `Application protocols offered: ${x.alpnOffered} (the choice is encrypted in TLS 1.3)`,
              x.httpRequest && `HTTP request: ${x.httpRequest}`,
            ].filter(Boolean).join('\n'),
          },
          {
            label: 'Response',
            sort: (x: Row) => x.httpStatus,
            width: '7rem',
            cell: (x: Row) => (x.httpStatus != null
              ? <><span style={{ color: x.httpStatus >= 400 ? T.warn : undefined }}>{x.httpStatus}</span>{x.httpResponseMs != null && <span className="ml-1.5 text-muted">{fmtMs(x.httpResponseMs)}</span>}</>
              : '–'),
            title: () => 'HTTP status of the response to that request, and the time to its first byte',
          },
          { label: 'Handshake', sort: (x: Row) => x.handshakeMs, right: true, width: '6rem', cell: (x: Row) => fmtMs(x.handshakeMs), title: () => 'TCP round trip when the connection opened' },
          { label: 'Round trip', sort: (x: Row) => x.rttMs, right: true, width: '6rem', cell: (x: Row) => fmtMs(x.rttMs), title: () => 'The kernel’s smoothed round-trip time, now' },
          { label: 'Delivery rate', sort: (x: Row) => x.deliveryBytesPerSecond, right: true, width: '6rem', cell: (x: Row) => fmtRate(x.deliveryBytesPerSecond), title: () => 'The rate the kernel last measured data being delivered at: what the path can carry' },
          {
            label: 'Limited by',
            sort: (x: Row) => x.limitedBy,
            width: '7rem',
            cell: (x: Row) => (x.limitedBy ? <span style={{ color: x.limitedBy === 'receiver' || x.limitedBy === 'send buffer' ? T.warn : undefined }}>{x.limitedBy}</span> : '–'),
            title: () => 'While sending in the last second: the receiver’s window, the send buffer, the application not providing data, or else the network itself',
          },
          {
            label: 'Problems',
            sort: (x: Row) => x.problems?.length ?? 0,
            width: '10rem',
            cell: (x: Row) => <>{x.problems?.map(p => <Badge key={p} warn>{p}</Badge>)}</>,
            title: (x: Row) => x.problems?.join(', ') ?? '',
          },
          {
            label: 'Queued',
            sort: (x: Row) => (x.sendQueueBytes == null ? null : x.sendQueueBytes + (x.receiveQueueBytes ?? 0)),
            right: true,
            width: '7rem',
            cell: (x: Row) => (x.sendQueueBytes == null ? '–' : <span style={{ color: x.sendQueueBytes + (x.receiveQueueBytes ?? 0) > 0 ? T.warn : undefined }}>{fmtBytes(x.sendQueueBytes, 0)} / {fmtBytes(x.receiveQueueBytes, 0)}</span>),
            title: () => 'Waiting in the socket now: sent but not yet acknowledged / received but not yet read by the program',
          },
          {
            label: 'Resent',
            sort: (x: Row) => (x.retransmitsOut == null ? null : x.retransmitsOut + (x.retransmitsIn ?? 0)),
            right: true,
            width: '5.5rem',
            cell: (x: Row) => (x.retransmitsOut == null ? '–' : <><Count value={x.retransmitsOut} /> / <Count value={x.retransmitsIn} /></>),
            title: () => 'TCP segments sent again: by this container / by the other end',
          },
          { label: 'Out', sort: x => x.outBytesPerSecond, right: true, width: '5.5rem', cell: x => fmtRate(x.outBytesPerSecond) },
          { label: 'In', sort: x => x.inBytesPerSecond, right: true, width: '5.5rem', cell: x => fmtRate(x.inBytesPerSecond) },
          { label: 'Sent', sort: x => x.bytesOut, right: true, width: '5.5rem', cell: x => fmtBytes(x.bytesOut) },
          { label: 'Received', sort: x => x.bytesIn, right: true, width: '5.5rem', cell: x => fmtBytes(x.bytesIn) },
          { label: 'First seen', sort: x => -x.firstSeenSecondsAgo, right: true, width: '5.5rem', cell: x => ago(x.firstSeenSecondsAgo) },
          { label: 'Last seen', sort: x => -x.lastSeenSecondsAgo, right: true, width: '5.5rem', cell: x => ago(x.lastSeenSecondsAgo) },
          {
            label: 'Flags',
            sort: (x: Row) => Number(x.newDestination) * 2 + Number(x.internet),
            width: '9rem',
            cell: x => (
              <>
                {x.newDestination && <Badge warn>New destination</Badge>}
                {x.zone ? <Badge title={ZONES[x.zone]}>{x.zone === 'network' ? 'Same network' : x.zone === 'host' ? 'Host' : x.zone === 'private' ? 'Private' : 'Internet'}</Badge> : x.internet && <Badge>Internet</Badge>}
              </>
            ),
          },
        ]}
      />
    </Panel>
  );
}

function LookupsPanel({ c }: { c: Ctx }) {
  const all = rowsOf(c, n => n.lookups).filter(x => x.lastSeenSecondsAgo <= c.range).sort((a, b) => a.lastSeenSecondsAgo - b.lastSeenSecondsAgo);
  return (
    <Panel icon={<IconNetwork size={14} />} title="DNS lookups" help={HELP.lookups} subtitle={`Last ${heldSpan(c.range)}`}>
      <Table
        id="lookups"
        rows={all}
        rowKey={x => `${x.containerId}/${x.name}`}
        empty={captureOff(c) ? 'Needs packet capture.' : c.network ? 'No lookups yet.' : 'Loading…'}
        cols={[
          {
            label: 'Name',
            sort: (x: (typeof all)[number]) => x.name,
            cell: x => {
              const address = reverseOf(x.name);
              return address
                ? <span><span className="font-mono text-text">{address}</span><span className="ml-1.5 text-muted">reverse lookup</span></span>
                : <span className="text-text">{x.name}</span>;
            },
            title: x => `${x.name}${x.answers.length ? ` → ${x.answers.join(', ')}` : ''}`,
          },
          ...(c.scope.containerId ? [] : [{ label: 'Container', sort: (x: (typeof all)[number]) => containerName(c, x.containerId), width: '13rem', cell: (x: (typeof all)[number]) => <ContainerCell c={c} id={x.containerId} /> }]),
          {
            label: 'Result',
            sort: (x: (typeof all)[number]) => x.result,
            width: '9rem',
            cell: x => (x.result !== 'NOERROR' ? <Badge warn>{x.result}</Badge>
              : x.answers.some(a => !isAddress(a)) ? <span className="text-text">{x.answers.filter(a => !isAddress(a)).join(', ')}</span>
              : `${x.answers.length} address${x.answers.length === 1 ? '' : 'es'}`),
          },
          {
            label: 'Alias of',
            sort: (x: (typeof all)[number]) => x.aliases?.[x.aliases.length - 1],
            width: '13rem',
            cell: (x: (typeof all)[number]) => (x.aliases?.length ? x.aliases[x.aliases.length - 1] : '–'),
            title: (x: (typeof all)[number]) => (x.aliases?.length ? [x.name, ...x.aliases].join(' → ') : ''),
          },
          { label: 'Reply time', sort: (x: (typeof all)[number]) => x.latencyMs, right: true, width: '5.5rem', cell: (x: (typeof all)[number]) => fmtMs(x.latencyMs) },
          { label: 'First seen', sort: x => -x.firstSeenSecondsAgo, right: true, cell: x => ago(x.firstSeenSecondsAgo) },
          { label: 'Last seen', sort: x => -x.lastSeenSecondsAgo, right: true, cell: x => ago(x.lastSeenSecondsAgo) },
        ]}
      />
    </Panel>
  );
}

function ListeningPanel({ c }: { c: Ctx }) {
  const all = rowsOf(c, n => n.listening).sort((a, b) => Number(b.new) - Number(a.new) || a.port - b.port);
  const limit = c.network?.find(x => x.network.listenBacklogLimit != null)?.network.listenBacklogLimit;
  return (
    <Panel icon={<IconNetwork size={14} />} title="Listening ports" help={HELP.listening} subtitle="Ports accepting connections">
      <Table
        id="listening"
        rows={all}
        rowKey={x => `${x.containerId}/${x.protocol}/${x.address}/${x.port}`}
        empty={c.network ? 'Nothing listening.' : 'Loading…'}
        cols={[
          { label: 'Port', sort: x => x.port, width: '6rem', cell: x => <span className="font-mono text-text">{x.port}/{x.protocol}</span> },
          {
            label: 'Address',
            sort: x => x.address,
            cell: x => (
              <span>
                <span className="font-mono">{x.address}</span>
                <span className="ml-1.5 text-muted">{LOOPBACK.has(x.address) ? 'this container only' : ANY.has(x.address) ? 'all interfaces' : ''}</span>
              </span>
            ),
          },
          {
            label: 'Clients',
            sort: (x: (typeof all)[number]) => x.clients,
            right: true,
            width: '7rem',
            cell: (x: (typeof all)[number]) => x.clients ?? '–',
            title: () => 'Addresses that connected to it in the last hour',
          },
          {
            label: 'Reached from',
            sort: (x: (typeof all)[number]) => (x.internetClients ?? 0) * 2 + (x.hostClients || x.hostPorts?.length ? 1 : 0),
            width: '9rem',
            cell: (x: (typeof all)[number]) => (
              <>
                {x.hostPorts?.length
                  ? <Badge warn title={`Published on the host’s port ${x.hostPorts.join(', ')} (docker run -p), from Docker’s record of the container`}>host :{x.hostPorts.join(' :')}</Badge>
                  : x.hostClients ? <Badge warn title={hostPort(c, x) ? `From the host, on its port ${hostPort(c, x)}: the port its clients asked for (HTTP Host header)` : 'Connections came through the network’s gateway: from the host, which is how a published port (docker run -p) arrives'}>host{hostPort(c, x) ? ` :${hostPort(c, x)}` : ''}</Badge> : null}
                {x.internetClients ? <Badge warn title="Connections came from public addresses">internet</Badge> : null}
                {x.clients && x.clients > (x.hostClients ?? 0) + (x.internetClients ?? 0) ? <Badge title="Connections came from other containers or private addresses">network</Badge> : null}
              </>
            ),
          },
          ...(c.scope.containerId ? [] : [{ label: 'Container', sort: (x: (typeof all)[number]) => containerName(c, x.containerId), width: '13rem', cell: (x: (typeof all)[number]) => <ContainerCell c={c} id={x.containerId} /> }]),
          { label: 'Process', sort: x => x.process, cell: x => owner(x) },
          {
            label: 'Accept queue',
            sort: (x: (typeof all)[number]) => x.acceptQueue,
            right: true,
            width: '7.5rem',
            cell: (x: (typeof all)[number]) => (x.acceptQueue == null ? '–'
              : <span style={{ color: x.acceptQueue > 0 ? T.warn : undefined }}>{x.acceptQueue}{limit ? ` of ≤${limit}` : ''}</span>),
            title: () => 'Connections waiting for the program to accept them; no queue is longer than net.core.somaxconn',
          },
          { label: 'Flags', sort: x => Number(x.new), width: '4.5rem', cell: x => (x.new ? <Badge warn>New</Badge> : null) },
        ]}
      />
    </Panel>
  );
}

const ZONES = {
  network: 'On a network this container is attached to: another container, usually',
  host: 'The network’s gateway: the host, or what it forwards (published ports)',
  private: 'A private address it reaches through a gateway: the host, the LAN or another network',
  internet: 'A public address',
};
const LOOPBACK = new Set(['127.0.0.1', '::1']);
const ANY = new Set(['0.0.0.0', '::']);

const PROTOCOLS: Record<string, string> = {
  http: 'HTTP', postgres: 'PostgreSQL', mysql: 'MySQL', redis: 'Redis', mongodb: 'MongoDB', ldap: 'LDAP', sqlserver: 'SQL Server',
  cassandra: 'Cassandra', kafka: 'Kafka', mqtt: 'MQTT', memcached: 'Memcached', amqp: 'AMQP', smtp: 'SMTP', ftp: 'FTP', pop3: 'POP3', imap: 'IMAP',
};

function RequestsPanel({ c }: { c: Ctx }) {
  const all = rowsOf(c, n => n.requests ?? []).sort((a, b) => b.requestsPerSecond - a.requestsPerSecond);
  const latency = (v: number | null) => (v == null ? '–' : v >= 5000 ? '> 5 s' : `≤ ${fmtMs(v)}`);
  return (
    <Panel icon={<IconNetwork size={14} />} title="Requests" help={HELP.requests} sounding wide subtitle={captureOff(c) ? `Needs packet capture (${captureOff(c)})` : 'Decoded from the packets, last minute'}>
      <Table
        id="requests"
        sounding={x => x.process === 'sounding-agent'}
        rows={all}
        rowKey={x => `${x.containerId}/${x.protocol}/${x.role}/${x.peer}/${x.port}/${x.operation}`}
        empty={c.network ? `No requests in the last minute. Decoded: ${Object.values(PROTOCOLS).join(', ')} (not encrypted).` : 'Loading…'}
        cols={[
          { label: 'Protocol', sort: x => x.protocol, width: '7rem', cell: x => PROTOCOLS[x.protocol] ?? x.protocol },
          { label: 'Request', sort: x => x.operation, cell: x => <span className="font-mono text-text">{x.operation}</span>, title: x => x.operation },
          { label: 'Talking to', sort: x => x.peerName ?? x.peer, width: '13rem', cell: x => <span>{x.role === 'client' ? 'to ' : 'from '}<span className="text-text">{x.peerName ?? x.peer}</span>{x.role === 'client' && <span className="text-muted">:{x.port}</span>}</span>, title: x => (x.role === 'client' ? 'This container asks ' : 'This container answers ') + x.peer },
          {
            label: 'Process',
            sort: (x: (typeof all)[number]) => x.process,
            width: '10rem',
            cell: (x: (typeof all)[number]) => (x.process === 'sounding-agent' ? <><span className="mr-1.5">{x.process}</span><Badge>Sounding</Badge></> : x.process ?? '–'),
          },
          ...(c.scope.containerId ? [] : [{ label: 'Container', width: '13rem', sort: (x: (typeof all)[number]) => containerName(c, x.containerId), cell: (x: (typeof all)[number]) => <ContainerCell c={c} id={x.containerId} /> }]),
          { label: 'Per second', sort: x => x.requestsPerSecond, right: true, cell: x => fmt('perSecond', x.requestsPerSecond) },
          {
            label: 'Errors',
            sort: x => x.errorsPerSecond / Math.max(x.requestsPerSecond, 1e-9),
            right: true,
            cell: x => (x.errorsPerSecond > 0 ? <span style={{ color: T.warn }}>{fmtPct(pct(x.errorsPerSecond, x.requestsPerSecond))}</span> : '0%'),
            title: () => 'Requests that got an error reply',
          },
          { label: 'Median', sort: x => x.p50Ms, right: true, cell: x => latency(x.p50Ms), title: () => 'Half the requests were answered within this' },
          { label: '95th', sort: x => x.p95Ms, right: true, cell: x => latency(x.p95Ms), title: () => '95 in 100 requests were answered within this' },
          { label: 'Sent / received', sort: x => (x.requestBytes ?? 0) + (x.responseBytes ?? 0), right: true, width: '8rem', cell: x => `${fmtBytes(x.requestBytes, 0)} / ${fmtBytes(x.responseBytes, 0)}`, title: () => 'Average request and response size' },
          { label: 'Per connection', sort: x => x.requestsPerConnection, right: true, width: '7rem', cell: x => x.requestsPerConnection?.toLocaleString() ?? '–', title: () => 'Requests each connection carried: 1 means a new connection for every request' },
        ]}
      />
    </Panel>
  );
}

function CapturePanel({ c }: { c: Ctx }) {
  const id = c.scope.containerId!;
  const [asked, setAsked] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [opened, setOpened] = useState<number | null>(null);
  const captures = usePolled<CaptureInfo[]>(`api/containers/${encodeURIComponent(id)}/captures?v=${asked}`, 1500);
  const busy = captures?.some(x => x.status === 'requested' || x.status === 'running');
  const start = (seconds: number) => {
    setError(null);
    fetch(`api/containers/${encodeURIComponent(id)}/captures?seconds=${seconds}`, { method: 'POST' })
      .then(async response => { if (!response.ok) setError(await response.text()); setAsked(n => n + 1); })
      .catch(() => setError('The dashboard couldn’t be reached.'));
  };
  const off = captureOff(c);
  return (
    <Panel
      icon={<IconNetwork size={14} />} title="Packet capture" help={HELP.capture}
      wide
      subtitle={off ? `Not available: ${off}` : 'The first 128 bytes of each packet. Open one to read it here.'}
      actions={
        <div className="flex items-center gap-1">
          {[10, 30].map(seconds => (
            <button
              key={seconds}
              type="button"
              disabled={Boolean(busy || off)}
              onClick={() => start(seconds)}
              className="rounded border border-line bg-bg px-1.5 py-px text-[11px] font-medium text-text-2 hover:text-text disabled:opacity-40"
            >
              Capture {seconds} s
            </button>
          ))}
        </div>
      }
    >
      {error && <div className="mb-1.5" style={{ color: T.warn }}>{error}</div>}
      <Table
        id="captures"
        rows={captures ?? []}
        rowKey={x => String(x.id)}
        empty={captures ? 'No captures yet.' : 'Loading…'}
        onRow={x => { if (x.status === 'done') setOpened(opened === x.id ? null : x.id); }}
        cols={[
          { label: 'Started', sort: x => Date.parse(x.requestedAt), cell: x => new Date(x.requestedAt).toLocaleTimeString() },
          { label: 'Length', sort: x => x.seconds, right: true, cell: x => `${x.seconds} s` },
          { label: 'Packets', sort: x => x.packets, right: true, cell: x => x.packets?.toLocaleString() ?? '–' },
          {
            label: 'File',
            cell: x => (x.status === 'done'
              ? (
                <span className="flex gap-3">
                  <button type="button" className="text-usage hover:underline" onClick={e => { e.stopPropagation(); setOpened(opened === x.id ? null : x.id); }}>{opened === x.id ? 'Close' : 'Open'}</button>
                  <a className="text-muted hover:text-text hover:underline" href={`api/captures/${x.id}`} download>Download .pcap ({fmtBytes(x.bytes, 0)})</a>
                </span>
              )
              : x.status === 'failed' ? <span style={{ color: T.warn }}>failed</span>
              : <span className="text-muted">{x.status === 'running' ? 'capturing…' : 'waiting for the agent…'}</span>),
          },
        ]}
      />
      {opened != null && <div className="mt-2"><PacketViewer id={opened} /></div>}
    </Panel>
  );
}

// The host's port for a published one isn't visible inside the container, but an HTTP client names it in the
// request (Host: localhost:8088): taken from the connections that came through the gateway to this port.
function hostPort(c: Ctx, listener: { containerId: string; port: number }) {
  const network = c.network?.find(x => x.containerId === listener.containerId)?.network;
  for (const x of network?.connections ?? []) {
    const host = x.zone === 'host' && x.localPort === listener.port ? x.httpRequest?.split(' ')[1]?.split('/')[0] : undefined;
    const port = host?.match(/:(\d+)$/)?.[1];
    if (port) return port;
  }
  return null;
}

function reverseOf(name: string) {
  const suffix = '.in-addr.arpa';
  return name.endsWith(suffix) ? name.slice(0, -suffix.length).split('.').reverse().join('.') : null;
}

const isAddress = (s: string) => /^[\d.]+$/.test(s) || s.includes(':');

const fmtMs = (v: number | null | undefined) => (v == null ? '–' : `${v.toFixed(v >= 10 ? 0 : 1)} ms`);

function SocketsPanel({ c }: { c: Ctx }) {
  const interfaces: NetworkInterface[] = c.scope.containerId ? (c.w ? usageOf(c.w)?.detail?.interfaces ?? [] : []) : c.snapshot?.network.interfaces ?? [];
  const states = new Map<string, number>();
  for (const x of c.network ?? []) for (const [state, n] of Object.entries(x.network.socketStates ?? {})) states.set(state, (states.get(state) ?? 0) + n);
  const tcp = c.scope.containerId ? (c.w ? usageOf(c.w)?.detail?.tcp : null) : c.snapshot?.network.tcp;
  const conntrack = c.scope.containerId ? (c.w ? usageOf(c.w)?.detail?.conntrack : null) : c.snapshot?.network.conntrack;
  const rows = [...states].sort((a, b) => b[1] - a[1]).map(([state, n]): [string, string, boolean] => [state, n.toLocaleString(), state === 'CLOSE_WAIT' && n > 0]);
  const ephemeral = (c.network ?? []).map(x => x.network.ephemeral).filter(e => e != null).sort((a, b) => b.usedPercent - a.usedPercent)[0];
  return (
    <Panel icon={<IconNetwork size={14} />} title="Interfaces and connection states" help={HELP.socketStates} subtitle={c.scope.containerId ? 'This container, now' : 'The host’s interfaces; the containers’ sockets; the rates are the host’s own'}>
      <Table
        id="interfaces"
        rows={interfaces}
        rowKey={x => x.name}
        empty={c.scope.containerId ? 'No interfaces reported yet.' : c.snapshot?.network.error ?? 'No interfaces reported.'}
        cols={[
          { label: 'Interface', sort: x => x.name, cell: x => <span className="font-mono text-text">{x.name}</span> },
          { label: 'In', sort: x => x.receiveBytesPerSecond, right: true, cell: x => fmtRate(x.receiveBytesPerSecond) },
          { label: 'Out', sort: x => x.transmitBytesPerSecond, right: true, cell: x => fmtRate(x.transmitBytesPerSecond) },
          { label: 'Packets', sort: x => x.receivePacketsPerSecond + x.transmitPacketsPerSecond, right: true, width: '5.5rem', cell: x => `${x.receivePacketsPerSecond.toFixed(0)} / ${x.transmitPacketsPerSecond.toFixed(0)}`, title: () => 'In / out per second' },
          { label: 'Errors', sort: x => x.errorsTotal + x.dropsTotal, right: true, width: '5rem', cell: x => <><Count value={x.errorsTotal} /> / <Count value={x.dropsTotal} /></>, title: () => 'Errors / drops, in total' },
        ]}
      />
      <div className="mt-3" />
      <Facts rows={[
        ...(rows.length ? rows : [['Connections', c.network ? 'none' : '…'] as [string, string]]),
        ['Connections opened / accepted', tcp ? `${fmt('perSecond', tcp.activeOpensPerSecond)} / ${fmt('perSecond', tcp.passiveOpensPerSecond)}` : '–'],
        ['Accept queue overflows', tcp ? fmt('perSecond', tcp.listenDropsPerSecond) : '–', (tcp?.listenDropsPerSecond ?? 0) > 0],
        ['Connection tracking', conntrack ? `${conntrack.entries.toLocaleString()}${conntrack.max ? ` of ${conntrack.max.toLocaleString()}` : ''}` : NA,
          Boolean(conntrack?.max && conntrack.entries > conntrack.max * 0.8)],
        ['Local ports in use, busiest destination', ephemeral ? `${ephemeral.inUse.toLocaleString()} of ${ephemeral.range.toLocaleString()} (${ephemeral.destination})` : '–',
          (ephemeral?.usedPercent ?? 0) > 50],
      ]} />
    </Panel>
  );
}

const USER_COL: Col<ProcessUsage> = { label: 'User', sort: p => p.user, width: '5.5rem', cell: p => p.user ?? '–' };
const CPU_COL: Col<ProcessUsage> = { label: 'CPU', sort: p => p.cpuCores, right: true, cell: p => fmtCores(p.cpuCores) };
const MEMORY_COL: Col<ProcessUsage> = { label: 'Memory', sort: p => p.memoryBytes, right: true, cell: p => fmtBytes(p.memoryBytes) };
const THREADS_COL: Col<ProcessUsage> = { label: 'Threads', sort: p => p.threads, right: true, width: '4.5rem', cell: p => p.threads };
const perSecond = (v: number | null | undefined) => (v == null ? '–' : v.toFixed(v >= 10 ? 0 : 1));
const STATES: Record<string, string> = { R: 'running', S: 'sleeping', D: 'waiting on I/O (uninterruptible)', Z: 'zombie: exited, not reaped by its parent', T: 'stopped', I: 'idle' };
const SHELLS = new Set(['sh', 'bash', 'ash', 'dash', 'zsh', 'ksh', 'fish']);

const PORTS_COL: Col<ProcessUsage> = {
  label: 'Ports', sort: p => p.ports?.[0], width: '6.5rem',
  cell: p => (p.ports?.length ? <span className="font-mono">{p.ports.join(', ')}</span> : '–'),
  title: p => (p.ports?.length ? `Listening on ${p.ports.join(', ')}` : ''),
};
const CONNECTIONS_COL: Col<ProcessUsage> = {
  label: 'Connections', sort: p => p.connections, right: true, width: '6.5rem', cell: p => p.connections ?? '–', title: () => 'Open network connections it holds',
};
const FLAGS_COL: Col<ProcessUsage> = {
  label: 'Flags', width: '9rem',
  sort: p => (p.capabilities?.length ?? 0) * 4 + Number(Boolean(p.unconfined)) * 2 + Number(p.user === 'root'),
  cell: p => (
    <>
      {p.user === 'root' && !p.sounding && <Badge title="Runs as root inside the container">root</Badge>}
      {p.capabilities?.length ? <Badge warn title={`Capabilities beyond Docker’s default: ${p.capabilities.join(', ')}`}>+{p.capabilities.length} privileges</Badge> : null}
      {p.unconfined && <Badge warn title="No seccomp filter: every system call is allowed">unrestricted</Badge>}
      {SHELLS.has(p.name) && <Badge title="A shell">shell</Badge>}
    </>
  ),
};

const CPU_COLS: Col<ProcessUsage>[] = [
  USER_COL, CPU_COL,
  { label: 'Waiting', sort: p => p.waitMillisecondsPerSecond, right: true, width: '5.5rem', cell: p => fmt('ms', p.waitMillisecondsPerSecond), title: () => 'Time ready to run but waiting for a CPU' },
  { label: 'Interrupted', sort: p => p.involuntarySwitchesPerSecond, right: true, width: '5.5rem', cell: p => perSecond(p.involuntarySwitchesPerSecond), title: p => `Taken off the CPU ${perSecond(p.involuntarySwitchesPerSecond)}/s, gave it up ${perSecond(p.voluntarySwitchesPerSecond)}/s` },
  { label: 'State', sort: p => p.state, width: '4rem', cell: p => <span style={{ color: p.state === 'D' || p.state === 'Z' ? T.warn : undefined }}>{p.state ?? '–'}</span>, title: p => STATES[p.state ?? ''] ?? '' },
  MEMORY_COL, THREADS_COL, PORTS_COL, CONNECTIONS_COL, FLAGS_COL,
];
const MEMORY_COLS: Col<ProcessUsage>[] = [
  USER_COL, CPU_COL, MEMORY_COL,
  { label: 'Program data', sort: p => p.anonBytes, right: true, cell: p => fmtBytes(p.anonBytes), title: () => 'Its own data: heap and stacks' },
  { label: 'Code & files', sort: p => p.fileBytes, right: true, cell: p => fmtBytes(p.fileBytes), title: p => `Mapped files: code and libraries${p.shmemBytes ? `; ${fmtBytes(p.shmemBytes)} shared memory besides` : ''}` },
  { label: 'Swapped', sort: p => p.swapBytes, right: true, cell: p => fmtBytes(p.swapBytes), title: () => 'Moved out to swap' },
  { label: 'Own share', sort: p => p.pssBytes, right: true, cell: p => fmtBytes(p.pssBytes), title: () => 'Proportional set size: memory with shared pages split among the processes sharing them' },
  { label: 'OOM score', sort: p => p.oomScore, right: true, width: '5.5rem', cell: p => p.oomScore ?? '–', title: () => 'Higher is killed first when memory runs out (0 to 1000)' },
  THREADS_COL,
];
const DISK_COLS: Col<ProcessUsage>[] = [
  USER_COL,
  { label: 'Read', sort: p => p.readBytesPerSecond, right: true, cell: p => fmtRate(p.readBytesPerSecond) },
  { label: 'Write', sort: p => p.writeBytesPerSecond, right: true, cell: p => fmtRate(p.writeBytesPerSecond) },
  {
    label: 'Open files',
    sort: p => p.openFiles,
    right: true,
    width: '7rem',
    cell: p => (p.openFiles == null ? '–' : <span style={{ color: p.openFilesLimit && p.openFiles > p.openFilesLimit * 0.8 ? T.warn : undefined }}>{p.openFiles.toLocaleString()}</span>),
    title: p => (p.openFilesLimit ? `limit ${p.openFilesLimit.toLocaleString()}` : ''),
  },
];

type ProcessMode = 'window' | 'now';

interface WindowView { sort: (p: ProcessWindow) => number; cols: Col<ProcessWindow>[] }

interface TreeRow { pid: number; parentPid: number | null; containerId: string; commandLine: string | null }

// Rows that are another row's child go under it. The launcher at the top holds the container's own command,
// so that command and its children start at the top level too; the agent stays under the launcher.
function processTree<P extends TreeRow>(rows: P[]) {
  const key = (p: { containerId: string; pid: number }) => `${p.containerId}/${p.pid}`;
  const byKey = new Map(rows.map(p => [key(p), p]));
  const children = new Map<string, P[]>();
  const roots: P[] = [];
  for (const p of rows) {
    const parent = p.parentPid != null ? byKey.get(`${p.containerId}/${p.parentPid}`) : undefined;
    const launcher = parent && parent.commandLine?.startsWith('/opt/sounding/run') && !byKey.has(`${p.containerId}/${parent.parentPid}`);
    if (!parent || (launcher && !p.commandLine?.startsWith('sounding-agent '))) roots.push(p);
    else children.set(key(parent), [...(children.get(key(parent)) ?? []), p]);
  }
  return { roots, childrenOf: (p: P) => children.get(key(p)) ?? [] };
}

const isSounding = (p: { sounding?: boolean | null; commandLine: string | null }) =>
  Boolean(p.sounding) || Boolean(p.commandLine?.startsWith('/opt/sounding/') || p.commandLine?.startsWith('sounding-agent '));

// A process's busiest threads, as rows under it.
const threadRows = (p: ProcessUsage & { containerId: string }) =>
  (p.hotThreads ?? []).map(t => ({ containerId: p.containerId, pid: t.tid, parentPid: p.pid, name: t.name, cpuCores: t.cpuCores, user: p.user, thread: true }) as unknown as ProcessUsage & { containerId: string });

function ProcessPanel({ c, sort, title, gpu = false, cols, help, window }: {
  c: Ctx;
  sort: (p: ProcessUsage) => number;
  title: string;
  gpu?: boolean;
  cols?: Col<ProcessUsage>[];
  help?: Help;
  window?: WindowView;
}) {
  const LIMIT = 50;
  const mine = <T extends { containerId: string }>(x: T) => !c.scope.containerId || x.containerId === c.scope.containerId;
  const fleetContainer = (id: string) => c.h.containers.find(x => x.containerId === id);
  const head = [
    { label: 'PID', sort: (p: { pid: number }) => p.pid, right: true, width: '6rem', cell: (p: { pid: number }) => p.pid },
    {
      label: 'Name',
      width: '14rem',
      sort: (p: { name: string }) => p.name,
      cell: (p: { name: string; running?: boolean; threadName?: string | null; sounding?: boolean | null; thread?: boolean; commandLine: string | null }) => (
        <span>
          <span className={p.running === false || p.thread ? 'text-muted' : 'text-text'}>{p.name}</span>
          {p.threadName && <span className="ml-1.5 text-muted" title="The thread’s name, which the program set">{p.threadName}</span>}
          {p.thread && <span className="ml-1.5"><Badge>thread</Badge></span>}
          {isSounding(p) && <span className="ml-1.5"><Badge title="Part of Sounding: the launcher, the agent or its helpers">Sounding</Badge></span>}
        </span>
      ),
      title: (p: { name: string; commandLine: string | null }) => p.commandLine ?? p.name,
    },
    ...(c.scope.containerId ? [] : [{ label: 'Container', sort: (p: { containerId: string }) => containerName(c, p.containerId), width: '13rem', cell: (p: { containerId: string }) => (fleetContainer(p.containerId) ? <ContainerId h={c.h} w={fleetContainer(p.containerId)!} /> : p.containerId) }]),
  ];
  const command = { label: 'Command', sort: (p: { commandLine: string | null }) => p.commandLine, cell: (p: { commandLine: string | null }) => <span className="block max-w-[28rem] truncate text-muted">{p.commandLine ?? ''}</span>, title: (p: { commandLine: string | null }) => p.commandLine ?? '' };
  const hovered = window && c.processesAt && c.frame.hover != null ? c.frame.hover : null;
  const mode = !window ? 'now' : hovered != null ? 'at' : c.processMode;
  const where = c.scope.containerId ? 'In this container' : 'In every container on this host';

  const switcher = window && (
    <div className="flex items-center rounded border border-line bg-bg p-0.5 text-[11px]" role="group" aria-label="Processes over">
      {(['window', 'now'] as const).map(m => (
        <button
          key={m}
          type="button"
          onClick={() => c.onProcessMode(m)}
          aria-pressed={c.processMode === m}
          className={`rounded-xs px-1.5 py-px font-medium ${c.processMode === m ? 'bg-surface-3 text-text' : 'text-muted hover:text-text-2'}`}
        >
          {m === 'window' ? `Last ${span(c.range)}` : 'Now'}
        </button>
      ))}
    </div>
  );

  if (mode === 'window' && window) {
    const rows = (c.processWindow ?? [])
      .filter(mine)
      .flatMap(x => x.processes.map(p => ({ ...p, containerId: x.containerId })))
      .filter(p => !gpu || p.averageGpuPercent != null || p.peakGpuMemoryBytes != null)
      .sort((a, b) => window.sort(b) - window.sort(a));
    const exited = rows.filter(p => !p.running).length;
    const tree = processTree(rows);
    return (
      <Panel
        icon={<IconTopology size={14} />} title={title} help={help ?? (gpu ? HELP.gpuProcesses : HELP.processes)} wide actions={switcher} sounding
        subtitle={`${where}, last ${span(c.range)}${exited ? ` · ${exited} exited` : ''}${tree.roots.length > LIMIT ? ` · top ${LIMIT} of ${tree.roots.length}` : ''}`}
      >
        <Table
          id={`processes:${title}:window`}
          sounding={isSounding}
          rows={tree.roots}
          childrenOf={tree.childrenOf}
          limit={LIMIT}
          rowKey={p => `${p.containerId}/${p.pid}/${p.startedAt}`}
          empty={c.processWindow ? (gpu ? 'No CUDA processes in this window.' : 'No processes in this window yet.') : 'Loading…'}
          cols={[...head, ...window.cols, STATUS_COL, command] as Col<(typeof rows)[number]>[]}
        />
      </Panel>
    );
  }

  const source = mode === 'at' ? c.processesAt : c.processes;
  const all = (source ?? [])
    .filter(mine)
    .flatMap(x => x.processes.map(p => ({ ...p, containerId: x.containerId })))
    .filter(p => !gpu || p.gpuMemoryBytes != null || p.gpuPercent != null)
    .sort((a, b) => sort(b) - sort(a));
  const tree = processTree(all);
  const withThreads = cols === CPU_COLS && mode === 'now';

  return (
    <Panel
      icon={<IconTopology size={14} />}
      title={title}
      help={help ?? (gpu ? HELP.gpuProcesses : HELP.processes)}
      actions={switcher}
      sounding
      subtitle={`${where}${hovered != null ? `, at ${new Date(hovered).toLocaleTimeString()} (the hovered moment)` : ''}${tree.roots.length > LIMIT ? ` · top ${LIMIT} of ${tree.roots.length}` : ''}`}
      wide
    >
      <Table
        id={`processes:${title}`}
        sounding={isSounding}
        rows={tree.roots}
        childrenOf={p => [...tree.childrenOf(p), ...(withThreads ? threadRows(p) : [])]}
        limit={LIMIT}
        rowKey={p => `${p.containerId}/${p.pid}${'thread' in p ? '/thread' : ''}`}
        empty={source ? (gpu ? 'No CUDA processes running.' : mode === 'at' ? 'No process sample near this moment.' : 'No processes reported.') : 'Loading…'}
        cols={[
          ...head,
          ...(cols ?? (gpu
            ? [
                { label: 'Compute', sort: (p: ProcessUsage) => p.gpuPercent, right: true, width: '5.5rem', cell: (p: ProcessUsage) => fmtPct(p.gpuPercent) },
                { label: 'VRAM', sort: (p: ProcessUsage) => p.gpuMemoryBytes, right: true, cell: (p: ProcessUsage) => fmtBytes(p.gpuMemoryBytes) },
                { label: 'Peak VRAM', sort: (p: ProcessUsage) => p.gpuMemoryPeakBytes, right: true, cell: (p: ProcessUsage) => fmtBytes(p.gpuMemoryPeakBytes) },
              ]
            : [USER_COL, CPU_COL, MEMORY_COL, THREADS_COL])),
          command,
        ] as Col<(typeof all)[number]>[]}
      />
    </Panel>
  );
}

const W_USER: Col<ProcessWindow> = { label: 'User', sort: p => p.user, width: '5.5rem', cell: p => p.user ?? '–' };
const W_AVG_CPU: Col<ProcessWindow> = { label: 'Avg CPU', sort: p => p.averageCores, right: true, cell: p => fmtCores(p.averageCores), title: () => 'CPU time used in the window over the time the process was seen' };
const W_PEAK_CPU: Col<ProcessWindow> = { label: 'Peak CPU', sort: p => p.peakCores, right: true, cell: p => fmtCores(p.peakCores), title: () => 'The highest one-second CPU use sampled' };
const W_PEAK_MEMORY: Col<ProcessWindow> = { label: 'Peak memory', sort: p => p.peakMemoryBytes, right: true, cell: p => fmtBytes(p.peakMemoryBytes) };
const STATUS_COL: Col<ProcessWindow> = {
  label: 'Status',
  sort: p => (p.running ? Number.MAX_SAFE_INTEGER : Date.parse(p.lastSeen)),
  width: '7.5rem',
  cell: p => (p.running ? 'running' : <span className="text-muted">exited {ago((Date.now() - Date.parse(p.lastSeen)) / 1000)}</span>),
  title: p => `seen ${new Date(p.firstSeen).toLocaleTimeString()} to ${new Date(p.lastSeen).toLocaleTimeString()}${p.startedAt ? `, started ${new Date(p.startedAt).toLocaleString()}` : ''}`,
};
const CPU_WINDOW: WindowView = { sort: p => p.averageCores ?? p.peakCores, cols: [W_USER, W_AVG_CPU, W_PEAK_CPU, W_PEAK_MEMORY] };
const MEMORY_WINDOW: WindowView = {
  sort: p => p.peakMemoryBytes,
  cols: [
    W_USER, W_PEAK_MEMORY,
    { label: 'Memory', sort: p => p.memoryBytes, right: true, cell: p => fmtBytes(p.memoryBytes), title: () => 'At its last sample' },
    {
      label: 'Growth',
      sort: p => p.memoryGrowthBytesPerHour,
      right: true,
      cell: p => (p.memoryGrowthBytesPerHour == null ? '–'
        : <span style={{ color: p.memoryGrowthBytesPerHour > 50 * 1024 * 1024 ? T.warn : undefined }}>{p.memoryGrowthBytesPerHour >= 0 ? '+' : '−'}{fmtBytes(Math.abs(p.memoryGrowthBytesPerHour))}/h</span>),
      title: () => 'The trend of its memory over the window (from a few minutes of samples): steady growth is how a leak looks',
    },
    W_AVG_CPU,
  ],
};
const GPU_WINDOW: WindowView = {
  sort: p => p.averageGpuPercent ?? -1,
  cols: [
    { label: 'Avg compute', sort: p => p.averageGpuPercent, right: true, width: '6.5rem', cell: p => fmtPct(p.averageGpuPercent) },
    { label: 'Peak VRAM', sort: p => p.peakGpuMemoryBytes, right: true, cell: p => fmtBytes(p.peakGpuMemoryBytes) },
    W_AVG_CPU,
  ],
};
const DISK_WINDOW: WindowView = {
  sort: p => (p.readBytes ?? 0) + (p.writeBytes ?? 0),
  cols: [
    W_USER,
    { label: 'Read', sort: p => p.readBytes, right: true, cell: p => fmtBytes(p.readBytes), title: p => (p.readBytes == null ? 'Another user’s process: not readable' : 'Read from storage in the window') },
    { label: 'Written', sort: p => p.writeBytes, right: true, cell: p => fmtBytes(p.writeBytes), title: p => (p.writeBytes == null ? 'Another user’s process: not readable' : 'Written to storage in the window') },
  ],
};

function useDebounced<T>(value: T, ms: number): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), ms);
    return () => clearTimeout(timer);
  }, [value, ms]);
  return settled;
}

function useFetched<T>(url: string | null): T | null {
  const [value, setValue] = useState<T | null>(null);
  useEffect(() => {
    if (!url) return;
    const controller = new AbortController();
    fetch(url, { signal: controller.signal })
      .then(response => (response.ok ? response.json() : null))
      .then(v => { if (v) setValue(v); })
      .catch(() => { /* keep the last answer */ });
    return () => controller.abort();
  }, [url]);
  return url ? value : null;
}

function MainChart({ c, id, icon }: { c: Ctx; id: string; icon: ReactNode }) {
  const rows = useMemo(() => chartRows(c.data.t, c.data.columns, c.bucketSeconds, c.start), [c.data, c.bucketSeconds, c.version, c.start]);
  const trend = c.data.trends.find(t => t.id === id);
  const card = c.cards.find(x => x.id === (id === 'disk' ? 'storage' : id));
  const help = overviewHelp(id, c.scope, wslVm(c.h));
  return (
    <div className="grid min-h-[280px]">
      <PerfTile icon={icon} help={help} card={card} trend={trend} rows={rows} at={nearest(rows, c.frame.hover)} ticks={c.frame.ticks} start={c.start} end={c.end} rangeSeconds={c.range} onHover={c.frame.onHover} syncByTime />
    </div>
  );
}

const NA = 'Not available';

function hostCountersGap(c: Ctx) {
  const network = c.snapshot?.network;
  return c.scope.containerId || !network || network.tcp ? undefined : NA;
}

function notReported(c: Ctx, live: unknown, values: Column | undefined, why: string) {
  if (!c.hist || (c.scope.containerId ? !c.w?.usage?.detail : !c.snapshot)) return undefined;
  return live == null && !values?.some(v => v != null) ? `${NA}: ${why}.` : undefined;
}

interface Line { label: string; values: Column | undefined; color?: string; fill?: boolean }

function DetailChart({ frame, hist, icon, label, help, info, unit, series, refs = [], empty, wide = false, floor }: {
  frame: Frame;
  hist: SeriesHistory | null;
  icon: ReactNode;
  label: string;
  help: Help;
  info: string[];
  unit: Unit;
  series: Line[];
  refs?: Ref[];
  empty?: string;
  wide?: boolean;
  floor?: number;
}) {
  const columns = Object.fromEntries(series.map((s, i) => [`s${i}`, s.values ?? []]));
  const rows = hist ? chartRows(hist.t, columns, hist.bucketSeconds, frame.start) : [];
  const hasData = rows.some(row => series.some((_, i) => row[`s${i}`] != null));
  const last = nearest(rows, null)?.s0 ?? null;
  const trend: Trend = {
    id: label,
    title: label,
    unit,
    series: series.map((s, i) => ({ key: `s${i}`, label: s.label, color: s.color ?? (i === 0 ? T.usage : T.second), fill: s.fill ?? i === 0 })),
    refs,
    fixedMax: null,
    empty: empty ?? (!hist ? 'Loading…' : hasData ? null : 'No samples in this window yet.'),
    floor,
  };
  const card: Card = { id: label, label, value: fmt(unit, last), lines: info, bullet: null };
  return (
    <div className={`grid min-h-[280px] ${wide ? 'md:col-span-2' : ''}`}>
      <PerfTile icon={icon} help={help} card={card} trend={trend} rows={rows} at={nearest(rows, frame.hover)} ticks={frame.ticks} start={frame.start} end={frame.end} rangeSeconds={frame.range} onHover={frame.onHover} syncByTime />
    </div>
  );
}

function ContainerId({ h, w }: { h: HostSummary; w: ContainerState }) {
  const i = liveContainers(h).findIndex(x => x.containerId === w.containerId);
  return (
    <span className="inline-flex items-center gap-1.5">
      {i >= 0 ? <span className="size-2 shrink-0 rounded-xs" style={{ background: PALETTE[i % PALETTE.length] }} /> : <StatusDot status={w.status} />}
      <span className={w.status === 'online' ? 'text-text' : 'text-muted'} title={`${fullNameOf(w)} (${w.containerId})`}>{nameOf(w)}</span>
    </span>
  );
}

function CoreGrid({ cores, allowed }: { cores: number[]; allowed: Set<number> | null }) {
  if (!cores.length) return <div className="text-muted">Per-core load not reported.</div>;
  return (
    <div>
      <div className="grid gap-0.5" style={{ gridTemplateColumns: `repeat(${Math.min(16, cores.length)}, minmax(0, 1fr))` }}>
        {cores.map((v, i) => (
          <div
            key={i}
            title={`Core ${i}: ${fmtPct(v)}`}
            className="flex h-8 flex-col items-center justify-center rounded-[2px] font-mono text-[9px] leading-tight"
            style={{ background: heat(v), opacity: allowed && !allowed.has(i) ? 0.2 : 1, color: v > 50 ? T.text2 : undefined }}
          >
            <span className="text-muted">{i}</span>
            <span className="text-text">{v.toFixed(0)}</span>
          </div>
        ))}
      </div>
      <div className="mt-1.5 flex items-center justify-end gap-1 text-[10px] text-muted">
        <span>0%</span>
        <span className="h-1 w-10 rounded-full" style={{ background: `linear-gradient(90deg, ${HEAT_RAMP.join(',')})` }} />
        <span>100%</span>
      </div>
    </div>
  );
}

const Empty = ({ children }: { children: ReactNode }) => (
  <div className="flex flex-1 items-center justify-center text-xs text-muted">{children}</div>
);

const col = (h: SeriesHistory | null, name: string): Column | undefined => h?.columns[name];

function across(h: SeriesHistory | null, prefix: string, op: 'max' | 'sum'): Column | undefined {
  if (!h) return undefined;
  const columns = Object.entries(h.columns).filter(([k]) => k.startsWith(`${prefix}:`)).map(([, v]) => v);
  if (!columns.length) return undefined;
  return h.t.map((_, i) => {
    let out: number | null = null;
    for (const column of columns) {
      const v = column[i];
      if (v != null) out = out == null ? v : op === 'max' ? Math.max(out, v) : out + v;
    }
    return out;
  });
}

const add = (a: Column | undefined, b: Column | undefined): Column | undefined =>
  a && b ? a.map((v, i) => (v == null && b[i] == null ? null : (v ?? 0) + (b[i] ?? 0))) : a ?? b;

function nearest(rows: ChartRow[], t: number | null): ChartRow | undefined {
  if (t == null) return [...rows].reverse().find(row => Object.entries(row).some(([k, v]) => k !== 't' && v != null)) ?? rows[rows.length - 1];
  let best = rows[0];
  for (const row of rows) if (Math.abs(row.t - t) < Math.abs((best?.t ?? 0) - t)) best = row;
  return best;
}

function parseCpuset(text: string | null | undefined): Set<number> | null {
  if (!text) return null;
  const out = new Set<number>();
  for (const part of text.split(',')) {
    const [a, b] = part.split('-').map(Number);
    if (Number.isNaN(a)) continue;
    for (let i = a; i <= (Number.isNaN(b) || b == null ? a : b); i++) out.add(i);
  }
  return out;
}
