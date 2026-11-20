import { useState } from 'react';
import { useVisitorStats } from '../api';
import { roundedTop } from '../lib/periods';
import type { CountView, VisitorStats } from '../types';

const RANGES = [7, 30, 90] as const;

const fmtDay = (iso: string) => new Date(`${iso}T00:00:00Z`).toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' });

/** Supervisor-only, anonymous traffic summary: daily counters only, nothing that identifies a visitor. */
export function VisitorsView() {
  const [days, setDays] = useState<number>(30);
  const stats = useVisitorStats(days);

  return (
    <div className="visitors">
      <section className="panel">
        <div className="panel-toolbar visitors-toolbar">
          <h2>Site visitors</h2>
          <div className="chips" role="tablist" aria-label="Date range">
            {RANGES.map((n) => (
              <button key={n} role="tab" aria-selected={days === n} className={`chip ${days === n ? 'chip-active' : ''}`} onClick={() => setDays(n)}>
                {n} days
              </button>
            ))}
          </div>
        </div>
        <p className="muted small">
          Anonymous and cookie-free. Each visitor is counted once per UTC day using a hash whose salt is deleted after the day,
          so the same person can't be recognized across days, and no IP addresses or full URLs are stored.
        </p>
      </section>

      {stats.isPending && <div className="center"><div className="spinner" /></div>}
      {stats.isError && <div className="banner banner-error">{stats.error.message}</div>}
      {stats.data && <Summary s={stats.data} />}
    </div>
  );
}

function Summary({ s }: { s: VisitorStats }) {
  const perDay = s.visitorDays / s.days;
  const today = s.daily[s.daily.length - 1];
  const signedInShare = s.visitorDays ? s.signedInVisitorDays / s.visitorDays : 0;
  return (
    <>
      <section className="stats" aria-label="Visitor totals">
        <Tile label="Visitors today" value={String(today?.uniqueVisitors ?? 0)} sub={`${perDay < 10 ? perDay.toFixed(1) : Math.round(perDay)} a day over ${s.days} days (UTC)`} />
        <Tile label="Visitor-days" value={s.visitorDays.toLocaleString()} sub="unique visitors, summed by day" />
        <Tile label="Page views" value={s.pageViews.toLocaleString()} sub={s.visitorDays ? `${(s.pageViews / s.visitorDays).toFixed(1)} per visitor-day` : ' '} />
        <Tile label="Signed in" value={`${Math.round(signedInShare * 100)}%`} sub={`${s.signedInVisitorDays} visitor-days`} />
      </section>

      <section className="panel">
        <h2>Unique visitors per day</h2>
        <DailyChart s={s} />
      </section>

      <div className="breakdowns">
        <Breakdown title="Referrers" items={s.referrers} note="first visit of the day" />
        <Breakdown title="Pages" items={s.pages} note="page views" labels={PAGE_LABELS} />
        <Breakdown title="Countries" items={s.countries} />
        <Breakdown title="Devices" items={s.devices} />
        <Breakdown title="Browsers" items={s.browsers} />
        <Breakdown title="Operating systems" items={s.operatingSystems} />
        <Breakdown title="Languages" items={s.languages} />
      </div>
    </>
  );
}

const PAGE_LABELS: Record<string, string> = {
  ops: 'Operations', findings: 'Findings', missions: 'Missions', mission: 'Mission overview', visitors: 'Visitors', other: 'Other',
};

function Tile({ label, value, sub }: { label: string; value: string; sub: string }) {
  return (
    <div className="stat">
      <div className="stat-label">{label}</div>
      <div className="stat-value">{value}</div>
      <div className="stat-sub">{sub}</div>
    </div>
  );
}

/** One series (unique visitors), so no legend; page views ride along in each bar's tooltip. */
function DailyChart({ s }: { s: VisitorStats }) {
  const [hover, setHover] = useState<number | null>(null);
  const W = 1000, H = 190, PAD_L = 28, PAD_B = 22, PAD_T = 8;
  const max = Math.max(1, ...s.daily.map((d) => d.uniqueVisitors));
  const nice = max <= 4 ? 4 : Math.ceil(max / 10) * 10; // even, so the midline tick is a whole number
  const slot = (W - PAD_L) / s.daily.length;
  const barW = Math.max(2, slot - 2); // 2px gap between bars
  const y = (v: number) => PAD_T + (H - PAD_T - PAD_B) * (1 - v / nice);
  const labelEvery = Math.ceil(s.daily.length / 8);
  const h = hover == null ? null : s.daily[hover];

  if (s.visitorDays === 0) return <p className="muted">No visits recorded in this range yet.</p>;

  return (
    <div className="visit-chart">
      <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label={`Unique visitors per day over the last ${s.days} days`} onMouseLeave={() => setHover(null)}>
        {[0, nice / 2, nice].map((t) => (
          <g key={t}>
            <line x1={PAD_L} x2={W} y1={y(t)} y2={y(t)} className="grid-line" />
            <text x={PAD_L - 6} y={y(t) + 3} textAnchor="end" className="axis-label">{Math.round(t)}</text>
          </g>
        ))}
        {s.daily.map((d, i) => {
          const x = PAD_L + i * slot + 1;
          const top = y(d.uniqueVisitors);
          const bh = H - PAD_B - top;
          return (
            <g key={d.day} onMouseEnter={() => setHover(i)}>
              <rect x={PAD_L + i * slot} y={PAD_T} width={slot} height={H - PAD_T - PAD_B} fill="transparent" />
              {bh > 0 && <path d={roundedTop(x, top, barW, bh, Math.min(4, barW / 2))} className={`visit-bar ${hover === i ? 'visit-bar-hover' : ''}`} />}
              {i % labelEvery === 0 && <text x={x + barW / 2} y={H - 6} textAnchor="middle" className="axis-label">{fmtDay(d.day)}</text>}
            </g>
          );
        })}
      </svg>
      {h && hover != null && (
        <div className="chart-tip" style={{ left: `${((PAD_L + hover * slot + slot / 2) / W) * 100}%` }}>
          <strong>{fmtDay(h.day)}</strong>
          <div>{h.uniqueVisitors} visitor{h.uniqueVisitors === 1 ? '' : 's'}</div>
          <div className="muted">{h.pageViews} page view{h.pageViews === 1 ? '' : 's'}</div>
        </div>
      )}
      <table className="sr-only">
        <caption>Visitors per day</caption>
        <thead><tr><th>Day</th><th>Unique visitors</th><th>Page views</th></tr></thead>
        <tbody>{s.daily.map((d) => <tr key={d.day}><td>{d.day}</td><td>{d.uniqueVisitors}</td><td>{d.pageViews}</td></tr>)}</tbody>
      </table>
    </div>
  );
}

function Breakdown({ title, items, note, labels }: { title: string; items: CountView[]; note?: string; labels?: Record<string, string> }) {
  const total = items.reduce((n, i) => n + i.count, 0);
  const max = Math.max(1, ...items.map((i) => i.count));
  return (
    <section className="panel breakdown">
      <h2>{title}{note && <span className="muted small breakdown-note"> · {note}</span>}</h2>
      {items.length === 0 ? <p className="muted small">Nothing yet.</p> : (
        <ul>
          {items.map((i) => (
            <li key={i.name} title={`${i.count} (${Math.round((i.count / total) * 100)}%)`}>
              <span className="breakdown-bar" style={{ width: `${(i.count / max) * 100}%` }} />
              <span className="breakdown-name">{labels?.[i.name] ?? i.name}</span>
              <span className="breakdown-count">{i.count}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
