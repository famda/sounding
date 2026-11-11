import { Fragment, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { type Help, HELP_PALETTE, HELP_RAMP, type HelpItem } from '../help';
import type { Bullet, Ref, TrendSeries } from '../models';
import { levelColor, T, type Level } from '../theme';
import type { ContainerStatus } from '../types';
import { IconHelp } from './Icons';

export const LevelText = ({ level, children }: { level: Level; children: ReactNode }) => (
  <span style={level === 'ok' ? undefined : { color: levelColor(level) }}>{children}</span>
);

export function BulletBar({ bullet, height = 6 }: { bullet: Bullet; height?: number }) {
  const scale = Math.max(bullet.cap, bullet.limit ?? 0);
  const at = (v: number) => `${Math.min(100, (100 * v) / scale)}%`;
  return (
    <div className="relative" style={{ height }}>
      <div className="absolute inset-y-0 left-0 rounded-full bg-surface-3" style={{ width: at(bullet.cap) }} />
      {bullet.limit != null && bullet.limit > bullet.cap && (
        <div
          className="absolute inset-y-0 rounded-r-full"
          style={{
            left: at(bullet.cap),
            width: `calc(${at(bullet.limit)} - ${at(bullet.cap)})`,
            background: `repeating-linear-gradient(135deg, rgba(161,161,170,0.22) 0 3px, transparent 3px 6px)`,
          }}
        />
      )}
      <div
        className="absolute inset-y-0 left-0 rounded-full"
        style={{ width: at(bullet.used), background: levelColor(bullet.level) }}
      />
      {bullet.limit != null && (
        <div
          className="absolute -inset-y-1 w-[1.5px] rounded-full"
          style={{ left: `calc(${at(bullet.limit)} - 1px)`, background: T.limit }}
        />
      )}
    </div>
  );
}

const TIP_CARD = 'rounded-lg bg-surface-2 shadow-xl shadow-black/25 ring-1 ring-line-2';

export interface TipRow { label: string; value: string; share?: string | null; color?: string; detail?: boolean }

interface BarTipAnchor { x: number; fraction: number; top: number; bottom: number; minTop: number }

export function BarTip({ title, rows, children }: { title?: string; rows: TipRow[]; children: ReactNode }) {
  const [at, setAt] = useState<BarTipAnchor | null>(null);
  const visible = at !== null;
  useEffect(() => {
    if (!visible) return;
    const close = () => setAt(null);
    window.addEventListener('scroll', close, true);
    window.addEventListener('resize', close);
    return () => {
      window.removeEventListener('scroll', close, true);
      window.removeEventListener('resize', close);
    };
  }, [visible]);

  return (
    <div
      className="relative -my-1 py-1"
      onMouseMove={e => {
        const box = e.currentTarget.getBoundingClientRect();
        const main = e.currentTarget.closest('main');
        setAt({
          x: e.clientX,
          fraction: (e.clientX - box.left) / Math.max(1, box.width),
          top: box.top,
          bottom: box.bottom,
          minTop: Math.max(4, main?.getBoundingClientRect().top ?? 4),
        });
      }}
      onMouseLeave={() => setAt(null)}
    >
      {children}
      {at && createPortal(<BarTooltip at={at} title={title} rows={rows} />, document.body)}
    </div>
  );
}

function BarTooltip({ at, title, rows }: { at: BarTipAnchor; title?: string; rows: TipRow[] }) {
  const ref = useRef<HTMLDivElement>(null);
  const [position, setPosition] = useState<{ left: number; top: number } | null>(null);
  useLayoutEffect(() => {
    const box = ref.current?.getBoundingClientRect();
    if (!box) return;
    const above = at.top - box.height - 4;
    const top = above >= at.minTop ? above : at.bottom + 4;
    setPosition({
      left: Math.max(4, Math.min(at.x - box.width * at.fraction, window.innerWidth - box.width - 4)),
      top: Math.max(4, Math.min(top, window.innerHeight - box.height - 4)),
    });
  }, [at]);

  return (
    <div
      ref={ref}
      role="tooltip"
      className={`${TIP_CARD} pointer-events-none fixed z-50 max-w-[calc(100vw-8px)] px-2.5 py-2 text-[11px]`}
      style={{ left: position?.left ?? 0, top: position?.top ?? 0, visibility: position ? 'visible' : 'hidden' }}
    >
      {title && <div className="mb-1 break-words font-medium text-text">{title}</div>}
      <div className="grid grid-cols-[minmax(0,auto)_auto_auto] items-center gap-x-3 gap-y-0.5">
        {rows.map((row, i) => (
          <Fragment key={i}>
            <span className={`inline-flex min-w-0 items-center gap-1.5 break-words ${row.detail ? 'pl-3.5 text-muted' : 'text-text-2'}`}>
              <span className="size-2 shrink-0 rounded-xs" style={{ background: row.color ?? 'transparent' }} />
              {row.label}
            </span>
            <span className="whitespace-nowrap text-right font-mono text-text">{row.value}</span>
            <span className="whitespace-nowrap text-right font-mono text-muted">{row.share ?? ''}</span>
          </Fragment>
        ))}
      </div>
    </div>
  );
}

export function StatusDot({ status }: { status: ContainerStatus | boolean }) {
  const s: ContainerStatus = typeof status === 'boolean' ? (status ? 'online' : 'offline') : status;
  const color = s === 'online' ? 'bg-ok' : s === 'stopped' ? 'bg-muted' : 'bg-crit';
  return <span className={`inline-block size-2 shrink-0 rounded-full ${color}`} />;
}

export const statusText = { online: 'Running', offline: 'Not reporting', stopped: 'Stopped' } as const;

export function HelpTip({ help, series = [], refs = [] }: { help: Help; series?: TrendSeries[]; refs?: Ref[] }) {
  const [open, setOpen] = useState(false);
  const items: HelpItem[] = [
    ...series.flatMap(s => {
      const text = help.series?.[s.label] ?? help.anySeries;
      return text ? [{ label: s.label, text, mark: { kind: 'line' as const, color: s.color } }] : [];
    }),
    ...(help.reference && refs.length ? [{ label: 'Dashed line', text: help.reference, mark: { kind: 'dashed' as const } }] : []),
    ...(help.items ?? []),
  ].filter((item, i, all) => all.findIndex(x => x.label === item.label) === i); // a chart line and its bar segment once
  return (
    <span
      className="relative inline-flex shrink-0"
      onMouseEnter={() => setOpen(true)}
      onMouseLeave={() => setOpen(false)}
      onClick={e => e.stopPropagation()}
    >
      <button
        type="button"
        aria-label="What this shows"
        onFocus={() => setOpen(true)}
        onBlur={() => setOpen(false)}
        className="rounded-full text-muted transition-colors hover:text-text-2 focus:text-text-2 focus:outline-none"
      >
        <IconHelp size={14} />
      </button>
      {open && (
        <span
          role="tooltip"
          className={`${TIP_CARD} absolute -right-1.5 top-full z-50 mt-2 block w-[28rem] max-w-[calc(100vw-2rem)] animate-tip-in px-3.5 py-3 text-left font-sans font-normal`}
        >
          <span className="absolute -top-1 right-2.5 size-2 rotate-45 border-l border-t border-line-2 bg-surface-2" />
          <span className="block text-xs/5 text-text">{help.meaning}</span>
          {items.length > 0 && (
            <span className="mt-2.5 flex flex-wrap gap-x-4 gap-y-1 border-t border-line-2 pt-2 text-[11px] text-text-2">
              {items.map(item => (
                <span key={item.label} className="inline-flex items-center gap-1.5">
                  <HelpLabel item={item} />
                  {item.text && <span className="text-muted">{item.text}</span>}
                </span>
              ))}
            </span>
          )}
        </span>
      )}
    </span>
  );
}

function HelpLabel({ item: { label, mark } }: { item: HelpItem }) {
  if (mark?.kind === 'badge') {
    return (
      <span
        className="justify-self-start whitespace-nowrap rounded-xs border px-1 text-[10px] leading-4"
        style={mark.warn ? { color: T.warn, borderColor: 'rgba(245, 158, 11, 0.4)' } : { color: T.text2, borderColor: 'var(--color-line-2)' }}
      >
        {label}
      </span>
    );
  }

  const marker =
    mark?.kind === 'line' ? <span className="h-0.5 w-3 shrink-0 rounded-full" style={{ background: mark.color }} />
    : mark?.kind === 'box' ? <span className="size-2 shrink-0 rounded-xs" style={{ background: mark.color }} />
    : mark?.kind === 'dashed' ? <span className="w-3 shrink-0 border-t-2 border-dashed" style={{ borderColor: T.limit }} />
    : mark?.kind === 'ramp' ? <span className="h-1.5 w-4 shrink-0 rounded-full" style={{ background: `linear-gradient(90deg, ${HELP_RAMP.join(',')})` }} />
    : mark?.kind === 'palette' ? (
        <span className="flex shrink-0 gap-px">
          {HELP_PALETTE.map(color => <span key={color} className="size-1.5 rounded-[1px]" style={{ background: color }} />)}
        </span>
      )
    : null;

  return (
    <span className="inline-flex items-center gap-1.5 whitespace-nowrap text-text-2">
      {marker}
      {label}
    </span>
  );
}

// A chart or table off screen redraws every few seconds instead of every second: in between, React leaves the
// subtree alone because it is handed the same element again. A page of a dozen charts and tables then pays mostly
// for those in view, and what is off screen is never more than a few seconds old (or stuck loading).
const OFF_SCREEN_MS = 5000;

export function WhileVisible({ className, children }: { className: string; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(true);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const observer = new IntersectionObserver(([entry]) => setVisible(entry.isIntersecting), { rootMargin: '200px' });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  const shown = useRef(children);
  const drawnAt = useRef(0);
  const now = Date.now();
  if (visible || now - drawnAt.current >= OFF_SCREEN_MS) {
    shown.current = children;
    drawnAt.current = now;
  }
  return <div ref={ref} className={className}>{shown.current}</div>;
}
