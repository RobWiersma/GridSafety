import { useMemo, useState } from 'react';
import { bucket, lastPeriods, opsToday, periodLabel, roundedTop, type PeriodSize } from '../lib/periods';

/** One measure in a by-day / week / month report: a column in the table, and a chart choice when `chart` is set. */
export interface PeriodMetric<T> {
  key: string;
  label: string;
  value(d: T): number;
  format?(n: number): string;
  chart?: boolean;
}

const SHOWN: Record<PeriodSize, number> = { day: 14, week: 12, month: 12 };
const NOW: Record<PeriodSize, [string, string]> = { day: ['today', 'yesterday'], week: ['this week', 'last week'], month: ['this month', 'last month'] };

/**
 * A report's activity by day, week or month: a bar chart of one measure over the last 14 days, 12 weeks or 12 months (empty periods
 * included, so gaps show), how the current period compares with the one before, and a table of every period with
 * activity, newest first. Days are Pacific calendar days, weeks start on Monday, months on the 1st.
 */
export function PeriodReport<T extends { day: string }>({ days, metrics, now, onDownload }: {
  days: T[];
  metrics: PeriodMetric<T>[];
  now: string;
  /** Hands back the table's rows (header first) for a CSV download. */
  onDownload?(size: PeriodSize, rows: (string | number)[][]): void;
}) {
  const [size, setSize] = useState<PeriodSize>('day');
  const charted = metrics.filter((m) => m.chart);
  const [metricKey, setMetricKey] = useState(charted[0]?.key);
  const mi = Math.max(0, metrics.findIndex((m) => m.key === metricKey));
  const metric = metrics[mi];
  const fmt = (m: PeriodMetric<T>, n: number) => (m.format ? m.format(n) : String(Math.round(n * 10) / 10));

  const sums = useMemo(() => bucket(days, size, metrics.map((m) => m.value)), [days, size, metrics]);
  const today = opsToday(now);
  const shown = lastPeriods(today, size, SHOWN[size]);
  const series = shown.map((p) => sums.get(p)?.[mi] ?? 0);
  const rows = [...sums.entries()].sort(([a], [b]) => b.localeCompare(a));

  if (days.length === 0) return null;

  const cur = series[series.length - 1], prev = series[series.length - 2];
  const unit = size;
  const change = prev > 0 ? Math.round(((cur - prev) / prev) * 100) : null;

  return (
    <section className="period-report">
      <div className="period-head">
        <h3 className="report-subhead">Activity</h3>
        <div className="chips" role="tablist" aria-label="Period">
          {(['day', 'week', 'month'] as const).map((s) => (
            <button key={s} role="tab" aria-selected={size === s} className={`chip ${size === s ? 'chip-active' : ''}`} onClick={() => setSize(s)}>
              By {s}
            </button>
          ))}
        </div>
        {onDownload && (
          <button className="btn btn-ghost btn-xs period-download"
            onClick={() => onDownload(size, [[size === 'week' ? 'week_starting' : size, ...metrics.map((m) => m.key)], ...rows.map(([p, v]) => [p, ...v.map((n) => Math.round(n * 100) / 100)])])}>
            CSV by {unit}
          </button>
        )}
      </div>
      <div className="chips period-metrics" role="tablist" aria-label="Measure to chart">
        {charted.map((m) => (
          <button key={m.key} role="tab" aria-selected={m.key === metric.key} className={`chip ${m.key === metric.key ? 'chip-active' : ''}`} onClick={() => setMetricKey(m.key)}>
            {m.label}
          </button>
        ))}
      </div>
      <p className="small period-compare">
        {metric.label} {NOW[size][0]}: <strong>{fmt(metric, cur)}</strong>
        <span className="muted">
          {' · '}{NOW[size][1]}: {fmt(metric, prev)}
          {change != null && change !== 0 && <> ({change > 0 ? '+' : ''}{change}%)</>}
          {' · '}average over the last {SHOWN[size]} {unit}s: {fmt(metric, series.reduce((a, b) => a + b, 0) / series.length)}
        </span>
      </p>
      <BarChart periods={shown} values={series} size={size} label={metric.label} format={(n) => fmt(metric, n)} whole={!metric.format} />
      <div className="table-wrap">
        <table className="table table-compact">
          <thead><tr><th>{size[0].toUpperCase() + size.slice(1)}</th>{metrics.map((m) => <th key={m.key}>{m.label}</th>)}</tr></thead>
          <tbody>
            {rows.map(([p, v]) => (
              <tr key={p}>
                <td className="nowrap">{periodLabel(p, size)}</td>
                {metrics.map((m, i) => <td key={m.key}>{fmt(m, v[i])}</td>)}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

/** One series, so no legend: the chips above name it. Hover shows the exact value. */
function BarChart({ periods, values, size, label, format, whole }: {
  periods: string[]; values: number[]; size: PeriodSize; label: string; format(n: number): string;
  /** Counts: keep the axis ticks whole numbers. */
  whole: boolean;
}) {
  const [hover, setHover] = useState<number | null>(null);
  const W = 1000, H = 170, PAD_L = 40, PAD_B = 22, PAD_T = 8;
  const max = Math.max(...values);
  const axisTop = niceMax(max);
  const nice = whole ? Math.max(2, axisTop % 2 ? axisTop * 2 : axisTop) : axisTop; // whole counts: an even top keeps the midline tick whole
  const slot = (W - PAD_L) / periods.length;
  const barW = Math.max(2, slot - 6);
  const y = (v: number) => PAD_T + (H - PAD_T - PAD_B) * (1 - v / nice);
  const labelEvery = Math.ceil(periods.length / 7);

  return (
    <div className="visit-chart period-chart">
      <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label={`${label} per ${size}`} onMouseLeave={() => setHover(null)}>
        {[0, nice / 2, nice].map((t) => (
          <g key={t}>
            <line x1={PAD_L} x2={W} y1={y(t)} y2={y(t)} className="grid-line" />
            <text x={PAD_L - 6} y={y(t) + 3} textAnchor="end" className="axis-label">{max === 0 && t > 0 ? '' : format(t)}</text>
          </g>
        ))}
        {periods.map((p, i) => {
          const x = PAD_L + i * slot + 3;
          const top = y(values[i]);
          const bh = H - PAD_B - top;
          const n = periods.length - 1 - i;
          return (
            <g key={p} onMouseEnter={() => setHover(i)}>
              <rect x={PAD_L + i * slot} y={PAD_T} width={slot} height={H - PAD_T - PAD_B} fill="transparent" />
              {bh > 0 && <path d={roundedTop(x, top, barW, bh, Math.min(4, barW / 2))} className={`visit-bar ${hover === i ? 'visit-bar-hover' : ''}`} />}
              {n % labelEvery === 0 && <text x={x + barW / 2} y={H - 6} textAnchor="middle" className="axis-label">{periodLabel(p, size, true)}</text>}
            </g>
          );
        })}
      </svg>
      {hover != null && (
        <div className="chart-tip" style={{ left: `${((PAD_L + hover * slot + slot / 2) / W) * 100}%` }}>
          <strong>{periodLabel(periods[hover], size)}</strong>
          <div>{label}: {format(values[hover])}</div>
        </div>
      )}
      <table className="sr-only">
        <caption>{label} per {size}</caption>
        <tbody>{periods.map((p, i) => <tr key={p}><td>{periodLabel(p, size)}</td><td>{format(values[i])}</td></tr>)}</tbody>
      </table>
    </div>
  );
}

/** A round axis top: 1, 2, 4, 5 or 10 times a power of ten, so the midline tick stays tidy. */
function niceMax(max: number) {
  if (max <= 0) return 1;
  const p = 10 ** Math.floor(Math.log10(max));
  return ([1, 2, 4, 5, 10].map((m) => m * p).find((v) => v >= max) ?? 10 * p);
}
