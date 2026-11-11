export const clock = {
  offset: 0,
  now: () => Date.now() + clock.offset,
};

export type Column = (number | null)[];

export class Timeline {
  t: number[] = [];
  cols = new Map<string, Column>();
  bucketSeconds = 1;
  private live: { t: number; row: Record<string, number | null> }[] = [];

  col(key: string): Column {
    let column = this.cols.get(key);
    if (!column) {
      column = new Array(this.t.length).fill(null);
      this.cols.set(key, column);
    }
    return column;
  }

  ids(prefix: string): string[] {
    return [...this.cols.keys()].filter(k => k.startsWith(prefix)).map(k => k.slice(prefix.length));
  }

  private append(t: number, row: Record<string, number | null>, minGapMs: number): boolean {
    if (this.t.length && t - this.t[this.t.length - 1] < Math.max(1, minGapMs)) return false;
    for (const key of Object.keys(row)) this.col(key);
    this.t.push(t);
    for (const [key, column] of this.cols) column.push(row[key] ?? null);
    return true;
  }

  push(t: number, row: Record<string, number | null>, cutoff: number, minGapMs = 0): boolean {
    const added = this.append(t, row, minGapMs);
    if (added) this.live.push({ t, row });
    this.prune(cutoff);
    return added;
  }

  load(t: number[], columns: Record<string, Column>, bucketSeconds: number, cutoff: number) {
    this.t = [...t];
    this.cols = new Map(Object.entries(columns).map(([key, values]) => [key, [...values]]));
    this.bucketSeconds = bucketSeconds;
    // Live points the history now covers are never shown again.
    const loaded = this.t.length ? this.t[this.t.length - 1] : -Infinity;
    this.live = this.live.filter(point => point.t > loaded);
    for (const { t: liveT, row } of this.live) this.append(liveT, row, 0);
    this.prune(cutoff);
  }

  prune(cutoff: number) {
    let n = 0;
    while (n < this.t.length && this.t[n] < cutoff) n++;
    if (n) {
      this.t.splice(0, n);
      for (const column of this.cols.values()) column.splice(0, n);
    }
    let m = 0;
    while (m < this.live.length && this.live[m].t < cutoff) m++;
    if (m) this.live.splice(0, m);
  }

  sum(keys: string[]): Column {
    const columns = keys.map(k => this.col(k));
    return this.t.map((_, i) => (columns.some(c => c[i] != null) ? columns.reduce((s, c) => s + (c[i] ?? 0), 0) : null));
  }

  mean(keys: string[]): Column {
    const columns = keys.map(k => this.col(k));
    return this.t.map((_, i) => {
      const values = columns.map(c => c[i]).filter((v): v is number => v != null);
      return values.length ? values.reduce((s, v) => s + v, 0) / values.length : null;
    });
  }
}
