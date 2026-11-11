export const T = {
  usage: 'var(--color-usage)',
  usageFill: 'rgba(91, 141, 239, 0.12)',
  second: 'var(--color-second)',
  limit: 'var(--color-limit)',
  warn: 'var(--color-warn)',
  crit: 'var(--color-crit)',
  grid: 'var(--color-grid)',
  axis: 'var(--color-axis)',
  line: 'var(--color-line)',
  text2: 'var(--color-text-2)',
};

export type Level = 'ok' | 'warn' | 'crit';

export const THRESHOLDS = {
  cpu: [85, 95],
  memory: [80, 90],
  disk: [80, 90],
} as const;

export function levelOf(percent: number | null | undefined, [warn, crit]: readonly [number, number]): Level {
  if (percent == null) return 'ok';
  return percent >= crit ? 'crit' : percent >= warn ? 'warn' : 'ok';
}

export const levelColor = (level: Level) => (level === 'crit' ? T.crit : level === 'warn' ? T.warn : T.usage);

const RAMP = Array.from({ length: 8 }, (_, i) => `var(--heat-${i})`);
export const heat = (percent: number) => RAMP[Math.min(RAMP.length - 1, Math.floor(Math.sqrt(Math.max(0, percent) / 100) * RAMP.length))];
export const HEAT_RAMP = RAMP;

export const OTHER_GREY = 'var(--color-other)';

export const FREE_SWATCH = 'var(--color-line-2)';

export const PALETTE = ['#5b8def', '#0d9488', '#c026d3', '#9085e9', '#0891b2', '#d55181'];
