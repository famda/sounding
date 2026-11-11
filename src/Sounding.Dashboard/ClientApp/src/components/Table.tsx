import { type PointerEvent as ReactPointerEvent, type ReactNode, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import type { Help } from '../help';
import { T } from '../theme';
import { IconChevronRight, IconMaximize, IconMinimize } from './Icons';
import { HelpTip, WhileVisible } from './ui';

// Whether tables list Sounding's own processes and traffic: one choice for every table, kept per browser.
const soundingKey = 'sounding:show-sounding';
const soundingListeners = new Set<() => void>();
let soundingShown = (() => {
  try {
    return localStorage.getItem(soundingKey) !== 'false';
  } catch {
    return true;
  }
})();

export function useShowSounding(): [boolean, (shown: boolean) => void] {
  const shown = useSyncExternalStore(
    listener => { soundingListeners.add(listener); return () => soundingListeners.delete(listener); },
    () => soundingShown,
  );
  const set = (next: boolean) => {
    soundingShown = next;
    try {
      localStorage.setItem(soundingKey, String(next));
    } catch {
    }
    soundingListeners.forEach(listener => listener());
  };
  return [shown, set];
}

export function SoundingToggle() {
  const [shown, setShown] = useShowSounding();
  return (
    <button
      type="button"
      onClick={() => setShown(!shown)}
      title="Show or hide Sounding’s own processes and traffic, in every table"
      className="rounded border border-line bg-bg px-1.5 py-px text-[11px] font-medium text-muted hover:text-text"
    >
      {shown ? 'Hide Sounding' : 'Show Sounding'}
    </button>
  );
}

export interface Col<R> {
  label: string;
  right?: boolean;
  width?: string;
  cell: (row: R) => ReactNode;
  title?: (row: R) => string;
  sort?: (row: R) => number | string | null | undefined;
}

interface Sort { label: string; desc: boolean }

function useSaved<T>(key: string | undefined, initial: T) {
  const [value, setValue] = useState<T>(() => {
    try {
      return key ? JSON.parse(localStorage.getItem(key) ?? 'null') ?? initial : initial;
    } catch {
      return initial;
    }
  });
  const save = (next: T) => {
    setValue(next);
    try {
      if (key) localStorage.setItem(key, JSON.stringify(next));
    } catch {
    }
  };
  return [value, save] as const;
}

function compare(a: number | string | null | undefined, b: number | string | null | undefined) {
  if (a == null || b == null) return a == null ? (b == null ? 0 : 1) : -1;
  return typeof a === 'number' && typeof b === 'number' ? a - b : String(a).localeCompare(String(b));
}

const WINDOWED = 150, OVERSCAN = 20;

// The element a table scrolls in: the nearest one that scrolls, or none for the page itself.
function scrollerOf(el: HTMLElement) {
  for (let p = el.parentElement; p; p = p.parentElement) {
    if (/auto|scroll/.test(getComputedStyle(p).overflowY) && p.scrollHeight > p.clientHeight + 1) return p;
  }
  return null;
}

// A table whose columns resize and sort (click a header), and whose rows can hold more rows: given childrenOf,
// the rows are the top level and a click on one that has children shows them underneath. Rows that sounding
// marks as Sounding's own are left out while the Sounding toggle hides them.
export function Table<R>({ id, rows: all, cols, rowKey, onRow, onHoverRow, childrenOf, sounding, limit, empty = 'Nothing to show.' }: {
  id?: string;
  rows: R[];
  cols: Col<R>[];
  rowKey: (row: R) => string;
  onRow?: (row: R) => void;
  onHoverRow?: (row: R | null) => void;
  childrenOf?: (row: R) => R[];
  sounding?: (row: R) => boolean;
  limit?: number;
  empty?: string;
}) {
  const [widths, setWidths] = useSaved<Record<string, number>>(id && `sounding:cols:${id}`, {});
  const [sort, setSort] = useSaved<Sort | null>(id && `sounding:sort:${id}`, null);
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const [dragging, setDragging] = useState<string | null>(null);
  const [showSounding] = useShowSounding();
  const visible = (list: R[]) => (sounding && !showSounding ? list.filter(row => !sounding(row)) : list);
  const rows = visible(all);

  // A long table draws only the rows in view and some around them, with empty space for the rest: hundreds of
  // rows redrawn every second would keep the page busy.
  const body = useRef<HTMLTableSectionElement>(null);
  const [range, setRange] = useState({ start: 0, end: 60, height: 31 });
  const measure = useRef(() => {});
  measure.current = () => {
    const el = body.current;
    if (!el) return;
    const view = scrollerOf(el);
    const offset = (view ? view.getBoundingClientRect().top : 0) - el.getBoundingClientRect().top;
    const seen = view ? view.clientHeight : window.innerHeight;
    setRange(r => {
      const height = el.querySelector<HTMLElement>('tr[data-row]')?.offsetHeight || r.height;
      const start = Math.max(0, Math.floor(offset / height) - OVERSCAN);
      const end = Math.max(start + 1, Math.ceil((offset + seen) / height) + OVERSCAN);
      return r.start === start && r.end === end && r.height === height ? r : { start, end, height };
    });
  };
  const has = rows.length > 0;
  // Any scroll (the table's box or the page; which one can change as the table grows) and any change in the
  // size of its panel, as when it is maximized.
  useEffect(() => {
    const el = body.current;
    if (!el) return;
    const update = () => measure.current();
    document.addEventListener('scroll', update, { capture: true, passive: true });
    window.addEventListener('resize', update);
    const observer = new ResizeObserver(update);
    observer.observe(el.closest('section') ?? document.documentElement);
    return () => {
      document.removeEventListener('scroll', update, { capture: true });
      window.removeEventListener('resize', update);
      observer.disconnect();
    };
  }, [has]);
  useEffect(() => measure.current());

  if (!rows.length) return <div className="py-4 text-center text-muted">{all.length ? 'Only Sounding’s own here, which is hidden.' : empty}</div>;
  // The tree is drawn in the first left-aligned column (the name), not in a right-aligned one like a PID.
  const treeColumn = childrenOf ? Math.max(0, cols.findIndex(c => !c.right)) : -1;

  const sorter = cols.find(c => c.label === sort?.label)?.sort;
  const sorted = (list: R[]) => (sorter && sort
    ? [...list].sort((a, b) => {
        const x = sorter(a), y = sorter(b);
        return x == null || y == null ? compare(x, y) : (sort.desc ? -1 : 1) * compare(x, y);
      })
    : list);
  const toggleSort = (col: Col<R>) => {
    if (!col.sort) return;
    setSort(sort?.label === col.label ? { label: col.label, desc: !sort.desc } : { label: col.label, desc: Boolean(col.right) });
  };
  const toggleOpen = (key: string) => {
    const next = new Set(open);
    if (!next.delete(key)) next.add(key);
    setOpen(next);
  };

  const startResize = (e: ReactPointerEvent<HTMLSpanElement>, label: string) => {
    e.preventDefault();
    const handle = e.currentTarget;
    const th = handle.parentElement!;
    const table = th.closest('table')!;
    const fixed: Record<string, number> = {};
    table.querySelectorAll('thead th').forEach((cell, i) => { if (cols[i]) fixed[cols[i].label] = (cell as HTMLElement).offsetWidth; });
    const startX = e.clientX;
    const startWidth = th.offsetWidth;
    let next = { ...fixed };
    handle.setPointerCapture(e.pointerId);
    setDragging(label);
    const move = (ev: PointerEvent) => {
      next = { ...fixed, [label]: Math.max(48, Math.round(startWidth + ev.clientX - startX)) };
      setWidths(next);
    };
    const up = () => {
      handle.removeEventListener('pointermove', move);
      handle.removeEventListener('pointerup', up);
      handle.removeEventListener('pointercancel', up);
      setDragging(null);
      setWidths(next);
    };
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', up);
    handle.addEventListener('pointercancel', up);
  };
  const resized = Object.keys(widths).length > 0;
  const minRem = cols.reduce((total, col) => total + (col.width ? parseFloat(col.width) : col.right ? 6.5 : 10), 0);

  type Line = { row: R; depth: number; key: string; children: R[]; expanded: boolean };
  const lines = (row: R, depth: number): Line[] => {
    const key = rowKey(row);
    const children = visible(childrenOf?.(row) ?? []);
    const expanded = children.length > 0 && open.has(key);
    return [{ row, depth, key, children, expanded }, ...(expanded ? sorted(children).flatMap(child => lines(child, depth + 1)) : [])];
  };
  const flat = sorted(rows).slice(0, limit ?? rows.length).flatMap(row => lines(row, 0));
  const [from, to] = flat.length > WINDOWED ? [Math.min(range.start, flat.length), Math.min(range.end, flat.length)] : [0, flat.length];

  const render = ({ row, depth, key, children, expanded }: Line): ReactNode => {
    const click = onRow ? () => onRow(row) : children.length ? () => toggleOpen(key) : undefined;
    return (
      <tr
        key={key}
        data-row=""
        onClick={click}
        onMouseEnter={() => onHoverRow?.(row)}
        className={`border-b border-line/50 hover:bg-surface-2/60 ${depth > 0 ? 'bg-surface-2/40' : ''} ${click ? 'cursor-pointer' : ''}`}
      >
        {cols.map((col, i) => (
          <td key={col.label} title={col.title?.(row)} className={`truncate px-2 py-1.5 ${col.right ? 'text-right font-mono' : ''}`}>
            {i === treeColumn ? (
              <span className="inline-flex items-center" style={{ paddingLeft: depth * 14 }}>
                <span className="mr-1 inline-flex w-3 shrink-0 justify-center text-muted">
                  {children.length > 0 && <IconChevronRight size={10} className={`transition-transform ${expanded ? 'rotate-90' : ''}`} />}
                </span>
                {col.cell(row)}
              </span>
            ) : col.cell(row)}
          </td>
        ))}
      </tr>
    );
  };

  return (
    <table
      className={`table-fixed border-collapse whitespace-nowrap ${resized ? '' : 'w-full'}`}
      style={resized
        ? { width: cols.reduce((total, col) => total + (widths[col.label] ?? 96), 0), minWidth: '100%' }
        : { minWidth: `${minRem}rem` }}
    >
      <colgroup>
        {cols.map(col => <col key={col.label} style={{ width: widths[col.label] ?? col.width ?? (col.right ? '6.5rem' : undefined) }} />)}
      </colgroup>
      <thead className="sticky top-0 z-10 bg-surface">
        <tr className="border-b border-line text-muted">
          {cols.map((col, i) => (
            <th
              key={col.label}
              onClick={() => toggleSort(col)}
              title={col.sort ? 'Sort' : undefined}
              className={`relative truncate px-2 py-1.5 font-medium ${col.right ? 'text-right' : 'text-left'} ${col.sort ? 'cursor-pointer select-none hover:text-text' : ''} ${sort?.label === col.label && sorter ? 'text-text' : ''}`}
            >
              {col.label}
              {sort?.label === col.label && sorter && <span className="ml-1">{sort.desc ? '↓' : '↑'}</span>}
              <span
                title="Drag to resize, double-click to reset"
                onPointerDown={e => startResize(e, col.label)}
                onClick={e => e.stopPropagation()}
                onDoubleClick={() => setWidths({})}
                className="group absolute inset-y-0 right-0 z-10 flex w-2 cursor-col-resize touch-none justify-end"
              >
                <span
                  className={`my-1.5 transition-colors ${
                    dragging === col.label ? 'w-0.5 bg-usage' : `w-px group-hover:w-0.5 group-hover:bg-muted ${i < cols.length - 1 ? 'bg-line-2' : 'bg-transparent'}`
                  }`}
                />
              </span>
            </th>
          ))}
        </tr>
      </thead>
      <tbody ref={body} onMouseLeave={() => onHoverRow?.(null)}>
        {from > 0 && <tr aria-hidden style={{ height: from * range.height }} />}
        {flat.slice(from, to).map(render)}
        {to < flat.length && <tr aria-hidden style={{ height: (flat.length - to) * range.height }} />}
      </tbody>
    </table>
  );
}

// A card. Maximized, it covers the page above the others (Escape, or a click outside, puts it back). With
// sounding, it carries the toggle that shows or hides Sounding's own rows.
export function Panel({ icon, title, help, subtitle, wide = false, fit = false, sounding = false, actions, children }: {
  icon: ReactNode; title: string; help?: Help; subtitle?: string; wide?: boolean; fit?: boolean; sounding?: boolean; actions?: ReactNode; children: ReactNode;
}) {
  const [maximized, setMaximized] = useState(false);
  useEffect(() => {
    if (!maximized) return;
    // First, so Escape only puts the panel back and does not also leave the view.
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); setMaximized(false); } };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [maximized]);
  return (
    <>
      {maximized && <div className="fixed inset-0 z-40 bg-bg/70" onClick={() => setMaximized(false)} />}
      <section
        className={`flex min-h-0 flex-col rounded border border-line bg-surface p-3 ${maximized ? 'panel-maximized fixed inset-3 z-50 shadow-2xl' : wide ? 'md:col-span-2' : ''}`}
      >
        <div className="flex items-start justify-between gap-2">
          <div className="flex items-center gap-2 text-xs font-semibold text-text">
            <span className="text-text-2">{icon}</span>
            <span>{title}</span>
          </div>
          <div className="flex items-center gap-2">
            {actions}
            {sounding && <SoundingToggle />}
            {help && <HelpTip help={help} />}
            <button
              type="button"
              onClick={() => setMaximized(!maximized)}
              title={maximized ? 'Back to the page (Esc)' : 'Use the whole page'}
              className="rounded text-muted hover:text-text-2"
            >
              {maximized ? <IconMinimize size={14} /> : <IconMaximize size={14} />}
            </button>
          </div>
        </div>
        {subtitle && <div className="mt-0.5 text-[11px] text-muted">{subtitle}</div>}
        <WhileVisible className={`mt-2 min-h-0 overflow-auto text-[11px] text-text-2 ${maximized ? 'flex-1' : fit ? '' : 'max-h-[420px]'}`}>{children}</WhileVisible>
      </section>
    </>
  );
}

export function Facts({ rows }: { rows: [string, string, boolean?][] }) {
  return (
    <div className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-1">
      {rows.map(([label, value, flag]) => (
        <div key={label} className="contents">
          <span className={label.startsWith('  ') ? 'pl-3 text-muted' : 'text-muted'}>{label.trim()}</span>
          <span className="text-right font-mono" style={{ color: flag ? T.warn : undefined }}>{value}</span>
        </div>
      ))}
    </div>
  );
}

export function Badge({ children, warn = false, title }: { children: ReactNode; warn?: boolean; title?: string }) {
  return (
    <span
      title={title}
      className="mr-1 inline-block rounded-xs border px-1 text-[10px] leading-4"
      style={warn ? { color: T.warn, borderColor: 'rgba(245, 158, 11, 0.4)' } : { color: T.text2, borderColor: 'var(--color-line-2)' }}
    >
      {children}
    </span>
  );
}

export const Count = ({ value }: { value: number | null | undefined }) =>
  value == null ? <>–</> : <span style={{ color: value > 0 ? T.warn : undefined }}>{value.toLocaleString()}</span>;
