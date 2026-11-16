import { useStats } from '../api';
import { severityColor } from '../lib/format';

export function StatsBar({ onOpenFindings }: { onOpenFindings(): void }) {
  const { data } = useStats();
  const v = (n: number | undefined | null, suffix = '') => (n == null ? '–' : `${n}${suffix}`);

  return (
    <section className="stats" aria-label="Operations summary">
      <div className="stat">
        <div className="stat-label">Drones flying</div>
        <div className="stat-value">
          {v(data?.dronesFlying)}
          {data && data.dronesFlying > 0 && <span className="live-dot" aria-label="live" />}
        </div>
      </div>
      <div className="stat">
        <div className="stat-label">Patrols · 7d</div>
        <div className="stat-value">{v(data?.missionsLast7Days)}</div>
        <div className="stat-sub">{data ? `${data.milesFlownLast7Days} mi flown` : ' '}</div>
      </div>
      <button className="stat stat-button" onClick={onOpenFindings}>
        <div className="stat-label">Open findings</div>
        <div className="stat-value">{v(data?.openFindings)}</div>
        <div className="sev-bar" aria-label="Open findings by severity">
          {data &&
            [5, 4, 3, 2, 1].map((s) =>
              data.openBySeverity[s] ? (
                <span key={s} style={{ flex: data.openBySeverity[s], background: severityColor(s) }} title={`Sev ${s}: ${data.openBySeverity[s]}`} />
              ) : null,
            )}
        </div>
      </button>
      <button className={`stat stat-button ${data?.overdueTriage ? 'stat-alert' : ''}`} onClick={onOpenFindings}>
        <div className="stat-label">Overdue triage</div>
        <div className="stat-value">{v(data?.overdueTriage)}</div>
        <div className="stat-sub">past SLA, still New</div>
      </button>
      <div className="stat">
        <div className="stat-label">Median time to triage</div>
        <div className="stat-value">{data?.medianHoursToTriage != null ? `${data.medianHoursToTriage}h` : '–'}</div>
      </div>
    </section>
  );
}
