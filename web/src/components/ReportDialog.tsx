import { useEffect, useRef } from 'react';
import { useReport, type AnyReport, type ReportTarget } from '../api';
import { REST_HOURS, SHIFT_HOURS } from '../lib/field';
import { FINDING_TYPE_LABEL, miles, pct } from '../lib/format';
import type { FlightReport, JobOutcome, ShiftStatus, TechnicianReport, WorkReport } from '../types';
import { ShiftMeter } from './CrewsPanel';

const SHIFT_STATUS: Record<ShiftStatus, string> = { Working: 'Working', OnCall: 'Back at depot', Resting: 'Resting', Done: 'Done' };
const OUTCOME: Record<JobOutcome, string> = { Repaired: 'Repaired', CalledBack: 'Called back', InProgress: 'In progress', LinedUp: 'Lined up' };
const ALERT_LABEL: Record<string, string> = {
  LowBattery: 'Low battery', CriticalBattery: 'Critical battery', CorridorBreach: 'Corridor breach',
  AltitudeCeiling: 'Altitude ceiling', WeakSignal: 'Weak signal', LinkCritical: 'Link critical',
};

const when = (iso?: string) => (iso ? new Date(iso).toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }) : '–');
const time = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });

/**
 * A report for any resource, with a CSV download: shift reports for a technician, work reports for a truck, a ground crew
 * or a yard, and the flight log for a drone, a pilot or a flight crew.
 */
export function ReportDialog({ target, onClose }: { target: ReportTarget; onClose(): void }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => ref.current?.showModal(), []);
  const report = useReport(target);
  const data = report.data;

  const title = titleOf(target);
  const csv = data ? toCsv(target, data) : null;

  return (
    <dialog ref={ref} className="dialog report-dialog" onClose={onClose} onCancel={onClose} aria-label={title}>
      <div className="dialog-body">
        <header className="report-head">
          <div>
            <h2>{title}</h2>
            <p className="muted small">{subtitle(target, data)}</p>
          </div>
          <div className="report-actions">
            {csv && (
              <button className="btn btn-secondary btn-xs" onClick={() => download(csv.name, csv.text)} title="Download every row as a spreadsheet (CSV)">
                Download CSV
              </button>
            )}
            <button className="btn btn-ghost btn-xs" onClick={() => ref.current?.close()} aria-label="Close">✕</button>
          </div>
        </header>
        {report.isPending && <p className="muted">Loading…</p>}
        {report.isError && <p className="error">Couldn't load the report: {report.error.message}</p>}
        {data && 'shifts' in data && <ShiftReports data={data} />}
        {data && 'report' in data && <FlightLog report={data.report} by={target.kind === 'pilot' ? 'drone' : 'pilot'} />}
        {data && 'work' in data && <WorkLog work={data.work} by={target.kind === 'depot' ? 'truck' : 'person'} />}
      </div>
    </dialog>
  );
}

function titleOf(t: ReportTarget) {
  switch (t.kind) {
    case 'drone': return `Flight log · ${t.id}`;
    case 'pilot': return `Flight log · ${t.member}`;
    case 'technician': return `Shift report · ${t.member}`;
    case 'truck': return `Work report · ${t.id}`;
    case 'ground-crew': return `Work report · ${t.name}`;
    case 'flight-crew': return `Flight log · ${t.name}`;
    case 'depot': return `Work report · ${t.name}`;
  }
}

/** A short file name for the CSV: "shifts-lenny-leonardo", "work-t-071", "flights-air-1". */
function fileOf(t: ReportTarget) {
  const kind = t.kind === 'technician' ? 'shifts' : t.kind === 'truck' || t.kind === 'ground-crew' || t.kind === 'depot' ? 'work' : 'flights';
  const who = 'member' in t ? t.member : 'crewId' in t ? t.crewId : t.id;
  return `${kind}-${who.toLowerCase().replace(/[^a-z0-9]+/g, '-')}.csv`;
}

function subtitle(target: ReportTarget, data: AnyReport | undefined) {
  if (!data) return '';
  if ('vehicle' in data) return `${data.kindLabel}, ${data.vehicle.seats} seats · kept at ${data.depotName ?? 'no yard'}`;
  if ('specialty' in data) return `${data.specialty}${data.depotName ? ` · departs ${data.depotName}` : ''} · every job its people were on, repairing or riding along`;
  if ('depot' in data) return `${data.trucks} truck${data.trucks === 1 ? '' : 's'} · ${data.people} technician${data.people === 1 ? '' : 's'} depart from here`;
  if ('homeBase' in data) return `${data.homeBase} · missions flown by its pilots`;
  if ('shifts' in data) return `${data.role}, ${data.crewName}${data.depotName ? ` · departs ${data.depotName}` : ''}`;
  if ('drone' in data) return `${data.drone.model} · ${data.drone.homeBase}`;
  return `${data.role}, ${data.crewName}${target.kind === 'pilot' && data.role !== 'Remote pilot (PIC)' ? ' · missions record the pilot in command only' : ''}`;
}

function ShiftReports({ data }: { data: TechnicianReport }) {
  const shifts = data.shifts;
  const worked = shifts.reduce((a, s) => a + s.workedHours, 0);
  const repaired = shifts.reduce((a, s) => a + s.repaired, 0);
  return (
    <>
      <div className="report-tiles">
        <Tile label="Now"><ShiftMeter shift={data.current} now={Date.parse(data.now)} /></Tile>
        <Tile label="Shifts">{shifts.length}</Tile>
        <Tile label="Hours worked">{worked.toFixed(1)} h</Tile>
        <Tile label="Repairs">{repaired}</Tile>
      </div>
      <p className="muted small">
        Each shift is up to {SHIFT_HOURS} h of driving and repairs, then {REST_HOURS} h off. Hours are simulated (1 s = 1 min); times are when it happened.
      </p>
      {shifts.length === 0 && <p className="muted">No shifts yet. Assign {data.member.split(' ')[0]} a work order to start one.</p>}
      {shifts.map((s) => (
        <section key={s.startedAt} className="report-shift">
          <header>
            <strong>{when(s.startedAt)} – {time(s.endsAt)}</strong>
            <span className={`pill shift-status shift-status-${s.status.toLowerCase()}`}>{SHIFT_STATUS[s.status]}</span>
          </header>
          <p className="small">
            <strong>{s.workedHours} h</strong> of {s.limitHours} h worked
            {s.bookedHours > s.workedHours && <> ({s.bookedHours} h booked)</>}
            {' · '}{s.driveHours} h driving · {s.repairHours} h repairing · {s.repaired} repaired
            {s.restHours != null && <> · then {s.restHours} h off</>}
          </p>
          <div className="table-wrap">
            <table className="table table-compact">
              <thead><tr><th>Structure</th><th>Defect</th><th>Truck</th><th>From</th><th>Left</th><th>Drive</th><th>Repair</th><th>Outcome</th></tr></thead>
              <tbody>
                {s.jobs.map((j) => (
                  <tr key={j.findingId}>
                    <td className="mono">{j.structureLabel}</td>
                    <td>Sev {j.severity} · {FINDING_TYPE_LABEL[j.type]}</td>
                    <td>
                      {j.vehicle ? <span className="mono">{j.vehicle.split(' · ')[0]}</span> : <span className="muted">–</span>}
                      {j.with && <span className="muted small"> with {j.with}</span>}
                    </td>
                    <td>{j.fromLabel}</td>
                    <td>{time(j.departedAt)}</td>
                    <td>{j.driveHours} h</td>
                    <td>{j.repairHours} h</td>
                    <td>{OUTCOME[j.outcome]}{j.nextSiteLabel ? `, on to ${j.nextSiteLabel}` : ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ))}
    </>
  );
}

function FlightLog({ report, by }: { report: FlightReport; by: 'pilot' | 'drone' }) {
  const t = report.totals;
  const alerts = Object.entries(t.alertsByKind).filter(([, n]) => n > 0);
  return (
    <>
      <div className="report-tiles">
        <Tile label="Missions">{t.missions}<span className="muted small"> · {t.completed} done{t.aborted ? `, ${t.aborted} aborted` : ''}</span></Tile>
        <Tile label="Flight time">{t.flightHours < 1 ? `${Math.round(t.flightHours * 60)} min` : `${t.flightHours.toFixed(1)} h`}</Tile>
        <Tile label="Distance">{miles(t.distanceMeters)} mi</Tile>
        <Tile label="Findings">{t.findings}<span className="muted small"> · {t.structuresInspected} structures</span></Tile>
      </div>
      {alerts.length > 0 && (
        <p className="small">Alerts: {alerts.map(([k, n]) => `${ALERT_LABEL[k] ?? k} ${n}`).join(' · ')}</p>
      )}
      {report.flights.length === 0 ? <p className="muted">No missions yet.</p> : (
        <div className="table-wrap">
          <table className="table table-compact">
            <thead>
              <tr><th>Mission</th><th>Circuit</th><th>{by === 'pilot' ? 'Pilot' : 'Drone'}</th><th>Flown</th><th>Time</th><th>Distance</th><th>Coverage</th><th>Findings</th><th>Alerts</th><th>Battery</th></tr>
            </thead>
            <tbody>
              {report.flights.map((f) => (
                <tr key={f.missionId}>
                  <td>{f.name ?? f.missionId.slice(0, 8)}<div className="muted small">{f.status === 'InFlight' ? 'In flight' : f.status}</div></td>
                  <td>{f.circuitName}</td>
                  <td>{by === 'pilot' ? f.pilot : <span className="mono">{f.droneId}</span>}</td>
                  <td>{when(f.startedAt ?? f.plannedAt)}</td>
                  <td>{f.startedAt ? `${f.flightMinutes} min` : '–'}</td>
                  <td>{miles(f.distanceMeters)} mi</td>
                  <td>{pct(f.coverage)}</td>
                  <td>{f.findings}</td>
                  <td>{f.alerts}{f.criticalAlerts ? <span className="muted small"> ({f.criticalAlerts} critical)</span> : ''}</td>
                  <td>{f.endBatteryPct != null ? `${Math.round(f.endBatteryPct)}%` : '–'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

function WorkLog({ work, by }: { work: WorkReport; by: 'person' | 'truck' }) {
  const t = work.totals;
  return (
    <>
      <div className="report-tiles">
        <Tile label="Trips">{t.trips}<span className="muted small"> · {t.jobs} job{t.jobs === 1 ? '' : 's'}</span></Tile>
        <Tile label="Repairs">{t.repaired}{t.calledBack ? <span className="muted small"> · {t.calledBack} called back</span> : ''}</Tile>
        <Tile label="Truck time">{(t.driveHours + t.repairHours).toFixed(1)} h<span className="muted small"> · {t.driveHours} driving</span></Tile>
        <Tile label="Person-hours">{t.personHours} h</Tile>
      </div>
      <p className="muted small">
        Hours are simulated (1 s = 1 min). Person-hours count everyone in the truck; a trip that went straight on from one job to the next counts once.
      </p>
      {work.breakdown.length > 0 && (
        <div className="table-wrap">
          <table className="table table-compact">
            <thead><tr><th>{by === 'person' ? 'Person' : 'Truck'}</th><th>Jobs</th><th>Repaired</th><th>Hours</th></tr></thead>
            <tbody>
              {work.breakdown.map((b) => (
                <tr key={b.name}>
                  <td>{by === 'truck' ? <span className="mono">{b.name}</span> : b.name}{b.detail && <span className="muted small"> · {b.detail}</span>}</td>
                  <td>{b.jobs}</td>
                  <td>{b.repaired}</td>
                  <td>{b.hours} h</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <h3 className="report-subhead">Jobs</h3>
      {work.jobs.length === 0 ? <p className="muted">No jobs yet.</p> : (
        <div className="table-wrap">
          <table className="table table-compact">
            <thead><tr><th>Structure</th><th>Defect</th><th>Repaired by</th><th>Truck</th><th>In the truck</th><th>Left</th><th>Drive</th><th>Repair</th><th>Outcome</th></tr></thead>
            <tbody>
              {work.jobs.map((j) => (
                <tr key={j.findingId}>
                  <td className="mono">{j.structureLabel}</td>
                  <td>Sev {j.severity} · {FINDING_TYPE_LABEL[j.type]}</td>
                  <td>{j.assignee.replace(/ \(.*\)$/, '')}</td>
                  <td>{j.vehicle ? <span className="mono">{j.vehicle.split(' · ')[0]}</span> : <span className="muted">–</span>}</td>
                  <td>{j.with ?? <span className="muted">–</span>}</td>
                  <td>{when(j.departedAt)}</td>
                  <td>{j.driveHours} h</td>
                  <td>{j.repairHours} h</td>
                  <td>{OUTCOME[j.outcome]}{j.nextSiteLabel ? `, on to ${j.nextSiteLabel}` : ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

function Tile({ label, children }: { label: string; children: React.ReactNode }) {
  return <div className="report-tile"><span className="muted small">{label}</span><strong>{children}</strong></div>;
}

/** Every row of the report as CSV (one row per job for shifts, one per mission for flight logs). */
function toCsv(target: ReportTarget, data: AnyReport): { name: string; text: string } {
  const cell = (v: unknown) => {
    const s = v == null ? '' : String(v);
    return /[",\n]/.test(s) || /^[=+\-@]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; // also defuse spreadsheet formulas
  };
  const rows: unknown[][] = [];
  if ('work' in data) {
    rows.push(['structure', 'defect', 'severity', 'repaired_by', 'truck', 'in_the_truck', 'from', 'departed', 'arrived', 'job_end', 'drive_h', 'repair_h', 'outcome', 'on_roads']);
    for (const j of data.work.jobs) {
      rows.push([j.structureLabel, FINDING_TYPE_LABEL[j.type], j.severity, j.assignee, j.vehicle, j.with, j.fromLabel, j.departedAt, j.arrivesAt, j.endsAt,
        j.driveHours, j.repairHours, j.outcome, j.onRoads]);
    }
  } else if ('shifts' in data) {
    rows.push(['shift_start', 'shift_end', 'shift_status', 'worked_h', 'drive_h', 'repair_h', 'rest_after_h', 'structure', 'defect', 'severity', 'done_by', 'truck', 'rode_with', 'from', 'departed', 'arrived', 'job_end', 'job_drive_h', 'job_repair_h', 'outcome', 'on_roads']);
    for (const s of data.shifts) for (const j of s.jobs) {
      rows.push([s.startedAt, s.endsAt, s.status, s.workedHours, s.driveHours, s.repairHours, s.restHours, j.structureLabel, FINDING_TYPE_LABEL[j.type], j.severity,
        j.assignee, j.vehicle, j.with, j.fromLabel, j.departedAt, j.arrivesAt, j.endsAt, j.driveHours, j.repairHours, j.outcome, j.onRoads]);
    }
  } else {
    rows.push(['mission_id', 'mission', 'status', 'circuit', 'drone', 'pilot', 'planned', 'started', 'ended', 'flight_min', 'distance_m', 'coverage', 'structures', 'findings', 'alerts', 'critical_alerts', 'end_battery_pct']);
    for (const f of data.report.flights) {
      rows.push([f.missionId, f.name, f.status, f.circuitName, f.droneId, f.pilot, f.plannedAt, f.startedAt, f.endedAt, f.flightMinutes, f.distanceMeters,
        f.coverage.toFixed(3), f.structuresInspected, f.findings, f.alerts, f.criticalAlerts, f.endBatteryPct]);
    }
  }
  return { name: fileOf(target), text: rows.map((r) => r.map(cell).join(',')).join('\r\n') };
}

function download(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }));
  const a = Object.assign(document.createElement('a'), { href: url, download: name });
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
