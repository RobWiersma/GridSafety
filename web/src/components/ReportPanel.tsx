import { useRates, useReport, type AnyReport, type ReportTarget } from '../api';
import { REST_HOURS, SHIFT_HOURS } from '../lib/field';
import { FINDING_TYPE_LABEL, miles, money, pct } from '../lib/format';
import type { PeriodSize } from '../lib/periods';
import type { ActivityDay, FlightDay, FlightReport, MaintenanceEvent, UpkeepCosts, JobOutcome, ShiftStatus, TechnicianReport, WorkDay, WorkReport } from '../types';
import { ShiftMeter } from './CrewsPanel';
import { PeriodReport, type PeriodMetric } from './PeriodReport';
import { FuelGauge, MaintenanceLog, PackBars, UpkeepPill } from './Upkeep';

/** An asset's maintenance: where it stands now, then its log. */
function UpkeepSection({ events, children }: { events: MaintenanceEvent[]; children: React.ReactNode }) {
  return (
    <section>
      <h3 className="report-subhead">Maintenance</h3>
      <div className="upkeep-head">{children}</div>
      <MaintenanceLog events={events} />
    </section>
  );
}

const SHIFT_STATUS: Record<ShiftStatus, string> = { Working: 'Working', OnCall: 'Back at depot', Resting: 'Resting', Done: 'Done' };
const OUTCOME: Record<JobOutcome, string> = { Repaired: 'Repaired', CalledBack: 'Called back', InProgress: 'In progress', LinedUp: 'Lined up' };
const ALERT_LABEL: Record<string, string> = {
  LowBattery: 'Low battery', CriticalBattery: 'Critical battery', CorridorBreach: 'Corridor breach',
  AltitudeCeiling: 'Altitude ceiling', WeakSignal: 'Weak signal', LinkCritical: 'Link critical',
};

const hours = (n: number) => `${n.toFixed(1)} h`;

const WORK_METRICS: PeriodMetric<WorkDay>[] = [
  { key: 'jobs', label: 'Jobs', value: (d) => d.jobs, chart: true },
  { key: 'trips', label: 'Trips', value: (d) => d.trips },
  { key: 'repaired', label: 'Repairs', value: (d) => d.repaired, chart: true },
  { key: 'called_back', label: 'Called back', value: (d) => d.calledBack },
  { key: 'drive_h', label: 'Driving', value: (d) => d.driveHours, format: hours },
  { key: 'truck_h', label: 'Truck hours', value: (d) => d.driveHours + d.repairHours, format: hours, chart: true },
  { key: 'person_h', label: 'Person-hours', value: (d) => d.personHours, format: hours, chart: true },
  { key: 'labor_cost', label: 'Labor cost', value: (d) => d.laborCost, format: money, chart: true },
];

/** A technician's own hours: the truck's time on the jobs they were on (person-hours would count their truck-mates too). */
const TECH_METRICS: PeriodMetric<WorkDay>[] = [
  { key: 'hours', label: 'Hours worked', value: (d) => d.driveHours + d.repairHours, format: hours, chart: true },
  { key: 'jobs', label: 'Jobs', value: (d) => d.jobs, chart: true },
  { key: 'repaired', label: 'Repairs', value: (d) => d.repaired, chart: true },
  { key: 'called_back', label: 'Called back', value: (d) => d.calledBack },
  { key: 'drive_h', label: 'Driving', value: (d) => d.driveHours, format: hours },
  { key: 'repair_h', label: 'Repairing', value: (d) => d.repairHours, format: hours },
];

/** Their own hours at the labor rate. */
const techMetrics = (rate: number): PeriodMetric<WorkDay>[] => [
  ...TECH_METRICS,
  { key: 'labor_cost', label: 'Labor cost', value: (d) => (d.driveHours + d.repairHours) * rate, format: money, chart: true },
];

const FLIGHT_METRICS: PeriodMetric<FlightDay>[] = [
  { key: 'missions', label: 'Missions', value: (d) => d.missions, chart: true },
  { key: 'aborted', label: 'Aborted', value: (d) => d.aborted },
  { key: 'flight_h', label: 'Flight time', value: (d) => d.flightHours, format: (n) => (n < 1 ? `${Math.round(n * 60)} min` : hours(n)), chart: true },
  { key: 'distance_mi', label: 'Distance', value: (d) => d.distanceMeters, format: (n) => `${miles(n)} mi`, chart: true },
  { key: 'structures', label: 'Structures', value: (d) => d.structuresInspected },
  { key: 'findings', label: 'Findings', value: (d) => d.findings, chart: true },
  { key: 'alerts', label: 'Alerts', value: (d) => d.alerts },
  { key: 'crew_cost', label: 'Crew cost', value: (d) => d.crewCost, format: money, chart: true },
];

const ACTIVITY_METRICS: PeriodMetric<ActivityDay>[] = [
  { key: 'total_cost', label: 'Total cost', value: (d) => d.totalCost, format: money, chart: true },
  { key: 'missions', label: 'Missions', value: (d) => d.missions, chart: true },
  { key: 'flight_h', label: 'Flight time', value: (d) => d.flightHours, format: (n) => (n < 1 ? `${Math.round(n * 60)} min` : hours(n)), chart: true },
  { key: 'distance_mi', label: 'Distance', value: (d) => d.distanceMeters, format: (n) => `${miles(n)} mi` },
  { key: 'found', label: 'Found', value: (d) => d.found, chart: true },
  { key: 'found_sev4_5', label: 'Sev 4–5', value: (d) => d.foundSevere },
  { key: 'triaged', label: 'Triaged', value: (d) => d.triaged, chart: true },
  { key: 'dismissed', label: 'Dismissed', value: (d) => d.dismissed },
  { key: 'missed_triage_sla', label: 'Missed SLA', value: (d) => d.escalated },
  { key: 'trips', label: 'Trips', value: (d) => d.trips },
  { key: 'jobs', label: 'Jobs', value: (d) => d.jobs, chart: true },
  { key: 'repaired', label: 'Repairs', value: (d) => d.repaired, chart: true },
  { key: 'called_back', label: 'Called back', value: (d) => d.calledBack },
  { key: 'person_h', label: 'Person-hours', value: (d) => d.personHours, format: hours, chart: true },
  { key: 'refuels', label: 'Refuels', value: (d) => d.refuels },
  { key: 'gallons', label: 'Gallons', value: (d) => d.gallons, format: (n) => n.toFixed(1) },
  { key: 'fuel_cost', label: 'Fuel cost', value: (d) => d.fuelCost, format: (n) => `$${Math.round(n).toLocaleString()}`, chart: true },
  { key: 'battery_swaps', label: 'Battery swaps', value: (d) => d.batterySwaps },
  { key: 'packs_replaced', label: 'Packs replaced', value: (d) => d.packsReplaced },
  { key: 'shop_visits', label: 'Service & repairs', value: (d) => d.shopVisits, chart: true },
  { key: 'labor_cost', label: 'Labor cost', value: (d) => d.laborCost, format: money },
  { key: 'flight_cost', label: 'Flight crew cost', value: (d) => d.flightCost, format: money },
  { key: 'maintenance_cost', label: 'Maintenance cost', value: (d) => d.maintenanceCost, format: money },
];

const when = (iso?: string) => (iso ? new Date(iso).toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }) : '–');
const time = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });

/**
 * A report for any resource, with a CSV download: shift reports for a technician, work reports for a truck, a ground crew
 * or a yard, and the flight log for a drone, a pilot or a flight crew. Shown on the Reports tab.
 */
export function ReportPanel({ target }: { target: ReportTarget }) {
  const report = useReport(target);
  const rates = useRates().data;
  const data = report.data;
  const techRate = rates?.laborPerPersonHour ?? 0;

  const title = titleOf(target);
  const csv = data ? toCsv(target, data) : null;
  const periodCsv = (size: PeriodSize, rows: (string | number)[][]) =>
    download(fileOf(target).replace(/\.csv$/, `-by-${size}.csv`), rows.map((r) => r.map(csvCell).join(',')).join('\r\n'));

  return (
    <section className="panel report-panel" aria-label={title}>
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
        </div>
      </header>
      {report.isPending && <p className="muted">Loading…</p>}
      {report.isError && <p className="error">Couldn't load the report: {report.error.message}</p>}
      {data && 'days' in data && !('shifts' in data) && (
        <>
          <p className="muted small">Days are California days. Flight time is real; field hours are simulated (1 s = 1 min).</p>
          <ActivityCosts days={data.days} />
          {data.days.length === 0 ? <p className="muted">Nothing has happened yet. Fly a demo mission to start.</p>
            : <PeriodReport days={data.days} metrics={ACTIVITY_METRICS} now={data.now} onDownload={periodCsv} />}
        </>
      )}
      {data && 'shifts' in data && (
        <ShiftReports data={data} rate={techRate}>
          <PeriodReport days={data.days} metrics={techMetrics(techRate)} now={data.now} onDownload={periodCsv} />
        </ShiftReports>
      )}
      {data && 'report' in data && (
        <FlightLog report={data.report} by={target.kind === 'pilot' ? 'drone' : 'pilot'} upkeep={'upkeep' in data ? data.upkeep.costs : undefined}>
          <PeriodReport days={data.report.days} metrics={FLIGHT_METRICS} now={data.now} onDownload={periodCsv} />
        </FlightLog>
      )}
      {data && 'work' in data && (
        <WorkLog work={data.work} by={target.kind === 'depot' ? 'truck' : 'person'}
          upkeep={'upkeep' in data ? data.upkeep.costs : 'truckUpkeep' in data ? data.truckUpkeep : undefined}>
          <PeriodReport days={data.work.days} metrics={WORK_METRICS} now={data.now} onDownload={periodCsv} />
        </WorkLog>
      )}
      {data && 'upkeep' in data && 'drone' in data && (
        <UpkeepSection events={data.upkeep.events}>
          <UpkeepPill state={data.upkeep.state} until={data.upkeep.busyUntil} note={data.upkeep.note} />
          <PackBars u={data.upkeep} />
          <span className="small muted">Inspection {data.upkeep.hoursToInspection < 0.1 ? 'due' : `in ${data.upkeep.hoursToInspection.toFixed(1)} flight h`}</span>
        </UpkeepSection>
      )}
      {data && 'upkeep' in data && 'vehicle' in data && (
        <UpkeepSection events={data.upkeep.events}>
          <UpkeepPill state={data.upkeep.state} until={data.upkeep.busyUntil} note={data.upkeep.note} />
          <FuelGauge u={data.upkeep} />
          <span className="small muted">Service in {data.upkeep.milesToService} mi · {data.upkeep.milesDriven} mi driven</span>
        </UpkeepSection>
      )}
    </section>
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
    case 'activity': return 'All activity';
  }
}

/** A short file name for the CSV: "shifts-lenny-leonardo", "work-t-071", "flights-air-1". */
function fileOf(t: ReportTarget) {
  if (t.kind === 'activity') return 'activity.csv';
  const kind = t.kind === 'technician' ? 'shifts' : t.kind === 'truck' || t.kind === 'ground-crew' || t.kind === 'depot' ? 'work' : 'flights';
  const who = 'member' in t ? t.member : 'crewId' in t ? t.crewId : t.id;
  return `${kind}-${who.toLowerCase().replace(/[^a-z0-9]+/g, '-')}.csv`;
}

function subtitle(target: ReportTarget, data: AnyReport | undefined) {
  if (!data) return '';
  if (!('shifts' in data) && 'days' in data) return 'Flights, findings, triage and field work across the whole operation';
  if ('vehicle' in data) return `${data.kindLabel}, ${data.vehicle.seats} seats · kept at ${data.depotName ?? 'no yard'}`;
  if ('specialty' in data) return `${data.specialty}${data.depotName ? ` · departs ${data.depotName}` : ''} · every job its people were on, repairing or riding along`;
  if ('depot' in data) return `${data.trucks} truck${data.trucks === 1 ? '' : 's'} · ${data.people} technician${data.people === 1 ? '' : 's'} depart from here`;
  if ('homeBase' in data) return `${data.homeBase} · missions flown by its pilots`;
  if ('shifts' in data) return `${data.role}, ${data.crewName}${data.depotName ? ` · departs ${data.depotName}` : ''}`;
  if ('drone' in data) return `${data.drone.model} · ${data.drone.homeBase}`;
  return `${data.role}, ${data.crewName}${target.kind === 'pilot' && data.role !== 'Remote pilot (PIC)' ? ' · missions record the pilot in command only' : ''}`;
}

function ShiftReports({ data, rate, children }: { data: TechnicianReport; rate: number; children?: React.ReactNode }) {
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
        <Tile label="Labor cost">{money(worked * rate)}{repaired ? <span className="muted small"> · {money((worked * rate) / repaired)} a repair</span> : ''}</Tile>
      </div>
      <p className="muted small">
        Each shift is up to {SHIFT_HOURS} h of driving and repairs, then {REST_HOURS} h off. Hours are simulated (1 s = 1 min); times are when it happened.
      </p>
      {children}
      {shifts.length > 0 && <h3 className="report-subhead">Shifts</h3>}
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

function FlightLog({ report, by, upkeep, children }: { report: FlightReport; by: 'pilot' | 'drone'; upkeep?: UpkeepCosts; children?: React.ReactNode }) {
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
      <CostTiles main={['Crew time', t.crewCost]} upkeep={upkeep} per={['mission', t.missions]} />
      {alerts.length > 0 && (
        <p className="small">Alerts: {alerts.map(([k, n]) => `${ALERT_LABEL[k] ?? k} ${n}`).join(' · ')}</p>
      )}
      {children}
      {report.flights.length > 0 && <h3 className="report-subhead">Missions</h3>}
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

function WorkLog({ work, by, upkeep, children }: { work: WorkReport; by: 'person' | 'truck'; upkeep?: UpkeepCosts; children?: React.ReactNode }) {
  const t = work.totals;
  return (
    <>
      <div className="report-tiles">
        <Tile label="Trips">{t.trips}<span className="muted small"> · {t.jobs} job{t.jobs === 1 ? '' : 's'}</span></Tile>
        <Tile label="Repairs">{t.repaired}{t.calledBack ? <span className="muted small"> · {t.calledBack} called back</span> : ''}</Tile>
        <Tile label="Truck time">{(t.driveHours + t.repairHours).toFixed(1)} h<span className="muted small"> · {t.driveHours} driving</span></Tile>
        <Tile label="Person-hours">{t.personHours} h</Tile>
      </div>
      <CostTiles main={['Labor', t.laborCost]} upkeep={upkeep} per={['job', t.jobs]} />
      <p className="muted small">
        Hours are simulated (1 s = 1 min). Person-hours count everyone in the truck; a trip that went straight on from one job to the next counts once.
      </p>
      {children}
      {work.breakdown.length > 0 && <h3 className="report-subhead">{by === 'person' ? 'By person' : 'By truck'}</h3>}
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

/** Where the money went: labor or crew time, then upkeep when there is any, the total and a unit cost. */
function CostTiles({ main, upkeep, per }: { main: [string, number]; upkeep?: UpkeepCosts; per?: [string, number] }) {
  const total = main[1] + (upkeep?.total ?? 0);
  return (
    <div className="report-tiles cost-tiles">
      <Tile label={main[0]}>{money(main[1])}</Tile>
      {upkeep && (
        <>
          {upkeep.fuel > 0 && <Tile label="Fuel">{money(upkeep.fuel)}</Tile>}
          {upkeep.batteries > 0 && <Tile label="Batteries">{money(upkeep.batteries)}</Tile>}
          <Tile label="Service & repairs">{money(upkeep.service + upkeep.repairs)}</Tile>
        </>
      )}
      <Tile label="Total cost">{money(total)}{per && per[1] > 0 ? <span className="muted small"> · {money(total / per[1])} a {per[0]}</span> : ''}</Tile>
    </div>
  );
}

/** All activity's cost totals, across every day on record. */
function ActivityCosts({ days }: { days: ActivityDay[] }) {
  const sum = (f: (d: ActivityDay) => number) => days.reduce((a, d) => a + f(d), 0);
  const total = sum((d) => d.totalCost), repaired = sum((d) => d.repaired);
  return (
    <div className="report-tiles cost-tiles">
      <Tile label="Total cost">{money(total)}{repaired ? <span className="muted small"> · {money(total / repaired)} a repair</span> : ''}</Tile>
      <Tile label="Field labor">{money(sum((d) => d.laborCost))}</Tile>
      <Tile label="Flight crews">{money(sum((d) => d.flightCost))}</Tile>
      <Tile label="Fuel">{money(sum((d) => d.fuelCost))}</Tile>
      <Tile label="Maintenance">{money(sum((d) => d.maintenanceCost))}</Tile>
    </div>
  );
}

function Tile({ label, children }: { label: string; children: React.ReactNode }) {
  return <div className="report-tile"><span className="muted small">{label}</span><strong>{children}</strong></div>;
}

/** Every row of the report as CSV (one row per job for shifts, one per mission for flight logs). */
function toCsv(target: ReportTarget, data: AnyReport): { name: string; text: string } {
  const rows: unknown[][] = [];
  if (!('shifts' in data) && 'days' in data) {
    rows.push(['day', ...ACTIVITY_METRICS.map((m) => m.key)]);
    for (const d of [...data.days].reverse()) rows.push([d.day, ...ACTIVITY_METRICS.map((m) => m.value(d))]);
  } else if ('work' in data) {
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
  return { name: fileOf(target), text: rows.map((r) => r.map(csvCell).join(',')).join('\r\n') };
}

function csvCell(v: unknown) {
  const s = v == null ? '' : String(v);
  return /[",\n]/.test(s) || /^[=+\-@]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; // also defuse spreadsheet formulas
}

function download(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }));
  const a = Object.assign(document.createElement('a'), { href: url, download: name });
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
