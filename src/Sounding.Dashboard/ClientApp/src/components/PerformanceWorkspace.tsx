import { type ReactNode, useMemo, useState } from 'react';
import { Area, AreaChart, CartesianGrid, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { RANGES, type Resolved, type Scope, type View } from '../data';
import { fmtBytes, fmtClock, fmtCores, fmtDuration, fmtPct, fmtRate, niceCeil, niceCeilBytes, pct, sum } from '../format';
import { ago, allocated, fullNameOf, nameOf, busyCores, containerVolume, fmt, gpuAverage, hostVolumes, type Bullet, type Card, type Trend, type TrendData, type Unit, usageOf, vram, wslVm } from '../models';
import { FREE_SWATCH, HEAT_RAMP, heat, levelColor, levelOf, OTHER_GREY, PALETTE, T, THRESHOLDS } from '../theme';
import type { HostSample, HostSummary, ContainerState } from '../types';
import { IconChevronRight, IconCpu, IconGpu, IconMemory, IconNetwork, IconStorage, IconTopology } from './Icons';
import { BarTip, BulletBar, HelpTip, LevelText, statusText, type TipRow, WhileVisible } from './ui';
import { type Help, overviewHelp } from '../help';

export type ChartRow = { t: number; [key: string]: number | null };

export function chartRows(t: number[], columns: Record<string, (number | null)[]>, bucketSeconds: number, start: number): ChartRow[] {
  const keys = Object.keys(columns);
  const gap = Math.max(5000, bucketSeconds * 2500);
  const out: ChartRow[] = [];
  t.forEach((time, i) => {
    if (time < start - 60_000) return;
    const previous = out[out.length - 1];
    if (previous && time - previous.t > gap) {
      out.push({ t: previous.t + 1, ...Object.fromEntries(keys.map(k => [k, null])) });
    }
    out.push({ t: time, ...Object.fromEntries(keys.map(k => [k, columns[k][i] ?? null])) });
  });
  return out;
}

export function timeTicks(start: number, end: number, range: number): number[] {
  const steps = [15e3, 30e3, 60e3, 120e3, 300e3, 600e3, 900e3, 1800e3, 3600e3, 7200e3, 10800e3, 21600e3].filter(s => range <= 60 || s >= 60e3);
  const step = steps.find(s => (end - start) / s <= 5) ?? steps[steps.length - 1];
  // Steps of an hour and more fall on the viewer's clock (00:00, 06:00, ...), whatever its offset from UTC.
  const shift = step >= 3600e3 ? new Date(end).getTimezoneOffset() * 60e3 : 0;
  const out: number[] = [];
  for (let t = Math.ceil((start - shift) / step) * step + shift; t <= end; t += step) out.push(t);
  return out;
}

export function PerformanceWorkspace({
  r,
  scope,
  onScope,
  cards,
  data,
  detail,
  bucketSeconds,
  version,
  start,
  end,
  range,
  onRange,
  title,
  headerExtra,
  headerEnd,
  onOpen,
  body,
  help = false,
}: {
  r: Resolved;
  scope: Scope;
  onScope: (s: Scope) => void;
  cards: Card[];
  data: TrendData;
  detail?: HostSample;
  bucketSeconds: number;
  version: number;
  start: number;
  end: number;
  range: number;
  onRange: (seconds: number) => void;
  title?: ReactNode;
  headerExtra?: ReactNode;
  headerEnd?: ReactNode;
  onOpen?: (view: View) => void;
  body?: ReactNode;
  help?: boolean;
}) {
  const [hover, setHover] = useState<number | null>(null);

  const isScopeOnline = !scope.hostId
    ? r.hosts.some(x => x.online)
    : !scope.containerId
      ? Boolean(r.host?.online)
      : r.container?.status === 'online';

  const rows = useMemo(() => chartRows(data.t, data.columns, bucketSeconds, start), [data, bucketSeconds, version, start]);
  const ticks = useMemo(() => timeTicks(start, end, range), [start, end, range]);

  const at = useMemo(() => {
    if (hover == null) {
      if (!isScopeOnline) return undefined;
      return rows[rows.length - 1];
    }
    let best = rows[0];
    for (const row of rows) if (Math.abs(row.t - hover) < Math.abs((best?.t ?? 0) - hover)) best = row;
    return best;
  }, [rows, hover, isScopeOnline]);

  const cardById = (id: string) => cards.find(c => c.id === id);
  const open = (view: View) => (onOpen && scope.hostId ? () => onOpen(view) : undefined);
  const helpFor = (id: string) => (help ? overviewHelp(id, scope, Boolean(r.host && wslVm(r.host))) : undefined);
  const trendById = (id: string) => data.trends.find(t => t.id === id);

  const h = r.host;
  const w = r.container;

  const headerFacts = useMemo(() => {
    if (!scope.hostId) {
      const on = r.hosts.filter(x => x.online);
      const containers = r.hosts.flatMap(x => x.containers);
      return `${on.length}/${r.hosts.length} hosts online · ${containers.filter(x => x.status === 'online').length}/${containers.length} containers · ${sum(on, x => x.logicalCores)} cores · ${fmtBytes(sum(on, x => x.memoryTotalBytes))} RAM`;
    }
    if (!h) return 'Host not reported';
    if (!scope.containerId) {
      const gpuText = [...new Set(h.gpus.map(g => g.name))].map(name => { const n = h.gpus.filter(g => g.name === name).length; return n > 1 ? `${n} × ${name}` : name; }).join(' + ') || null;
      return [
        !h.online ? 'Not reporting' : null,
        detail?.snapshot.cpu?.model,
        h.logicalCores ? `${h.logicalCores} cores` : null,
        h.memoryTotalBytes ? `${fmtBytes(h.memoryTotalBytes)} RAM${wslVm(h) ? ' (Docker VM, WSL2)' : ''}` : null,
        gpuText,
        h.uptimeSeconds != null ? `up ${fmtDuration(h.uptimeSeconds)}` : null,
      ]
        .filter(Boolean)
        .join(' · ');
    }
    if (!w) return 'Container not reported';
    const u = usageOf(w);
    const limits = [u?.cpuLimitCores ? `${u.cpuLimitCores} CPU` : null, u?.memoryLimitBytes ? fmtBytes(u.memoryLimitBytes, 0) : null].filter(Boolean);
    return [
      w.status === 'online' ? `Running for ${fmtDuration((Date.now() - new Date(w.startedAt).getTime()) / 1000)}` : `${statusText[w.status]} ${ago(w.lastSeen)}`,
      w.status !== 'online' ? exitText(w) : null,
      w.group,
      u ? (limits.length ? `limit ${limits.join(' · ')}` : 'no limits') : null,
      w.restarts ? `restarted ${w.restarts}×` : null,
    ]
      .filter(Boolean)
      .join(' · ');
  }, [r, scope, h, w, detail]);

  const headerTitle = w && scope.containerId
    ? [`${fullNameOf(w)} (${w.containerId})`, `started ${new Date(w.startedAt).toLocaleString()}`, exitDetail(w)].filter(Boolean).join('\n')
    : headerFacts;

  return (
    <div className="flex min-w-0 flex-1 flex-col lg:h-screen lg:overflow-hidden">
      <header className="flex shrink-0 flex-wrap items-center justify-between gap-x-3 gap-y-2 border-b border-line bg-surface px-4 py-2">
        <div className="min-w-0 max-w-full">
          <div className="flex min-w-0 items-baseline gap-2.5">
            {title ?? (
              <h1 className="truncate text-sm font-semibold text-text">
                {!scope.hostId ? 'All Hosts' : !scope.containerId ? h?.displayName ?? scope.hostId : `${h?.displayName} / ${w ? nameOf(w) : scope.containerId}`}
              </h1>
            )}
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
          {headerExtra}
          <span className="hidden whitespace-nowrap font-mono text-xs text-muted sm:inline">
            {hover != null && at ? fmtClock(at.t, true) : 'Real-time'}
          </span>
          <div className="flex items-center rounded border border-line bg-bg p-0.5 text-xs" role="group" aria-label="Time range">
            {RANGES.map(item => (
              <button
                key={item.seconds}
                type="button"
                onClick={() => onRange(item.seconds)}
                aria-pressed={item.seconds === range}
                className={`rounded-xs px-2 py-0.5 font-medium transition ${
                  item.seconds === range ? 'bg-surface-3 text-text' : 'text-muted hover:text-text-2'
                }`}
              >
                {item.label}
              </button>
            ))}
          </div>
          {headerEnd}
        </div>
      </header>
      <div className="shrink-0 border-b border-line bg-surface-2/40 px-4 py-1 text-[11px] text-muted sm:truncate" title={headerTitle}>
        {headerFacts}
      </div>

      {body ?? (
      <main className="grid min-h-0 flex-1 grid-cols-1 gap-2.5 overflow-y-auto p-2.5 md:grid-cols-2 lg:grid-rows-3 lg:overflow-hidden">
        <PerfTile
          icon={<IconCpu size={14} />}
          card={cardById('cpu')}
          onOpen={open('cpu')}
          help={helpFor('cpu')}
          trend={trendById('cpu')}
          rows={rows}
          at={at}
          ticks={ticks}
          start={start}
          end={end}
          rangeSeconds={range}
          onHover={setHover}
        >
          <CpuHardwareFooter r={r} scope={scope} detail={detail} />
        </PerfTile>

        <PerfTile
          icon={<IconMemory size={14} />}
          card={cardById('memory')}
          onOpen={open('memory')}
          help={helpFor('memory')}
          trend={trendById('memory')}
          rows={rows}
          at={at}
          ticks={ticks}
          start={start}
          end={end}
          rangeSeconds={range}
          onHover={setHover}
        >
          <MemoryHardwareFooter r={r} scope={scope} detail={detail} />
        </PerfTile>

        <PerfTile
          icon={<IconGpu size={14} />}
          card={cardById('gpu')}
          onOpen={open('gpu')}
          help={helpFor('gpu')}
          trend={trendById('gpu')}
          rows={rows}
          at={at}
          ticks={ticks}
          start={start}
          end={end}
          rangeSeconds={range}
          onHover={setHover}
        >
          <GpuHardwareFooter r={r} scope={scope} detail={detail} />
        </PerfTile>

        <PerfTile
          icon={<IconStorage size={14} />}
          card={cardById('storage')}
          onOpen={open('disk')}
          help={helpFor('disk')}
          trend={trendById('disk')}
          rows={rows}
          at={at}
          ticks={ticks}
          start={start}
          end={end}
          rangeSeconds={range}
          onHover={setHover}
        >
          <DiskHardwareFooter r={r} scope={scope} detail={detail} />
        </PerfTile>

        <PerfTile
          icon={<IconNetwork size={14} />}
          card={cardById('network')}
          onOpen={open('network')}
          help={helpFor('network')}
          trend={trendById('network')}
          rows={rows}
          at={at}
          ticks={ticks}
          start={start}
          end={end}
          rangeSeconds={range}
          onHover={setHover}
        >
          <NetworkHardwareFooter r={r} scope={scope} detail={detail} />
        </PerfTile>

        <TopologyTile r={r} scope={scope} onScope={onScope} help={helpFor('distribution')} />
      </main>
      )}
    </div>
  );
}

export function PerfTile({
  icon,
  onOpen,
  help,
  syncByTime = false,
  card,
  trend,
  rows,
  at,
  ticks,
  start,
  end,
  rangeSeconds,
  onHover,
  children,
}: {
  icon: React.ReactNode;
  card?: Card;
  trend?: Trend;
  rows: ChartRow[];
  at: ChartRow | undefined;
  ticks: number[];
  start: number;
  end: number;
  rangeSeconds: number;
  onHover: (t: number | null) => void;
  onOpen?: () => void;
  help?: Help;
  syncByTime?: boolean;
  children?: React.ReactNode;
}) {
  const [chartHeight, setChartHeight] = useState(0);
  const yMax = useMemo(() => {
    if (!trend) return 100;
    if (trend.fixedMax) return trend.fixedMax;
    let peak = 0;
    for (const row of rows) for (const s of trend.series) peak = Math.max(peak, row[s.key] ?? 0);
    const floor = trend.floor ?? (trend.unit === 'cores' ? 0.05 : trend.unit === 'bytes' ? 64 * 1024 * 1024
      : trend.unit === 'percent' || trend.unit === 'ops' || trend.unit === 'ms' || trend.unit === 'packets' || trend.unit === 'perSecond' || trend.unit === 'count' || trend.unit === 'latency' ? 2
      : trend.unit === 'watts' ? 10 : trend.unit === 'celsius' ? 50 : 64 * 1024);
    const max = Math.max(peak * 1.1, floor, ...trend.refs.map(r => r.value * 1.04));
    if (trend.unit === 'percent') return Math.min(100, niceCeil(max));
    return trend.unit === 'bytes' || trend.unit === 'rate' ? niceCeilBytes(max) : niceCeil(max);
  }, [rows, trend]);

  if (!trend) return null;

  return (
    <section
      className={`group flex min-h-0 flex-col justify-between rounded border border-line bg-surface p-3 ${onOpen ? 'cursor-pointer transition-colors hover:border-line-2' : ''}`}
      onClick={onOpen}
    >
      <div className="flex min-h-0 flex-1 flex-col">
        <div className="flex shrink-0 items-start justify-between gap-3">
          <div>
            <div className="flex items-center gap-2 text-xs font-semibold text-text">
              <span className="text-text-2">{icon}</span>
              <span>{card?.label ?? trend.title}</span>
              {onOpen && <IconChevronRight size={12} className="text-muted opacity-0 transition-opacity group-hover:opacity-100" />}
            </div>
            <div className="mt-0.5 text-[11px] text-muted">
              {card?.lines.join(' · ')}
            </div>
          </div>

          <div className="flex items-start gap-2">
            <div className="text-right">
              <div className="font-mono text-lg font-semibold leading-none text-text">
                {card?.bullet ? <LevelText level={card.bullet.level}>{card.value}</LevelText> : card?.value ?? '–'}
              </div>
              {card?.subValue && (
                <div className="mt-0.5 font-mono text-[10px] text-muted">{card.subValue}</div>
              )}
            </div>
            {help && <HelpTip help={help} series={trend.series} refs={trend.refs} />}
          </div>
        </div>

        {card?.bullet && (
          <div className="mt-1.5 shrink-0">
            <BarTip title={card.label} rows={bulletRows(card.bullet)}>
              <BulletBar bullet={card.bullet} height={4} />
            </BarTip>
          </div>
        )}

        {!trend.empty && (
          <div className="mt-1.5 flex shrink-0 flex-wrap items-center justify-between gap-2 text-[11px]">
            <div className="flex items-center gap-3">
              <SeriesValues trend={trend} row={at} />
            </div>
            {trend.refs.length > 0 && (
              <div className="flex items-center gap-2.5 text-muted">
                {trend.refs.map(ref => (
                  <span key={ref.label} className="inline-flex items-center gap-1 font-mono text-[10px]">
                    <span
                      className={`w-2 border-dashed ${ref.kind === 'limit' ? 'border-t-2' : 'border-t'}`}
                      style={{ borderColor: T.limit }}
                    />
                    {ref.label}
                  </span>
                ))}
              </div>
            )}
          </div>
        )}

        <WhileVisible className="mt-1.5 min-h-[72px] w-full flex-1">
          {trend.empty ? (
            <div className="flex h-full items-center justify-center rounded border border-dashed border-line text-xs text-muted">
              {trend.empty}
            </div>
          ) : (
            <ResponsiveContainer width="100%" height="100%" onResize={(_, height) => setChartHeight(height)}>
              <AreaChart
                data={rows}
                syncId={syncByTime ? 'detail' : 'perf'}
                syncMethod={syncByTime ? nearestInTime : 'index'}
                margin={{ top: 4, right: 4, bottom: 0, left: 0 }}
                onMouseMove={state => onHover(state?.activeLabel != null ? Number(state.activeLabel) : null)}
                onMouseLeave={() => onHover(null)}
              >
                <CartesianGrid stroke={T.grid} vertical={false} />
                <XAxis
                  dataKey="t"
                  type="number"
                  scale="time"
                  domain={[start, end]}
                  allowDataOverflow
                  ticks={ticks}
                  tickFormatter={t => fmtClock(t, rangeSeconds <= 60)}
                  tick={{ fill: T.axis, fontSize: 10 }}
                  tickLine={false}
                  axisLine={{ stroke: T.line }}
                />
                <YAxis
                  domain={[0, yMax]}
                  allowDataOverflow
                  width={62}
                  ticks={[0, yMax / 2, yMax]}
                  tickFormatter={v => fmt(trend.unit, v, true)}
                  tick={{ fill: T.axis, fontSize: 10, width: 120 }}
                  tickLine={false}
                  axisLine={false}
                />
                <Tooltip
                  content={({ active, payload }) =>
                    active && payload?.length ? (
                      <div className="pointer-events-none flex items-start gap-3 whitespace-nowrap text-[11px]">
                        <SeriesValues trend={trend} row={payload[0].payload as ChartRow} percentOf={trend.percentOf} />
                      </div>
                    ) : null
                  }
                  cursor={{ stroke: T.text2, strokeOpacity: 0.45 }}
                  position={{ y: Math.round(chartHeight * 0.15) }}
                  wrapperStyle={{ outline: 'none' }}
                  isAnimationActive={false}
                />
                {trend.refs.map(ref => (
                  <ReferenceLine
                    key={ref.label}
                    y={ref.value}
                    stroke={T.limit}
                    strokeDasharray={ref.kind === 'limit' ? '5 3' : '3 3'}
                    strokeWidth={1.25}
                  />
                ))}
                {trend.series.map(s => (
                  <Area
                    key={s.key}
                    dataKey={s.key}
                    type="monotone"
                    isAnimationActive={false}
                    connectNulls={false}
                    dot={false}
                    stroke={s.color}
                    strokeWidth={1.5}
                    fill={s.fill ? T.usageFill : 'transparent'}
                    activeDot={{ r: 2.5, strokeWidth: 0, fill: s.color }}
                  />
                ))}
              </AreaChart>
            </ResponsiveContainer>
          )}
        </WhileVisible>
      </div>

      {children && <div className="mt-2 shrink-0 border-t border-line pt-2">{children}</div>}
    </section>
  );
}

function nearestInTime(ticks: ReadonlyArray<{ value: unknown }>, data: { activeLabel?: unknown }) {
  const t = Number(data.activeLabel);
  let best = 0;
  ticks.forEach((tick, i) => {
    if (Math.abs(Number(tick.value) - t) < Math.abs(Number(ticks[best].value) - t)) best = i;
  });
  return best;
}

function SeriesValues({ trend, row, percentOf }: { trend: Trend; row: ChartRow | undefined; percentOf?: number | null }) {
  return (
    <>
      {trend.series.map(s => {
        const value = row?.[s.key] ?? null;
        return (
          <span key={s.key} className="inline-flex flex-col items-end">
            <span className="inline-flex items-center gap-1.5">
              <span className="h-0.5 w-2.5" style={{ background: s.color }} />
              <span className="text-muted">{s.label}</span>
              <span className="font-mono text-text">{fmt(trend.unit, value)}</span>
            </span>
            {percentOf ? <span className="font-mono text-[10px] text-muted">{fmtPct(pct(value, percentOf))}</span> : null}
          </span>
        );
      })}
    </>
  );
}

function CpuHardwareFooter({ r, scope, detail }: { r: Resolved; scope: Scope; detail?: HostSample }) {
  if (scope.containerId) return null;

  if (!scope.hostId) {
    return (
      <div className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-[11px] sm:grid-cols-4">
        {r.hosts.map(h => (
          <div key={h.hostId} className="min-w-0">
            <div className="flex justify-between gap-1">
              <span className="truncate text-text-2">{h.displayName}</span>
              <span className="font-mono text-text">{h.online ? fmtPct(h.cpuPercent, true) : 'off'}</span>
            </div>
            <BarTip
              title={`${h.displayName} · CPU`}
              rows={h.online
                ? capacityRows('cores', busyCores(h) ?? 0, h.logicalCores ?? 0, levelColor(levelOf(h.cpuPercent, THRESHOLDS.cpu)), 'In use', 'Idle')
                : [{ label: 'Not reporting', value: '–' }]}
            >
              <div className="mt-1 h-1 overflow-hidden rounded-full bg-surface-3">
                <div
                  className="h-full"
                  style={{
                    width: `${h.online ? Math.min(100, h.cpuPercent ?? 0) : 0}%`,
                    background: levelColor(levelOf(h.cpuPercent, THRESHOLDS.cpu)),
                  }}
                />
              </div>
            </BarTip>
          </div>
        ))}
      </div>
    );
  }

  const cores = detail?.snapshot.cpu?.coreUsagePercent ?? [];
  if (!cores.length) {
    return (
      <div className="text-[11px] text-muted">
        {r.host?.logicalCores ?? 0} logical processors
      </div>
    );
  }

  return (
    <div>
      <div className="mb-1 flex flex-wrap items-center justify-between gap-2 text-[10px] text-muted">
        <span>{cores.length} logical cores</span>
        <span className="inline-flex items-center gap-1">
          <span>0%</span>
          <span className="h-1 w-10 rounded-full" style={{ background: `linear-gradient(90deg, ${HEAT_RAMP.join(',')})` }} />
          <span>100%</span>
        </span>
      </div>
      <div
        className="grid gap-0.5"
        style={{ gridTemplateColumns: `repeat(${cores.length >= 32 ? 32 : Math.max(8, cores.length)}, minmax(0, 1fr))` }}
      >
        {cores.map((v, i) => (
          <div
            key={i}
            title={`Core ${i}: ${fmtPct(v)}`}
            className="h-2.5 rounded-[1px]"
            style={{ background: heat(v) }}
          />
        ))}
      </div>
    </div>
  );
}

function MemoryHardwareFooter({ r, scope, detail }: { r: Resolved; scope: Scope; detail?: HostSample }) {
  if (scope.containerId) {
    const h = r.host;
    const u = r.container ? usageOf(r.container) : null;
    if (!h?.memoryTotalBytes || !u) return <div className="text-[11px] text-muted">Container memory not reported</div>;
    return (
      <ContainerShareBar
        label={`Container memory on ${wslVm(h) ? 'the Docker VM' : h.displayName}`}
        summary={`${fmtBytes(u.memoryBytes)} of ${fmtBytes(h.memoryTotalBytes)}`}
        usedBytes={h.memoryUsedBytes ?? 0}
        totalBytes={h.memoryTotalBytes}
        containerBytes={u.memoryBytes}
        containerLabel="This container"
        thresholds={THRESHOLDS.memory}
        containerColor={T.usage}
        otherColor={OTHER_GREY}
        freeLabel="Available"
      />
    );
  }

  if (!scope.hostId) {
    return (
      <div className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-[11px] sm:grid-cols-4">
        {r.hosts.map(h => (
          <div key={h.hostId} className="min-w-0">
            <div className="flex justify-between gap-1">
              <span className="truncate text-text-2">{h.displayName}</span>
              <span className="font-mono text-text">{h.online ? fmtPct(h.memoryPercent, true) : 'off'}</span>
            </div>
            <BarTip
              title={`${h.displayName} · Memory`}
              rows={h.online
                ? capacityRows('bytes', h.memoryUsedBytes ?? 0, h.memoryTotalBytes ?? 0, levelColor(levelOf(h.memoryPercent, THRESHOLDS.memory)), 'In use', 'Available')
                : [{ label: 'Not reporting', value: '–' }]}
            >
              <div className="mt-1 h-1 overflow-hidden rounded-full bg-surface-3">
                <div
                  className="h-full"
                  style={{
                    width: `${h.online ? Math.min(100, h.memoryPercent ?? 0) : 0}%`,
                    background: levelColor(levelOf(h.memoryPercent, THRESHOLDS.memory)),
                  }}
                />
              </div>
            </BarTip>
          </div>
        ))}
      </div>
    );
  }

  const h = r.host;
  if (!h || !h.memoryTotalBytes) return null;
  const total = h.memoryTotalBytes;
  const used = h.memoryUsedBytes ?? 0;
  const containersBytes = sum(h.containers, w => usageOf(w)?.memoryBytes);
  const otherBytes = Math.max(0, used - containersBytes);
  const cachedBytes = detail?.snapshot.memory?.cachedBytes ?? 0;
  const freeBytes = Math.max(0, total - used);
  const widths = shareWidths(containersBytes, used, total);
  const rows: TipRow[] = [
    { label: 'Other', value: fmtBytes(otherBytes), share: shareOf(otherBytes, total), color: T.usage },
    { label: 'Containers', value: fmtBytes(containersBytes), share: shareOf(containersBytes, total), color: T.second },
    ...topRows(
      liveContainers(h),
      w => w.usage?.memoryBytes ?? 0,
      w => ({ label: nameOf(w), value: fmtBytes(w.usage?.memoryBytes), share: shareOf(w.usage?.memoryBytes, total), detail: true }),
      (count, bytes) => ({ label: `+${count} more`, value: fmtBytes(bytes), share: shareOf(bytes, total), detail: true }),
    ),
    { label: 'Available', value: fmtBytes(freeBytes), share: shareOf(freeBytes, total), color: FREE_SWATCH },
    { label: 'Total', value: fmtBytes(total) },
  ];

  return (
    <div className="space-y-1.5 text-[11px]">
      <BarTip title={`${wslVm(h) ? 'Docker VM memory' : 'Memory'} on ${h.displayName}`} rows={rows}>
        <div className="flex h-1.5 w-full overflow-hidden rounded-full bg-surface-3">
          <div style={{ width: `${widths.other}%`, background: T.usage }} />
          <div style={{ width: `${widths.part}%`, background: T.second }} />
        </div>
      </BarTip>
      <div className="flex flex-wrap justify-between gap-2 text-muted">
        <span>Other: <strong className="font-mono font-normal text-text">{fmtBytes(otherBytes)}</strong></span>
        <span>Containers: <strong className="font-mono font-normal text-text">{fmtBytes(containersBytes)}</strong></span>
        {cachedBytes > 0 && <span>Cache: <strong className="font-mono font-normal text-text">{fmtBytes(cachedBytes)}</strong></span>}
        <span>Available: <strong className="font-mono font-normal text-text">{fmtBytes(freeBytes)}</strong></span>
      </div>
    </div>
  );
}

function GpuHardwareFooter({ r, scope, detail }: { r: Resolved; scope: Scope; detail?: HostSample }) {
  if (scope.containerId) return null;

  const gpus = !scope.hostId
    ? r.hosts.flatMap(h => h.gpus.map(g => ({ host: h.displayName, ...g, powerDrawWatts: null as number | null })))
    : (detail?.snapshot.gpu.devices ?? r.host?.gpus ?? []).map(g => ({
        host: r.host?.displayName ?? '',
        index: g.index,
        name: g.name,
        utilizationPercent: g.utilizationPercent,
        memoryUsagePercent: g.memoryUsagePercent,
        memoryUsedBytes: g.memoryUsedBytes,
        memoryTotalBytes: g.memoryTotalBytes,
        temperatureCelsius: g.temperatureCelsius,
        powerDrawWatts: 'powerDrawWatts' in g ? g.powerDrawWatts : null,
      }));

  if (!gpus.length) {
    return <div className="text-[11px] text-muted">No GPUs present</div>;
  }

  return (
    <div className="grid grid-cols-1 gap-1.5 text-[11px] sm:grid-cols-2">
      {gpus.slice(0, 4).map((g, i) => (
        <div key={i} className="flex items-center justify-between gap-2 rounded bg-bg/60 px-2 py-1">
          <span className="truncate text-text-2">
            {!scope.hostId ? `${g.host} #${g.index}` : `${g.name}`}
          </span>
          <span className="shrink-0 font-mono text-text">
            {fmtPct(g.utilizationPercent, true)} · {fmtBytes(g.memoryUsedBytes)} / {fmtBytes(g.memoryTotalBytes)}
            {g.temperatureCelsius != null ? ` · ${g.temperatureCelsius}°C` : ''}
          </span>
        </div>
      ))}
    </div>
  );
}

function DiskHardwareFooter({ r, scope, detail }: { r: Resolved; scope: Scope; detail?: HostSample }) {
  const volume = scope.hostId && r.host ? containerVolume(r.host) : null;

  if (scope.containerId) {
    const u = r.container ? usageOf(r.container) : null;
    if (!volume || u?.diskUsedBytes == null) return <div className="text-[11px] text-muted">Container storage not reported</div>;
    return (
      <ContainerShareBar
        label={`Container storage on ${volume.name}`}
        summary={`${fmtBytes(u.diskUsedBytes)} of ${fmtBytes(volume.totalBytes)}`}
        usedBytes={volume.usedBytes}
        totalBytes={volume.totalBytes}
        containerBytes={u.diskUsedBytes}
        containerLabel="This container"
        thresholds={THRESHOLDS.disk}
        containerColor={T.usage}
        otherColor={OTHER_GREY}
      />
    );
  }

  const containers = r.host?.containers.filter(w => usageOf(w)?.diskUsedBytes != null) ?? [];
  const containersBytes = sum(containers, w => w.usage?.diskUsedBytes);

  const rawVolumes = !scope.hostId
    ? r.hosts.flatMap(h => hostVolumes(h).map(v => ({ label: `${h.displayName}: ${v.name}`, ...v })))
    : (() => {
        const list = (detail?.snapshot.disk.volumes ?? r.host?.volumes ?? []).filter(v => !('error' in v && v.error));
        const filtered = list.length > 1 && list.some(v => v.path !== '/') ? list.filter(v => v.path !== '/') : list;
        return filtered.map(v => ({ label: `${v.name} (${v.path})`, ...v }));
      })();

  if (!rawVolumes.length) return <div className="text-[11px] text-muted">No volumes reported</div>;

  return (
    <div className="grid grid-cols-1 gap-2 text-[11px]">
      {rawVolumes.slice(0, 3).map(v => volume && containers.length && v.path === volume.path ? (
        <ContainerShareBar
          key={v.label}
          label={v.label}
          summary={`${fmtBytes(v.usedBytes)} / ${fmtBytes(v.totalBytes)} (${fmtPct(v.usagePercent, false)})`}
          usedBytes={v.usedBytes}
          totalBytes={v.totalBytes}
          containerBytes={containersBytes}
          containerLabel="Containers"
          containerDetail={topRows(
            containers,
            w => w.usage?.diskUsedBytes ?? 0,
            w => ({ label: nameOf(w), value: fmtBytes(w.usage?.diskUsedBytes), share: shareOf(w.usage?.diskUsedBytes, v.totalBytes), detail: true }),
            (count, bytes) => ({ label: `+${count} more`, value: fmtBytes(bytes), share: shareOf(bytes, v.totalBytes), detail: true }),
          )}
          thresholds={THRESHOLDS.disk}
        />
      ) : (
        <div key={v.label} className="min-w-0">
          <div className="flex justify-between gap-2">
            <span className="truncate text-text-2">{v.label}</span>
            <span className="shrink-0 font-mono text-text">
              {fmtBytes(v.usedBytes)} / {fmtBytes(v.totalBytes)} ({fmtPct(v.usagePercent, false)})
            </span>
          </div>
          <BarTip title={v.label} rows={capacityRows('bytes', v.usedBytes, v.totalBytes, levelColor(levelOf(v.usagePercent, THRESHOLDS.disk)), 'Used')}>
            <div className="mt-1 h-1 overflow-hidden rounded-full bg-surface-3">
              <div
                className="h-full"
                style={{
                  width: `${Math.min(100, v.usagePercent)}%`,
                  background: levelColor(levelOf(v.usagePercent, THRESHOLDS.disk)),
                }}
              />
            </div>
          </BarTip>
        </div>
      ))}
    </div>
  );
}

export const liveContainers = (h: HostSummary) =>
  h.containers.filter(w => w.status === 'online' && w.usage).sort((a, b) => a.containerId.localeCompare(b.containerId));

const shareWidths = (part: number, used: number, total: number) => {
  const usedPct = total > 0 ? Math.min(100, (100 * Math.max(0, used)) / total) : 0;
  const partPct = used > 0 ? usedPct * Math.sqrt(Math.min(1, Math.max(0, part) / used)) : 0;
  return { part: partPct, other: usedPct - partPct };
};

const shareOf = (part: number | null | undefined, whole: number | null | undefined) => {
  const p = pct(part, whole);
  return p == null ? null : p > 0 && p < 0.1 ? '<0.1%' : fmtPct(p);
};

const TIP_TOP = 5;

function topRows<T>(
  items: T[],
  valueOf: (item: T) => number,
  toRow: (item: T) => TipRow,
  more: (count: number, total: number) => TipRow,
  pinned?: (item: T) => boolean,
): TipRow[] {
  const sorted = [...items].sort((a, b) => valueOf(b) - valueOf(a));
  const shown = sorted.filter((item, i) => i < TIP_TOP || pinned?.(item));
  const rest = sorted.filter(item => !shown.includes(item));
  return [...shown.map(toRow), ...(rest.length ? [more(rest.length, sum(rest, valueOf))] : [])];
}

const SIGNALS: Record<number, string> = { 1: 'SIGHUP', 2: 'SIGINT', 3: 'SIGQUIT', 6: 'SIGABRT', 9: 'SIGKILL', 11: 'SIGSEGV', 13: 'SIGPIPE', 15: 'SIGTERM' };

function exitText(w: ContainerState): string | null {
  if (w.exitCode == null && w.exitSignal == null) return null;
  return w.exitCode != null ? `exited ${w.exitCode}` : `killed by ${SIGNALS[w.exitSignal!] ?? `signal ${w.exitSignal}`}`;
}

function exitDetail(w: ContainerState): string | null {
  const how = exitText(w);
  if (!how) return null;
  const when = w.stopSeconds == null ? ', on its own' : w.stopSeconds < 0.1 ? ', under 0.1 s after the stop signal' : `, ${w.stopSeconds.toFixed(1)} s after the stop signal`;
  return `last ${how}${when}`;
}

const capacityRows = (unit: Unit, used: number, cap: number, color: string, usedLabel = 'In use', freeLabel = 'Free'): TipRow[] => [
  { label: usedLabel, value: fmt(unit, used), share: unit === 'percent' ? null : shareOf(used, cap), color },
  { label: freeLabel, value: fmt(unit, Math.max(0, cap - used)), share: unit === 'percent' ? null : shareOf(Math.max(0, cap - used), cap), color: FREE_SWATCH },
  { label: 'Total', value: fmt(unit, cap) },
];

const bulletRows = (bullet: Bullet): TipRow[] => [
  ...capacityRows(bullet.unit, bullet.used, bullet.cap, levelColor(bullet.level)),
  ...(bullet.limit != null
    ? [{ label: 'Limits set', value: fmt(bullet.unit, bullet.limit), share: shareOf(bullet.limit, bullet.cap), color: T.limit }]
    : []),
];

function ContainerShareBar({ label, summary, usedBytes, totalBytes, containerBytes, containerLabel, containerDetail = [], thresholds, containerColor = T.second, otherColor = T.usage, freeLabel = 'Free' }: {
  label: string;
  summary: string;
  usedBytes: number;
  totalBytes: number;
  containerBytes: number;
  containerLabel: string;
  containerDetail?: TipRow[];
  thresholds: readonly [number, number];
  containerColor?: string;
  otherColor?: string;
  freeLabel?: string;
}) {
  const otherBytes = Math.max(0, usedBytes - containerBytes);
  const freeBytes = Math.max(0, totalBytes - usedBytes);
  const level = levelOf(pct(usedBytes, totalBytes), thresholds);
  const widths = shareWidths(containerBytes, usedBytes, totalBytes);
  const otherFill = level === 'ok' ? otherColor : levelColor(level);
  const rows: TipRow[] = [
    { label: 'Other', value: fmtBytes(otherBytes), share: shareOf(otherBytes, totalBytes), color: otherFill },
    { label: containerLabel, value: fmtBytes(containerBytes), share: shareOf(containerBytes, totalBytes), color: containerColor },
    ...containerDetail,
    { label: freeLabel, value: fmtBytes(freeBytes), share: shareOf(freeBytes, totalBytes), color: FREE_SWATCH },
    { label: 'Total', value: fmtBytes(totalBytes) },
  ];
  return (
    <div className="min-w-0 space-y-1.5 text-[11px]">
      <div className="flex justify-between gap-2">
        <span className="truncate text-text-2">{label}</span>
        <span className="shrink-0 font-mono text-text">{summary}</span>
      </div>
      <BarTip title={label} rows={rows}>
        <div className="flex h-1.5 w-full overflow-hidden rounded-full bg-surface-3">
          <div style={{ width: `${widths.other}%`, background: otherFill }} />
          <div style={{ width: `${widths.part}%`, minWidth: containerBytes > 0 ? 3 : 0, background: containerColor }} />
        </div>
      </BarTip>
      <div className="flex flex-wrap justify-between gap-2 text-muted">
        <span>Other: <strong className="font-mono font-normal text-text">{fmtBytes(otherBytes)}</strong></span>
        <span>{containerLabel}: <strong className="font-mono font-normal text-text">{fmtBytes(containerBytes)}</strong></span>
        <span>{freeLabel}: <strong className="font-mono font-normal text-text">{fmtBytes(Math.max(0, totalBytes - usedBytes))}</strong></span>
      </div>
    </div>
  );
}

function NetworkHardwareFooter({ r, scope, detail }: { r: Resolved; scope: Scope; detail?: HostSample }) {
  if (scope.containerId) return null;

  if (!scope.hostId) {
    return (
      <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-[11px] sm:grid-cols-4">
        {r.hosts.map(h => (
          <div key={h.hostId} className="flex items-center justify-between gap-1">
            <span className="truncate text-text-2">{h.displayName}</span>
            <span className="font-mono text-text">
              {h.online ? fmtRate((h.netReceiveBytesPerSecond ?? 0) + (h.netTransmitBytesPerSecond ?? 0), true) : '–'}
            </span>
          </div>
        ))}
      </div>
    );
  }

  const ifaces = detail?.snapshot.network.interfaces ?? [];
  if (!ifaces.length) {
    return <div className="text-[11px] text-muted">Host network interfaces</div>;
  }

  return (
    <div className="grid grid-cols-1 gap-1.5 text-[11px] sm:grid-cols-2">
      {ifaces.slice(0, 4).map(iface => (
        <div key={iface.name} className="flex items-center justify-between gap-2 rounded bg-bg/60 px-2 py-1">
          <span className="font-mono text-text">{iface.name}</span>
          <span className="font-mono text-text-2">
            ↓ {fmtRate(iface.receiveBytesPerSecond, true)} · ↑ {fmtRate(iface.transmitBytesPerSecond, true)}
          </span>
        </div>
      ))}
    </div>
  );
}

interface Segment {
  id: string;
  label: string;
  value: number;
  formatted: string;
  color: string;
  scope: Scope | null;
  active: boolean;
}

function TopologyTile({ r, scope, onScope, help }: { r: Resolved; scope: Scope; onScope: (s: Scope) => void; help?: Help }) {
  const hosts = useMemo(() => [...r.hosts].sort((a, b) => a.displayName.localeCompare(b.displayName)), [r.hosts]);

  if (scope.hostId && r.host) {
    const h = r.host;
    const onlineContainers = liveContainers(h);

    const totalCores = Math.max(1, h.logicalCores ?? 1);
    const hostBusyCores = busyCores(h) ?? 0;
    const containersCores = sum(onlineContainers, w => w.usage?.cpuCores);
    const otherCores = Math.max(0, hostBusyCores - containersCores);

    const totalMem = Math.max(1, h.memoryTotalBytes ?? 1);
    const hostUsedMem = h.memoryUsedBytes ?? 0;
    const containersMem = sum(onlineContainers, w => w.usage?.memoryBytes);
    const otherMem = Math.max(0, hostUsedMem - containersMem);

    const volume = containerVolume(h);
    const containersDisk = sum(onlineContainers, w => w.usage?.diskUsedBytes);
    const otherDisk = volume ? Math.max(0, volume.usedBytes - containersDisk) : 0;

    const hasGpu = h.gpus.length > 0;
    const hostGpuPct = gpuAverage(h) ?? 0;
    const containersGpuPct = sum(onlineContainers, w => w.usage?.gpuPercent);
    const otherGpuPct = Math.max(0, hostGpuPct - containersGpuPct);

    const v = vram(h);
    const totalVram = Math.max(1, v.total);
    const hostUsedVram = v.used;
    const containersVram = sum(onlineContainers, w => w.usage?.gpuMemoryBytes);
    const otherVram = Math.max(0, hostUsedVram - containersVram);

    const alloc = allocated(h);

    const cpuSegments: Segment[] = [
      ...onlineContainers.map((w, i) => ({
        id: w.containerId,
        label: nameOf(w),
        value: w.usage?.cpuCores ?? 0,
        formatted: `${fmtCores(w.usage?.cpuCores, true)}c (${fmtPct(pct(w.usage?.cpuCores, totalCores), true)})`,
        color: PALETTE[i % PALETTE.length],
        scope: scope.containerId === w.containerId ? { hostId: h.hostId, containerId: null } : { hostId: h.hostId, containerId: w.containerId },
        active: !scope.containerId || scope.containerId === w.containerId,
      })),
      ...(otherCores > 0.05
        ? [{
            id: '__other',
            label: 'Other',
            value: otherCores,
            formatted: `${fmtCores(otherCores, true)}c (${fmtPct(pct(otherCores, totalCores), true)})`,
            color: OTHER_GREY,
            scope: null,
            active: !scope.containerId,
          }]
        : []),
    ];

    const memSegments: Segment[] = [
      ...onlineContainers.map((w, i) => ({
        id: w.containerId,
        label: nameOf(w),
        value: w.usage?.memoryBytes ?? 0,
        formatted: `${fmtBytes(w.usage?.memoryBytes)} (${fmtPct(pct(w.usage?.memoryBytes, totalMem), true)})`,
        color: PALETTE[i % PALETTE.length],
        scope: scope.containerId === w.containerId ? { hostId: h.hostId, containerId: null } : { hostId: h.hostId, containerId: w.containerId },
        active: !scope.containerId || scope.containerId === w.containerId,
      })),
      ...(otherMem > 0
        ? [{
            id: '__other',
            label: 'Other',
            value: otherMem,
            formatted: `${fmtBytes(otherMem)} (${fmtPct(pct(otherMem, totalMem), true)})`,
            color: OTHER_GREY,
            scope: null,
            active: !scope.containerId,
          }]
        : []),
    ];

    const diskSegments: Segment[] = volume
      ? [
          ...onlineContainers.map((w, i) => ({
            id: w.containerId,
            label: nameOf(w),
            value: w.usage?.diskUsedBytes ?? 0,
            formatted: fmtBytes(w.usage?.diskUsedBytes),
            color: PALETTE[i % PALETTE.length],
            scope: scope.containerId === w.containerId ? { hostId: h.hostId, containerId: null } : { hostId: h.hostId, containerId: w.containerId },
            active: !scope.containerId || scope.containerId === w.containerId,
          })),
          ...(otherDisk > 0
            ? [{
                id: '__other',
                label: 'Other',
                value: otherDisk,
                formatted: `${fmtBytes(otherDisk)} (${fmtPct(pct(otherDisk, volume.totalBytes), true)})`,
                color: OTHER_GREY,
                scope: null,
                active: !scope.containerId,
              }]
            : []),
        ]
      : [];

    const gpuSegments: Segment[] = hasGpu
      ? [
          ...onlineContainers.map((w, i) => ({
            id: w.containerId,
            label: nameOf(w),
            value: w.usage?.gpuPercent ?? 0,
            formatted: fmtPct(w.usage?.gpuPercent, true),
            color: PALETTE[i % PALETTE.length],
            scope: scope.containerId === w.containerId ? { hostId: h.hostId, containerId: null } : { hostId: h.hostId, containerId: w.containerId },
            active: !scope.containerId || scope.containerId === w.containerId,
          })),
          ...(otherGpuPct > 0.5
            ? [{
                id: '__other',
                label: 'Other',
                value: otherGpuPct,
                formatted: fmtPct(otherGpuPct, true),
                color: OTHER_GREY,
                scope: null,
                active: !scope.containerId,
              }]
            : []),
        ]
      : [];

    const vramSegments: Segment[] = hasGpu
      ? [
          ...onlineContainers.map((w, i) => ({
            id: w.containerId,
            label: nameOf(w),
            value: w.usage?.gpuMemoryBytes ?? 0,
            formatted: `${fmtBytes(w.usage?.gpuMemoryBytes)} (${fmtPct(pct(w.usage?.gpuMemoryBytes, totalVram), true)})`,
            color: PALETTE[i % PALETTE.length],
            scope: scope.containerId === w.containerId ? { hostId: h.hostId, containerId: null } : { hostId: h.hostId, containerId: w.containerId },
            active: !scope.containerId || scope.containerId === w.containerId,
          })),
          ...(otherVram > 0
            ? [{
                id: '__other',
                label: 'Other',
                value: otherVram,
                formatted: `${fmtBytes(otherVram)} (${fmtPct(pct(otherVram, totalVram), true)})`,
                color: OTHER_GREY,
                scope: null,
                active: !scope.containerId,
              }]
            : []),
        ]
      : [];

    return (
      <section className="flex min-h-0 flex-col justify-between rounded border border-line bg-surface p-3">
        <div className="flex shrink-0 items-center justify-between gap-2">
          <div>
            <div className="flex items-center gap-2 text-xs font-semibold text-text">
              <span className="text-text-2"><IconTopology size={14} /></span>
              <span>Resource Distribution</span>
            </div>
            <div className="mt-0.5 text-[11px] text-muted">
              Containers, other, and free capacity on {h.displayName}
            </div>
          </div>
          <div className="flex items-start gap-2">
            {(alloc.cpu > 0 || alloc.memory > 0) && (
              <div className="text-right font-mono text-[11px] text-muted">
                {alloc.cpu > 0 && <div>Limits: {alloc.cpu.toFixed(0)}/{totalCores} cores</div>}
                {alloc.memory > 0 && <div>{fmtBytes(alloc.memory)}/{fmtBytes(totalMem)}</div>}
              </div>
            )}
            {help && <HelpTip help={help} />}
          </div>
        </div>

        <div className="my-auto space-y-2.5 py-2">
          <StackedBarRow
            label="CPU"
            summary={`${hostBusyCores.toFixed(1)} / ${totalCores} cores (${fmtPct(h.cpuPercent, true)})`}
            capacity={totalCores}
            format={v => `${fmtCores(v, true)}c`}
            segments={cpuSegments}
            onScope={onScope}
          />
          <StackedBarRow
            label="Memory"
            summary={`${fmtBytes(hostUsedMem)} / ${fmtBytes(totalMem)} (${fmtPct(h.memoryPercent, true)})`}
            capacity={totalMem}
            format={v => fmtBytes(v)}
            segments={memSegments}
            onScope={onScope}
          />
          {volume && (
            <StackedBarRow
              label="Disk"
              summary={`containers ${fmtBytes(containersDisk)} · ${fmtBytes(volume.usedBytes)} / ${fmtBytes(volume.totalBytes)} (${fmtPct(volume.usagePercent, true)})`}
              capacity={volume.totalBytes}
              format={v => fmtBytes(v)}
              segments={diskSegments}
              onScope={onScope}
              minSegmentPx={4}
            />
          )}
          {hasGpu && (
            <>
              <StackedBarRow
                label="GPU compute"
                summary={`${fmtPct(hostGpuPct)} across ${h.gpus.length} GPU${h.gpus.length > 1 ? 's' : ''}`}
                capacity={100}
                format={v => fmtPct(v, true)}
                segments={gpuSegments}
                onScope={onScope}
              />
              <StackedBarRow
                label="VRAM"
                summary={`${fmtBytes(hostUsedVram)} / ${fmtBytes(totalVram)} (${fmtPct(pct(hostUsedVram, totalVram), true)})`}
                capacity={totalVram}
                format={v => fmtBytes(v)}
                segments={vramSegments}
                onScope={onScope}
              />
            </>
          )}
        </div>

        <div className="flex shrink-0 flex-wrap items-center justify-between gap-x-3 gap-y-1 border-t border-line pt-2 text-[11px]">
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
            {onlineContainers.slice(0, 5).map((w, i) => {
              const selected = scope.containerId === w.containerId;
              return (
                <button
                  key={w.containerId}
                  type="button"
                  onClick={() =>
                    onScope(selected ? { hostId: h.hostId, containerId: null } : { hostId: h.hostId, containerId: w.containerId })
                  }
                  className={`inline-flex items-center gap-1.5 font-mono transition ${
                    selected ? 'font-semibold text-text underline' : 'text-text-2 hover:text-text'
                  }`}
                >
                  <span className="size-2 rounded-xs" style={{ background: PALETTE[i % PALETTE.length] }} />
                  <span>{nameOf(w)}</span>
                </button>
              );
            })}
            {onlineContainers.length > 5 && (
              <span className="text-muted" title={onlineContainers.slice(5).map(nameOf).join(', ')}>+{onlineContainers.length - 5} more</span>
            )}
            <span className="inline-flex items-center gap-1.5 text-muted">
              <span className="size-2 rounded-xs bg-other" />
              <span>Other</span>
            </span>
            <span className="inline-flex items-center gap-1.5 text-muted">
              <span className="size-2 rounded-xs bg-surface-3" />
              <span>Free</span>
            </span>
          </div>
        </div>
      </section>
    );
  }

  const onlineHosts = hosts.filter(h => h.online);
  const totalCores = Math.max(1, sum(onlineHosts, h => h.logicalCores));
  const usedCores = sum(onlineHosts, busyCores);
  const totalMem = Math.max(1, sum(onlineHosts, h => h.memoryTotalBytes));
  const usedMem = sum(onlineHosts, h => h.memoryUsedBytes);

  const allGpus = onlineHosts.flatMap(h => h.gpus);
  const hasFleetGpu = allGpus.length > 0;
  const totalGpuCap = Math.max(1, allGpus.length * 100);
  const usedGpuSum = sum(allGpus, g => g.utilizationPercent);
  const totalVram = Math.max(1, sum(allGpus, g => g.memoryTotalBytes));
  const usedVram = sum(allGpus, g => g.memoryUsedBytes);

  const hostCpuSegments: Segment[] = onlineHosts.map((h, i) => {
    const c = busyCores(h) ?? 0;
    return {
      id: h.hostId,
      label: h.displayName,
      value: c,
      formatted: `${c.toFixed(1)} / ${h.logicalCores}c (${fmtPct(h.cpuPercent, true)})`,
      color: PALETTE[i % PALETTE.length],
      scope: { hostId: h.hostId, containerId: null },
      active: true,
    };
  });

  const hostMemSegments: Segment[] = onlineHosts.map((h, i) => {
    const m = h.memoryUsedBytes ?? 0;
    return {
      id: h.hostId,
      label: h.displayName,
      value: m,
      formatted: `${fmtBytes(m)} / ${fmtBytes(h.memoryTotalBytes)} (${fmtPct(h.memoryPercent, true)})`,
      color: PALETTE[i % PALETTE.length],
      scope: { hostId: h.hostId, containerId: null },
      active: true,
    };
  });

  const hostGpuSegments: Segment[] = onlineHosts
    .filter(h => h.gpus.length > 0)
    .map((h, i) => {
      const utilSum = sum(h.gpus, g => g.utilizationPercent);
      return {
        id: h.hostId,
        label: h.displayName,
        value: utilSum,
        formatted: `${fmtPct(gpuAverage(h), true)} (${h.gpus.length} GPU)`,
        color: PALETTE[i % PALETTE.length],
        scope: { hostId: h.hostId, containerId: null },
        active: true,
      };
    });

  const hostVramSegments: Segment[] = onlineHosts
    .filter(h => h.gpus.length > 0)
    .map((h, i) => {
      const hv = vram(h);
      return {
        id: h.hostId,
        label: h.displayName,
        value: hv.used,
        formatted: `${fmtBytes(hv.used)} / ${fmtBytes(hv.total)}`,
        color: PALETTE[i % PALETTE.length],
        scope: { hostId: h.hostId, containerId: null },
        active: true,
      };
    });

  return (
    <section className="flex min-h-0 flex-col justify-between rounded border border-line bg-surface p-3">
      <div className="flex shrink-0 items-center justify-between gap-2">
        <div>
          <div className="flex items-center gap-2 text-xs font-semibold text-text">
            <span className="text-text-2"><IconTopology size={14} /></span>
            <span>Fleet Resource Distribution</span>
          </div>
          <div className="mt-0.5 text-[11px] text-muted">
            Share of cluster CPU, Memory, and GPU consumed by each host
          </div>
        </div>
        <div className="flex items-start gap-2">
          <span className="font-mono text-[11px] text-muted">
            {onlineHosts.length}/{hosts.length} online
          </span>
          {help && <HelpTip help={help} />}
        </div>
      </div>

      <div className="my-auto space-y-2.5 py-2">
        <StackedBarRow
          label="CPU"
          summary={`${usedCores.toFixed(1)} / ${totalCores} cores (${fmtPct(pct(usedCores, totalCores), true)})`}
          capacity={totalCores}
          format={v => `${v.toFixed(1)}c`}
          segments={hostCpuSegments}
          onScope={onScope}
        />
        <StackedBarRow
          label="Memory"
          summary={`${fmtBytes(usedMem)} / ${fmtBytes(totalMem)} (${fmtPct(pct(usedMem, totalMem), true)})`}
          capacity={totalMem}
          format={v => fmtBytes(v)}
          segments={hostMemSegments}
          onScope={onScope}
        />
        {hasFleetGpu && (
          <>
            <StackedBarRow
              label="GPU compute"
              summary={`${fmtPct(usedGpuSum / allGpus.length, true)} avg across ${allGpus.length} GPUs`}
              capacity={totalGpuCap}
              format={v => fmtPct(v, true)}
              segments={hostGpuSegments}
              onScope={onScope}
            />
            <StackedBarRow
              label="VRAM"
              summary={`${fmtBytes(usedVram)} / ${fmtBytes(totalVram)} (${fmtPct(pct(usedVram, totalVram), true)})`}
              capacity={totalVram}
              format={v => fmtBytes(v)}
              segments={hostVramSegments}
              onScope={onScope}
            />
          </>
        )}
      </div>

      <div className="flex shrink-0 flex-wrap items-center justify-between gap-2 border-t border-line pt-2 text-[11px]">
        <div className="flex flex-wrap items-center gap-3">
          {onlineHosts.map((h, i) => (
            <button
              key={h.hostId}
              type="button"
              onClick={() => onScope({ hostId: h.hostId, containerId: null })}
              className="inline-flex items-center gap-1.5 text-text-2 hover:text-text"
            >
              <span className="size-2 rounded-xs" style={{ background: PALETTE[i % PALETTE.length] }} />
              <span>{h.displayName}</span>
            </button>
          ))}
          <span className="inline-flex items-center gap-1.5 text-muted">
            <span className="size-2 rounded-xs bg-surface-3" />
            <span>Free capacity</span>
          </span>
        </div>
      </div>
    </section>
  );
}

function StackedBarRow({ label, summary, capacity, segments, onScope, format, minSegmentPx = 0 }: {
  label: string;
  summary: string;
  capacity: number;
  segments: Segment[];
  onScope: (s: Scope) => void;
  format: (value: number) => string;
  minSegmentPx?: number;
}) {
  const members = segments.filter(s => s.id !== '__other');
  const selecting = segments.some(s => !s.active);
  const rows: TipRow[] = [
    ...topRows(
      members,
      s => s.value,
      s => ({ label: s.label, value: s.formatted, color: s.color }),
      (count, total) => ({ label: `+${count} more`, value: format(total) }),
      s => selecting && s.active,
    ),
    ...segments.filter(s => s.id === '__other').map(s => ({ label: s.label, value: s.formatted, color: s.color })),
  ];

  return (
    <div>
      <div className="mb-1 flex items-baseline justify-between gap-2 text-[11px]">
        <span className="font-medium text-text-2">{label}</span>
        <span className="font-mono text-muted">{summary}</span>
      </div>
      <BarTip title={`${label} · ${summary}`} rows={rows}>
        <div className="flex h-3 w-full overflow-hidden rounded-xs bg-surface-3">
          {segments.map(seg => {
            const widthPct = Math.min(100, (100 * seg.value) / Math.max(1e-9, capacity));
            if (widthPct < 0.25 && !(minSegmentPx && seg.value > 0)) return null;
            return (
              <div
                key={seg.id}
                onClick={() => seg.scope && onScope(seg.scope)}
                className={`h-full border-r border-bg/50 transition-opacity ${
                  seg.scope ? 'cursor-pointer hover:brightness-110' : ''
                } ${seg.active ? 'opacity-100' : 'opacity-30'}`}
                style={{ width: `${widthPct}%`, minWidth: seg.value > 0 ? minSegmentPx : 0, background: seg.color }}
              />
            );
          })}
        </div>
      </BarTip>
    </div>
  );
}
