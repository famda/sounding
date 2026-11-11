type N = number | null | undefined;

export function fmtBytes(bytes: N, digits = 1): string {
  if (bytes == null) return '–';
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  let value = bytes;
  let i = 0;
  while (Math.abs(value) >= 1024 && i < units.length - 1) { value /= 1024; i++; }
  if (i === 0) return `${value.toFixed(0)} ${units[i]}`;
  const effectiveDigits = i >= 3 ? (Math.abs(value - Math.round(value)) < 0.05 ? 0 : Math.max(1, digits)) : digits;
  return `${value.toFixed(effectiveDigits)} ${units[i]}`;
}

export const fmtRate = (v: N, compact = false) => (v == null ? '–' : `${fmtBytes(v, compact ? 0 : 1)}/s`);

export const fmtPct = (v: N, compact = false) => (v == null ? '–' : `${v.toFixed(compact || v >= 100 ? 0 : 1)}%`);

const coreDigits = (v: number) => (v === 0 || v >= 10 ? 0 : v < 0.1 ? 3 : v < 1 ? 2 : 1);
export const fmtCores = (v: N, compact = false) =>
  v == null ? '–' : compact ? v.toFixed(coreDigits(v)) : `${v.toFixed(Math.max(2, coreDigits(v)))} cores`;

export function fmtDuration(seconds: N): string {
  if (seconds == null) return '–';
  const s = Math.max(0, seconds);
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
  return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : m ? `${m}m ${Math.floor(s % 60)}s` : `${Math.floor(s)}s`;
}

// toLocaleTimeString with options builds a formatter on every call, and charts format every tick each second.
const clockSeconds = new Intl.DateTimeFormat([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const clockMinutes = new Intl.DateTimeFormat([], { hour: '2-digit', minute: '2-digit' });

export const fmtClock = (t: number, seconds: boolean) => (seconds ? clockSeconds : clockMinutes).format(t);

export const pct = (part: N, whole: N) => (part != null && whole ? (100 * part) / whole : null);

export const sum = <T,>(items: T[], pick: (item: T) => N) => items.reduce((total, item) => total + (pick(item) ?? 0), 0);

export const niceCeil = (value: number) => {
  if (value <= 0) return 1;
  const pow = 10 ** Math.floor(Math.log10(value));
  const n = value / pow;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * pow;
};

export const niceCeilBytes = (value: number) => {
  let unit = 1;
  while (value / unit >= 1024) unit *= 1024;
  const scaled = value / unit;
  return (scaled <= 1 ? 1 : 2 ** Math.ceil(Math.log2(scaled))) * unit;
};
