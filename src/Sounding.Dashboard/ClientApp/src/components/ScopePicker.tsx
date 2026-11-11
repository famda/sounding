import { Fragment, type ReactNode, useEffect, useRef, useState } from 'react';
import { type LiveStatus, type Resolved, type Scope, type View, VIEW_LABELS } from '../data';
import { nameOf } from '../models';
import { IconChevronRight, IconCpu, IconEvents, IconGpu, IconMemory, IconNetwork, IconStorage, IconTopology } from './Icons';
import { ProcessTree } from './ProcessTree';

const VIEW_ICONS: Record<View | 'overview', ReactNode> = {
  overview: <IconTopology size={13} />,
  cpu: <IconCpu size={13} />,
  memory: <IconMemory size={13} />,
  gpu: <IconGpu size={13} />,
  disk: <IconStorage size={13} />,
  network: <IconNetwork size={13} />,
  events: <IconEvents size={13} />,
};

export function ScopePicker({ r, scope, onScope, views }: {
  r: Resolved;
  scope: Scope;
  onScope: (s: Scope) => void;
  views?: { current: View | null; onChange: (view: View | null) => void; available: View[] };
}) {
  const [open, setOpen] = useState<'scope' | 'view' | null>(null);
  const box = useRef<HTMLDivElement>(null);
  const state = useRef({ open, views });
  state.current = { open, views };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        if (state.current.open) setOpen(null);
        else if (state.current.views?.current) state.current.views.onChange(null);
      }
      const typing = e.target instanceof HTMLElement && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName);
      if (e.key === '/' && !typing) {
        e.preventDefault();
        setOpen('scope');
      }
    };
    const onDown = (e: MouseEvent) => {
      if (box.current && !box.current.contains(e.target as Node)) setOpen(null);
    };
    window.addEventListener('keydown', onKey);
    window.addEventListener('mousedown', onDown);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('mousedown', onDown);
    };
  }, []);

  const up = (s: Scope) => {
    if (views?.current) views.onChange(null);
    pick(s);
  };

  const pick = (s: Scope) => {
    onScope(s);
    setOpen(null);
  };

  const crumbs: { label: string; to: Scope; mono?: boolean }[] = [{ label: 'All hosts', to: { hostId: null, containerId: null } }];
  if (scope.hostId) crumbs.push({ label: r.host?.displayName ?? scope.hostId, to: { hostId: scope.hostId, containerId: null } });
  if (scope.hostId && scope.containerId) crumbs.push(r.container ? { label: nameOf(r.container), to: scope } : { label: scope.containerId, to: scope, mono: true });
  const last = crumbs.length - 1;
  const toggle = (menu: 'scope' | 'view') => setOpen(open === menu ? null : menu);

  return (
    <div ref={box} className="relative min-w-0">
      <h1 className="flex min-w-0 items-center gap-2 text-sm">
        {crumbs.map((c, i) => (
          <Fragment key={i}>
            {i > 0 && <span className="hidden text-muted sm:inline">›</span>}
            {i < last ? (
              <button type="button" onClick={() => up(c.to)} className="-mx-1 hidden truncate rounded px-1 text-text-2 hover:bg-surface-2 hover:text-text sm:inline">
                {c.label}
              </button>
            ) : (
              <MenuButton open={open === 'scope'} onClick={() => toggle('scope')} title="Switch host or container (/)" strong={!views}>
                <span className={`truncate ${c.mono ? 'font-mono' : ''}`}>{c.label}</span>
              </MenuButton>
            )}
          </Fragment>
        ))}
        {views && !views.available.length && (
          <>
            <span className="text-muted">›</span>
            <span className="shrink-0 font-semibold text-text">Overview</span>
          </>
        )}
        {views && views.available.length > 0 && (
          <>
            <span className="text-muted">›</span>
            <span className="relative shrink-0">
              <MenuButton open={open === 'view'} onClick={() => toggle('view')} title="Switch page" strong>
                {views.current ? VIEW_LABELS[views.current] : 'Overview'}
              </MenuButton>
              {open === 'view' && (
                <div className="absolute left-0 top-full z-40 mt-1.5 w-44 overflow-hidden rounded border border-line-2 bg-surface py-1 text-[13px] font-normal shadow-2xl">
                  {([null, ...views.available] as (View | null)[]).map(v => (
                    <button
                      key={v ?? 'overview'}
                      type="button"
                      onClick={() => {
                        views.onChange(v);
                        setOpen(null);
                      }}
                      className={`flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-surface-2 ${v === views.current ? 'bg-usage/15 text-text' : 'text-text-2'}`}
                    >
                      <span className="text-muted">{VIEW_ICONS[v ?? 'overview']}</span>
                      {v ? VIEW_LABELS[v] : 'Overview'}
                    </button>
                  ))}
                </div>
              )}
            </span>
          </>
        )}
      </h1>

      {open === 'scope' && (
        <div role="dialog" aria-label="Host and container picker" className="absolute left-0 top-full z-40 mt-1.5 w-[800px] max-w-[calc(100vw-2rem)] overflow-hidden rounded border border-line-2 shadow-2xl">
          <ProcessTree hosts={r.hosts} scope={scope} onScope={pick} />
        </div>
      )}
    </div>
  );
}

function MenuButton({ open, onClick, title, strong, children }: { open: boolean; onClick: () => void; title: string; strong?: boolean; children: ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-expanded={open}
      title={title}
      className={`-mx-1 inline-flex min-w-0 items-center gap-1.5 rounded px-1 hover:bg-surface-2 ${strong ? 'font-semibold text-text' : 'text-text-2 hover:text-text'} ${open ? 'bg-surface-2' : ''}`}
    >
      {children}
      <IconChevronRight size={12} className={`shrink-0 text-muted transition-transform ${open ? '-rotate-90' : 'rotate-90'}`} />
    </button>
  );
}

export function LiveBadge({ status }: { status: LiveStatus }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-[11px] text-text-2">
      <span className={`size-1.5 rounded-full ${status === 'live' ? 'bg-ok' : status === 'error' ? 'bg-crit' : 'bg-warn'}`} />
      {status === 'live' ? 'Live' : status === 'error' ? 'Reconnecting' : status === 'stale' ? 'Stale data' : 'Connecting'}
    </span>
  );
}
