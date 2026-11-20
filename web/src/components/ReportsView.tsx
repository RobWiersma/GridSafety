import { useMemo, useState } from 'react';
import { useRates, useResources, type ReportTarget } from '../api';
import { money } from '../lib/format';
import type { Resources } from '../types';
import { ReportPanel } from './ReportPanel';

const PILOT = 'Remote pilot (PIC)';

type Group = 'overview' | 'trucks' | 'ground-crews' | 'technicians' | 'depots' | 'drones' | 'flight-crews' | 'pilots';
const GROUPS: [Group, string][] = [
  ['overview', 'Overview'], ['trucks', 'Trucks'], ['ground-crews', 'Ground crews'], ['technicians', 'Technicians'], ['depots', 'Yards'],
  ['drones', 'Drones'], ['flight-crews', 'Flight crews'], ['pilots', 'Pilots'],
];

interface Item { key: string; label: string; detail?: string; target: ReportTarget }

/** A link-safe key for a report target, e.g. "truck/T-071" or "technician/sector-7g/Lenny%20Leonardo". */
export function reportKey(t: ReportTarget): string {
  if (t.kind === 'activity') return 'activity';
  const parts = 'member' in t ? [t.crewId, t.member] : 'crewId' in t ? [t.crewId] : [t.id];
  return [t.kind, ...parts.map(encodeURIComponent)].join('/');
}

function groupOf(key: string | null): Group | null {
  const kind = key?.split('/')[0];
  const g: Record<string, Group> = { activity: 'overview', truck: 'trucks', 'ground-crew': 'ground-crews', technician: 'technicians', depot: 'depots', drone: 'drones', 'flight-crew': 'flight-crews', pilot: 'pilots' };
  return kind ? g[kind] ?? null : null;
}

function itemsOf(r: Resources, g: Group): Item[] {
  const item = (target: ReportTarget, label: string, detail?: string): Item => ({ key: reportKey(target), label, detail, target });
  const depot = (id?: string) => r.depots.find((d) => d.id === id)?.name;
  switch (g) {
    case 'overview': return [item({ kind: 'activity' }, 'All activity', 'Flights, findings, triage and field work, by day, week or month')];
    case 'trucks': return r.vehicles.map((v) => item({ kind: 'truck', id: v.vehicle.id }, v.vehicle.id, `${v.kindLabel} · ${depot(v.vehicle.depotId) ?? 'no yard'}`));
    case 'ground-crews': return r.groundCrews.map(({ crew: c }) => item({ kind: 'ground-crew', crewId: c.id, name: c.name }, c.name, c.specialty));
    case 'technicians': return r.groundCrews.flatMap(({ crew: c }) => c.members.map((m) => item({ kind: 'technician', crewId: c.id, member: m.name }, m.name, `${m.role} · ${c.name}`)));
    case 'depots': return r.depots.map((d) => item({ kind: 'depot', id: d.id, name: d.name }, d.name));
    case 'drones': return r.drones.map(({ drone: d }) => item({ kind: 'drone', id: d.id }, d.id, d.model));
    case 'flight-crews': return r.flightCrews.map(({ crew: c }) => item({ kind: 'flight-crew', crewId: c.id, name: c.name }, c.name, c.homeBase));
    case 'pilots': return r.flightCrews.flatMap(({ crew: c }) => c.members.filter((m) => m.role === PILOT).map((m) => item({ kind: 'pilot', crewId: c.id, member: m.name }, m.name, c.name)));
  }
}

/**
 * Every report in one place: pick a kind of resource and one of them on the left, its report (with the by-day and
 * by-week breakdown) shows on the right. The pick is in the URL (#reports/truck/T-071), so a report can be linked.
 */
export function ReportsView({ selected, onSelect }: { selected: string | null; onSelect(key: string | null): void }) {
  const resources = useResources();
  const rates = useRates().data;
  // With nothing picked, the overview shows.
  const pick = selected ?? 'activity';
  const [group, setGroup] = useState<Group>(() => groupOf(pick) ?? 'overview');
  const [query, setQuery] = useState('');
  const data = resources.data;

  const items = useMemo(() => (data ? itemsOf(data, group) : []), [data, group]);
  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q ? items.filter((i) => `${i.label} ${i.detail ?? ''}`.toLowerCase().includes(q)) : items;
  }, [items, query]);
  // The pick, looked up across every group (a link may point anywhere); its own group stays open.
  const current = useMemo(() => {
    const g = groupOf(pick);
    return data && g ? itemsOf(data, g).find((i) => i.key === pick) ?? null : null;
  }, [data, pick]);

  const pickGroup = (g: Group) => { setGroup(g); setQuery(''); };

  return (
    <div className="reports-page">
      <aside className="panel reports-picker" aria-label="Pick a report">
        <h2>Reports</h2>
        <div className="chips reports-groups" role="tablist" aria-label="Kind of resource">
          {GROUPS.map(([g, label]) => (
            <button key={g} role="tab" aria-selected={group === g} className={`chip ${group === g ? 'chip-active' : ''}`} onClick={() => pickGroup(g)}>{label}</button>
          ))}
        </div>
        {items.length > 6 && (
          <input className="input" type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search…" aria-label="Search this list" />
        )}
        {resources.isPending && <p className="muted small">Loading…</p>}
        <ul className="reports-list">
          {shown.map((i) => (
            <li key={i.key}>
              <button className={`reports-item ${i.key === pick ? 'reports-item-active' : ''}`} aria-current={i.key === pick} onClick={() => onSelect(i.key)}>
                <span className={group === 'trucks' || group === 'drones' ? 'mono' : undefined}>{i.label}</span>
                {i.detail && <span className="muted small">{i.detail}</span>}
              </button>
            </li>
          ))}
          {data && shown.length === 0 && <li className="muted small">Nothing matches.</li>}
        </ul>
        {rates && (
          <details className="rates">
            <summary className="small">Rates behind the costs</summary>
            <dl className="small">
              <dt>Technician labor</dt><dd>{money(rates.laborPerPersonHour)} a person-hour</dd>
              <dt>Flight crew</dt><dd>{money(rates.flightCrewPerHour)} a flight hour: pilot {money(rates.pilotPerHour)}, observer {money(rates.observerPerHour)}</dd>
              <dt>Fuel</dt><dd>{money(rates.gasPerGallon)} a gallon</dd>
              <dt>Truck service</dt><dd>{money(rates.truckService)}</dd>
              <dt>Drone inspection</dt><dd>{money(rates.droneInspection)}</dd>
              <dt>Battery pack</dt><dd>{money(rates.batteryPack)}</dd>
              <dt>Repairs</dt><dd>as logged; otherwise {money(rates.truckRepairDefault)} a truck, {money(rates.droneRepairDefault)} a drone</dd>
            </dl>
            <p className="muted small">Labor and flight crew rates are the standard ones; a supervisor can set someone's own rate on Resources. Hours are costed at the rate in effect when they were worked, so a raise doesn't reprice past work.</p>
          </details>
        )}
      </aside>
      <div className="reports-main">
        {current ? <ReportPanel key={current.key} target={current.target} /> : (
          <section className="panel reports-empty">
            <h2>Pick something to report on</h2>
            <p className="muted">
              Every truck, ground crew, technician and yard has a work report; every drone, flight crew and pilot a flight log.
              Each one breaks down by day, week or month and downloads as CSV.
            </p>
            {selected && data && <p className="muted small">That report's resource no longer exists.</p>}
          </section>
        )}
      </div>
    </div>
  );
}
