import { useMemo } from 'react';
import { useFieldJobs, useStats } from '../api';
import { peopleOf, phaseAt, tripKey, useServerClock } from '../lib/field';
import { severityColor } from '../lib/format';

export function StatsBar({ onOpenFindings }: { onOpenFindings(): void }) {
  const { data } = useStats();
  const field = useFieldJobs();
  const now = useServerClock(field.data?.now);
  const v = (n: number | undefined | null, suffix = '') => (n == null ? '–' : `${n}${suffix}`);

  // Each technician counts once (riders too), by what their truck is doing right now.
  const crew = useMemo(() => {
    if (!field.data) return null;
    const byPerson = new Map<string, string[]>();
    const trucks = new Set<string>();
    for (const j of field.data.jobs) {
      const phase = phaseAt(j.dispatch, now);
      if (phase === 'Back' || phase === 'MovedOn') continue;
      trucks.add(tripKey(j.dispatch));
      for (const who of peopleOf(j.dispatch)) byPerson.set(who, [...(byPerson.get(who) ?? []), phase]);
    }
    let working = 0, traveling = 0;
    for (const phases of byPerson.values()) {
      if (phases.includes('OnSite')) working++;
      else if (phases.includes('EnRoute') || phases.includes('Returning')) traveling++;
    }
    return { out: byPerson.size, working, traveling, trucks: trucks.size };
  }, [field.data, now]);

  return (
    <section className="stats stats-grouped" aria-label="Operations summary">
      <div className="stat-group stat-group-2" role="group" aria-labelledby="stats-drones">
        <h2 className="stat-group-title" id="stats-drones">Drones</h2>
        <div className="stat">
          <div className="stat-label">Flying</div>
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
      </div>

      <div className="stat-group stat-group-4" role="group" aria-labelledby="stats-crews">
        <h2 className="stat-group-title" id="stats-crews">Field crews</h2>
        <div className="stat">
          <div className="stat-label">In the field</div>
          <div className="stat-value">
            {v(crew?.out)}
            {crew && crew.out > 0 && <span className="live-dot" aria-label="live" />}
          </div>
          <div className="stat-sub">technicians out</div>
        </div>
        <div className="stat">
          <div className="stat-label">Working</div>
          <div className="stat-value">{v(crew?.working)}</div>
          <div className="stat-sub">on site, repairing</div>
        </div>
        <div className="stat">
          <div className="stat-label">Traveling</div>
          <div className="stat-value">{v(crew?.traveling)}</div>
          <div className="stat-sub">driving to or from a job</div>
        </div>
        <div className="stat">
          <div className="stat-label">Trucks out</div>
          <div className="stat-value">{v(crew?.trucks)}</div>
          <div className="stat-sub">{crew && crew.trucks > 0 ? `${(crew.out / crew.trucks).toFixed(1)} people per truck` : 'all at the yards'}</div>
        </div>
      </div>

      <div className="stat-group stat-group-3" role="group" aria-labelledby="stats-triage">
        <h2 className="stat-group-title" id="stats-triage">Findings &amp; triage</h2>
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
          <div className="stat-label">To triage</div>
          <div className="stat-value">{data?.medianHoursToTriage != null ? `${data.medianHoursToTriage}h` : '–'}</div>
          <div className="stat-sub">median</div>
        </div>
      </div>
    </section>
  );
}
