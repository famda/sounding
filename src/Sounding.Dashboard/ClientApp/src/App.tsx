import { useCallback, useEffect, useMemo, useRef } from 'react';
import { DetailPage } from './components/Details';
import { PerformanceWorkspace } from './components/PerformanceWorkspace';
import { Logo } from './components/Icons';
import { LiveBadge, ScopePicker } from './components/ScopePicker';
import { ThemeSwitch } from './components/ThemeSwitch';
import { RANGES, resolve, type Scope, useFleet, useNow, useScope, useScopeTimeline, useStored, useView, VIEW_LABELS, viewsOf } from './data';
import { cards, nameOf, trends } from './models';

// React Flow is only loaded when the map is opened.

export default function App() {
  const [scope, setScope] = useScope();
  const [view, setView] = useView();
  const [range, setRange] = useStored<number>('sounding:range', 300, v => RANGES.some(r => r.seconds === v));
  const { fleet, status, reconnects } = useFleet(scope.hostId, scope.containerId);
  const { timeline, version, historyStatus, retryHistory } = useScopeTimeline(scope, range, fleet, reconnects);
  const now = useNow();

  const r = useMemo(() => resolve(scope, fleet), [scope, fleet]);

  const opened = useRef(false);
  useEffect(() => {
    if (!fleet || opened.current) return;
    opened.current = true;
    if (fleet.hosts.length === 1 && !scope.hostId && !location.search) {
      setScope({ hostId: fleet.hosts[0].hostId, containerId: null });
    }
  }, [fleet, scope.hostId, setScope]);

  const openView = view && viewsOf(scope).includes(view) ? view : null;

  const changeScope = useCallback((next: Scope) => {
    if (view && !viewsOf(next).includes(view)) setView(null);
    setScope(next);
  }, [view, setView, setScope]);

  useEffect(() => {
    document.title = `${openView ? `${VIEW_LABELS[openView]} · ` : ''}${(r.container ? nameOf(r.container) : scope.containerId) ?? r.host?.displayName ?? 'All hosts'} · Sounding`;
  }, [openView, scope.containerId, r.host?.displayName]);

  const resourceCards = useMemo(() => cards(r, scope), [r, scope]);
  const trendData = useMemo(() => trends(r, timeline, scope), [r, timeline, version, scope]);
  const activeDetail = scope.hostId && fleet ? fleet.details[scope.hostId] : undefined;

  if (!fleet) {
    return (
      <div className="flex h-screen items-center justify-center bg-bg text-xs text-muted">
        {status === 'error' ? 'Unable to connect. Retrying…' : status === 'stale' ? 'Waiting for fresh data…' : 'Connecting…'}
      </div>
    );
  }

  if (fleet.hosts.length === 0) {
    return (
      <div className="flex h-screen items-center justify-center bg-bg text-xs text-muted">
        {status === 'live' ? 'No hosts reporting yet.' : 'Waiting for fresh data…'}
      </div>
    );
  }

  const start = now - range * 1000;

  return (
    <div className="flex min-h-screen flex-col bg-bg lg:h-screen lg:overflow-hidden">
      <PerformanceWorkspace
        title={
          <div className="flex min-w-0 items-center gap-3">
            <span className="flex shrink-0 items-center gap-2">
              <Logo />
              <span className="hidden text-sm font-semibold tracking-tight text-text md:inline">Sounding</span>
            </span>
            <span className="hidden h-4 w-px shrink-0 bg-line-2 md:block" />
            <ScopePicker
              r={r}
              scope={scope}
              onScope={changeScope}
              views={{ current: openView, onChange: setView, available: viewsOf(scope) }}
            />
          </div>
        }
        headerExtra={<div className="flex items-center gap-3">
          <LiveBadge status={status} />
          <span role="status" className="text-[11px] text-muted">
            {historyStatus === 'retrying' ? 'History unavailable. Retrying…' : historyStatus === 'empty' ? 'No recorded history in this range.' : historyStatus === 'error' ? <>
              History unavailable. <button type="button" className="underline" onClick={retryHistory}>Retry history</button>
            </> : null}
          </span>
        </div>}
        headerEnd={<ThemeSwitch />}
        help
        onOpen={setView}
        body={openView ? (
          <DetailPage
            view={openView}
            r={r}
            scope={scope}
            onScope={changeScope}
            onOpen={setView}
            cards={resourceCards}
            data={trendData}
            detail={activeDetail}
            bucketSeconds={timeline.bucketSeconds}
            version={version}
            start={start}
            end={now}
            range={range}
          />
        ) : undefined}
        r={r}
        scope={scope}
        onScope={changeScope}
        cards={resourceCards}
        data={trendData}
        detail={activeDetail}
        bucketSeconds={timeline.bucketSeconds}
        version={version}
        start={start}
        end={now}
        range={range}
        onRange={setRange}
      />
    </div>
  );
}
