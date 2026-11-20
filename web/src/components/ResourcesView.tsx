import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { ApiError, useMe, useRates, useResources, useSaveResource, type ReportTarget, type ResourceKind } from '../api';
import { FINDING_TYPE_LABEL, miles, money, relativeTime } from '../lib/format';
import { useSession } from '../session';
import { PHASE_LABEL, dispatchName, involves } from '../lib/field';
import { OutBadge, ShiftMeter } from './CrewsPanel';
import { BackInServiceButton, FuelGauge, MaintenanceDialog, PackBars, UpkeepPill } from './Upkeep';
import { EmployeeDialog, type EmployeeTarget } from './EmployeeDialog';
import type { Crew, CrewMember, CrewStatus, Depot, DroneAsset, DroneAssetStatus, FindingType, FlightCrew, Vehicle, VehicleKind, VehicleStatus } from '../types';
import type { ToastMessage } from './Toast';

const SECTIONS = [['drones', 'Drones'], ['flight-crews', 'Flight crews'], ['ground-crews', 'Ground crews'], ['vehicles', 'Trucks'], ['depots', 'Depots']] as const;
type Section = ResourceKind | 'depots';
const ALL_TYPES = Object.keys(FINDING_TYPE_LABEL) as FindingType[];
const PILOT = 'Remote pilot (PIC)';
const OBSERVER = 'Visual observer';
const DRONE_STATUS_LABEL: Record<DroneAssetStatus, string> = { Active: 'Active', Maintenance: 'Maintenance', Retired: 'Retired' };
const CREW_STATUS_LABEL: Record<CrewStatus, string> = { Available: 'On duty', OffDuty: 'Off duty' };
const VEHICLE_KIND_LABEL: Record<VehicleKind, string> = { BucketTruck: 'Bucket truck', LineTruck: 'Line truck', Pickup: 'Pickup' };
const VEHICLE_STATUS_LABEL: Record<VehicleStatus, string> = { InService: 'In service', OutOfService: 'Out of service' };
const ADD_LABEL: Record<ResourceKind, string> = { drones: 'Add drone', 'flight-crews': 'Add flight crew', 'ground-crews': 'Add ground crew', vehicles: 'Add truck' };

type Editing =
  | { kind: 'drones'; record?: DroneAsset }
  | { kind: 'flight-crews'; record?: FlightCrew }
  | { kind: 'ground-crews'; record?: Crew }
  | { kind: 'vehicles'; record?: Vehicle };

function rememberedSection(): Section {
  try {
    const v = localStorage.getItem('gs.resources.section');
    if (v === 'drones' || v === 'flight-crews' || v === 'ground-crews' || v === 'vehicles' || v === 'depots') return v;
  } catch { /* storage blocked */ }
  return 'drones';
}

/** Drones, flight crews and ground crews: everyone can look, supervisors add, edit and retire them. */
export function ResourcesView({ onToast, onOpenMission, onOpenReport }: {
  onToast(t: ToastMessage): void;
  onOpenMission(id: string): void;
  /** Opens that resource's report on the Reports tab. */
  onOpenReport(t: ReportTarget): void;
}) {
  const resources = useResources();
  const save = useSaveResource();
  const me = useMe();
  const { guest } = useSession();
  const canEdit = !guest && Boolean(me.data?.isSupervisor);
  // Pay is for signed-in users; only supervisors change it.
  const showPay = !guest;
  const rates = useRates().data;
  const [employee, setEmployee] = useState<EmployeeTarget | null>(null);
  const [section, setSection] = useState<Section>(rememberedSection);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [servicing, setServicing] = useState<{ kind: 'drones' | 'vehicles'; id: string } | null>(null);
  const done = (text: string) => onToast({ kind: 'success', text });
  const setReport = onOpenReport;

  const pick = (s: Section) => {
    setSection(s);
    try { localStorage.setItem('gs.resources.section', s); } catch { /* storage blocked */ }
  };

  const remove = (kind: ResourceKind, id: string, label: string) => {
    if (!confirm(`Remove ${label}? Past missions and work orders keep their records.`)) return;
    save.mutate({ kind, id }, {
      onSuccess: () => onToast({ kind: 'success', text: `${label} removed` }),
      onError: (e) => onToast({ kind: 'error', text: e.message }),
    });
  };

  const data = resources.data;
  const counts: Record<Section, number> = {
    drones: data?.drones.length ?? 0,
    'flight-crews': data?.flightCrews.length ?? 0,
    'ground-crews': data?.groundCrews.length ?? 0,
    vehicles: data?.vehicles?.length ?? 0,
    depots: data?.depots.length ?? 0,
  };
  const depots = data?.depots ?? [];
  const depotName = (id?: string) => depots.find((d) => d.id === id)?.name ?? 'no depot';

  return (
    <section className="panel panel-flush resources">
      <div className="panel-toolbar">
        <div>
          <h2>Resources</h2>
          <p className="muted small">The fleet and the people who fly it and fix what it finds.</p>
        </div>
        {canEdit && section !== 'depots' && (
          <button className="btn btn-primary" onClick={() => setEditing({ kind: section })}>
            + {ADD_LABEL[section]}
          </button>
        )}
      </div>
      <div className="panel-toolbar filter-chips">
        <div className="chips" role="tablist" aria-label="Resource type">
          {SECTIONS.map(([key, label]) => (
            <button key={key} role="tab" aria-selected={section === key} className={`chip ${section === key ? 'chip-active' : ''}`} onClick={() => pick(key)}>
              {label} <span className="chip-count">{counts[key]}</span>
            </button>
          ))}
        </div>
      </div>

      {resources.isPending && <div className="center"><div className="spinner" /></div>}
      {resources.isError && <div className="banner banner-error">Couldn't load resources: {resources.error.message}</div>}

      {data && section === 'drones' && (
        data.drones.length === 0 ? <p className="empty-row muted">No drones in the fleet yet.</p> : (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr><th>Drone</th><th>Home base</th><th>Status</th><th>Now</th><th>Batteries</th><th>Inspection</th><th>Missions</th><th>Flight time</th><th>Distance</th><th>Last flight</th>{canEdit && <th />}</tr>
              </thead>
              <tbody>
                {data.drones.map((d) => (
                  <tr key={d.drone.id}>
                    <td>
                      <strong className="mono">{d.drone.id}</strong>
                      <div className="muted small">{d.drone.model}</div>
                      <button className="link-button small" onClick={() => setReport({ kind: 'drone', id: d.drone.id })}>Flight log</button>
                      {d.drone.notes && <div className="muted small resource-note">{d.drone.notes}</div>}
                    </td>
                    <td>{d.drone.homeBase}</td>
                    <td>
                      {d.drone.status === 'Active' && d.upkeep.state !== 'Ready'
                        ? <UpkeepPill state={d.upkeep.state} until={d.upkeep.busyUntil} note={d.upkeep.note} />
                        : <span className={`pill pill-res-${d.drone.status.toLowerCase()}`}>{DRONE_STATUS_LABEL[d.drone.status]}</span>}
                    </td>
                    <td>
                      {d.activeMissionId ? (
                        <button className="link-button" onClick={() => onOpenMission(d.activeMissionId!)}>
                          {d.live?.status === 'Flying' ? 'Flying' : 'Assigned'} · {d.activeMissionName ?? 'mission'}
                        </button>
                      ) : <span className="muted">On the ground</span>}
                      {d.live && <div className="muted small">Battery {Math.round(d.live.batteryPct)}% · seen {relativeTime(d.live.lastSeen)}</div>}
                    </td>
                    <td><PackBars u={d.upkeep} /></td>
                    <td className="small">{d.upkeep.hoursToInspection < 0.1 ? 'Due' : `in ${d.upkeep.hoursToInspection.toFixed(1)} flight h`}</td>
                    <td>{d.missions}</td>
                    <td>{d.flightHours.toFixed(1)} h</td>
                    <td>{miles(d.distanceMeters)} mi</td>
                    <td>{d.lastFlight ? relativeTime(d.lastFlight) : <span className="muted">Never</span>}</td>
                    {canEdit && (
                      <td className="row-actions">
                        <button className="btn btn-ghost" onClick={() => setServicing({ kind: 'drones', id: d.drone.id })}>Maintenance</button>
                        <BackInServiceButton events={d.upkeep.events} onDone={done} />
                        <button className="btn btn-ghost" onClick={() => setEditing({ kind: 'drones', record: d.drone })}>Edit</button>
                        <button className="btn btn-danger-ghost" onClick={() => remove('drones', d.drone.id, d.drone.id)}>Remove</button>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )
      )}

      {data && section === 'flight-crews' && (
        <CardGrid empty="No flight crews yet.">
          {data.flightCrews.map(({ crew, missions, activeMissions, lastFlight }) => (
            <ResourceCard
              key={crew.id}
              title={crew.name}
              subtitle={crew.homeBase}
              status={crew.status}
              badge={activeMissions > 0 ? `${activeMissions} flying` : undefined}
              members={crew.members}
              payOf={showPay ? (m) => payOf(m, m.role === PILOT ? rates?.pilotPerHour : rates?.observerPerHour) : undefined}
              onEditMember={canEdit ? (m) => setEmployee({ kind: 'flight-crews', crewId: crew.id, crewName: crew.name, member: m,
                standard: (m.role === PILOT ? rates?.pilotPerHour : rates?.observerPerHour) ?? 0 }) : undefined}
              onMemberReport={(m) => setReport({ kind: 'pilot', crewId: crew.id, member: m.name })}
              memberReportLabel="Flight log"
              onReport={() => setReport({ kind: 'flight-crew', crewId: crew.id, name: crew.name })}
              reportLabel="Crew flight log"
              footer={`${missions} mission${missions === 1 ? '' : 's'}${lastFlight ? ` · last ${relativeTime(lastFlight)}` : ''}`}
              onEdit={canEdit ? () => setEditing({ kind: 'flight-crews', record: crew }) : undefined}
              onRemove={canEdit ? () => remove('flight-crews', crew.id, crew.name) : undefined}
            />
          ))}
        </CardGrid>
      )}

      {data && section === 'ground-crews' && (
        <CardGrid empty="No ground crews yet.">
          {data.groundCrews.map(({ crew, openWorkOrders, inField = [], shifts = [] }) => (
            <ResourceCard
              key={crew.id}
              title={crew.name}
              subtitle={`${crew.specialty} · departs ${depotName(crew.depotId)}`}
              status={crew.status}
              badge={`${openWorkOrders} open`}
              badgeBusy={openWorkOrders > 0}
              members={crew.members}
              payOf={showPay ? (m) => payOf(m, rates?.laborPerPersonHour) : undefined}
              onEditMember={canEdit ? (m) => setEmployee({ kind: 'ground-crews', crewId: crew.id, crewName: crew.name, member: m,
                standard: rates?.laborPerPersonHour ?? 0, handles: crew.handles }) : undefined}
              onMemberReport={(m) => setReport({ kind: 'technician', crewId: crew.id, member: m.name })}
              memberReportLabel="Shift report"
              onReport={() => setReport({ kind: 'ground-crew', crewId: crew.id, name: crew.name })}
              reportLabel="Crew work report"
              memberDetail={(m) => (
                <>
                  <OutBadge jobs={inField.filter((d) => involves(d, crew.id, m.name))} />
                  {crew.status !== 'OffDuty' && <ShiftMeter shift={shifts.find((s) => s.member === m.name)} />}
                  {m.depotId && m.depotId !== crew.depotId && <span className="muted small"> · departs {depotName(m.depotId)}</span>}
                  <span className="skill-tags">
                    {(m.skills ?? crew.handles).map((t) => <span key={t} className="skill-tag">{FINDING_TYPE_LABEL[t]}</span>)}
                  </span>
                </>
              )}
              footer={`Whole-crew work orders: ${crew.handles.map((t) => FINDING_TYPE_LABEL[t]).join(', ') || 'none'}`}
              onEdit={canEdit ? () => setEditing({ kind: 'ground-crews', record: crew }) : undefined}
              onRemove={canEdit ? () => remove('ground-crews', crew.id, crew.name) : undefined}
            />
          ))}
        </CardGrid>
      )}

      {data && section === 'vehicles' && (
        (data.vehicles ?? []).length === 0 ? <p className="empty-row muted">No trucks yet.</p> : (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr><th>Truck</th><th>Seats</th><th>Kept at</th><th>Status</th><th>Now</th><th>Fuel</th><th>Service</th><th>Jobs</th><th>Last out</th>{canEdit && <th />}</tr>
              </thead>
              <tbody>
                {[...data.vehicles].sort((a, b) => depotName(a.vehicle.depotId).localeCompare(depotName(b.vehicle.depotId)) || a.vehicle.id.localeCompare(b.vehicle.id)).map((v) => (
                  <tr key={v.vehicle.id}>
                    <td>
                      <strong className="mono">{v.vehicle.id}</strong>
                      <div className="muted small">{v.kindLabel}</div>
                      <button className="link-button small" onClick={() => setReport({ kind: 'truck', id: v.vehicle.id })}>Work report</button>
                      {v.vehicle.notes && <div className="muted small resource-note">{v.vehicle.notes}</div>}
                    </td>
                    <td>{v.vehicle.seats}</td>
                    <td>{depotName(v.vehicle.depotId)}</td>
                    <td>
                      {v.vehicle.status === 'InService' && v.upkeep.state !== 'Ready'
                        ? <UpkeepPill state={v.upkeep.state} until={v.upkeep.busyUntil} note={v.upkeep.note} />
                        : <span className={`pill pill-res-${v.vehicle.status === 'InService' ? 'active' : 'maintenance'}`}>{VEHICLE_STATUS_LABEL[v.vehicle.status]}</span>}
                      {v.upkeep.state === 'InShop' && v.upkeep.note && <div className="muted small">{v.upkeep.note}</div>}
                    </td>
                    <td>
                      {v.trip && v.phase ? (
                        <>
                          <span className="truck-out">{PHASE_LABEL[v.phase]} · {v.trip.siteLabel}</span>
                          <div className="muted small">{dispatchName(v.trip)}</div>
                        </>
                      ) : <span className="muted">{v.vehicle.status === 'InService' ? 'At the yard' : 'In the shop'}</span>}
                    </td>
                    <td><FuelGauge u={v.upkeep} /></td>
                    <td className="small nowrap">{v.upkeep.milesToService < 1 ? 'Due' : `in ${v.upkeep.milesToService} mi`}</td>
                    <td>{v.jobs}</td>
                    <td>{v.lastOut ? relativeTime(v.lastOut) : <span className="muted">Never</span>}</td>
                    {canEdit && (
                      <td className="row-actions">
                        <button className="btn btn-ghost" onClick={() => setServicing({ kind: 'vehicles', id: v.vehicle.id })}>Maintenance</button>
                        <BackInServiceButton events={v.upkeep.events} onDone={done} />
                        <button className="btn btn-ghost" onClick={() => setEditing({ kind: 'vehicles', record: v.vehicle })}>Edit</button>
                        <button className="btn btn-danger-ghost" onClick={() => remove('vehicles', v.vehicle.id, v.vehicle.id)}>Remove</button>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
            <p className="muted small depot-note">
              A technician leaving a yard takes its smallest free truck, and someone else on duty at that yard rides along when there's a seat.
              Jobs near the truck's last stop that anyone in it can fix go on the same trip instead of sending another truck,
              and repairs go quicker with more hands: two people take about 70% of the time one would.
            </p>
          </div>
        )
      )}

      {data && section === 'depots' && (
        depots.length === 0 ? <p className="empty-row muted">No depots yet.</p> : (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>Depot</th><th>Location</th><th>Departing from here</th></tr></thead>
              <tbody>
                {depots.map((d) => {
                  const departing = data.groundCrews.flatMap(({ crew }) =>
                    crew.members.filter((m) => (m.depotId ?? crew.depotId) === d.id).map((m) => ({ crew, m })));
                  return (
                    <tr key={d.id}>
                      <td>
                        <strong>{d.name}</strong>
                        <div><button className="link-button small" onClick={() => setReport({ kind: 'depot', id: d.id, name: d.name })}>Work report</button></div>
                      </td>
                      <td>
                        <a className="link" href={`https://www.google.com/maps?q=${d.location.lat},${d.location.lon}`} target="_blank" rel="noopener noreferrer">
                          {d.location.lat.toFixed(4)}, {d.location.lon.toFixed(4)} ↗
                        </a>
                      </td>
                      <td>
                        {departing.length === 0 ? <span className="muted">Nobody</span> : (
                          <ul className="depot-people">
                            {departing.map(({ crew, m }) => (
                              <li key={`${crew.id}/${m.name}`} className={crew.status === 'OffDuty' ? 'muted' : undefined}>
                                {m.name}<span className="muted small"> · {m.role}, {crew.name}{crew.status === 'OffDuty' ? ' (off duty)' : ''}</span>
                              </li>
                            ))}
                          </ul>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            <p className="muted small depot-note">The demo's yards; points are rough town centres. Each technician departs from their own depot, or their crew's.</p>
          </div>
        )
      )}


      {employee && <EmployeeDialog key={`${employee.crewId}/${employee.member.name}`} target={employee} depots={depots} onClose={() => setEmployee(null)} onDone={done} />}

      {servicing && <MaintenanceDialog key={servicing.id} kind={servicing.kind} id={servicing.id} onClose={() => setServicing(null)} onDone={done} />}


      {editing && (
        <ResourceDialog
          editing={editing}
          depots={depots}
          takenIds={new Set(
            editing.kind === 'drones' ? data?.drones.map((d) => d.drone.id) :
            editing.kind === 'flight-crews' ? data?.flightCrews.map((c) => c.crew.id) :
            editing.kind === 'vehicles' ? data?.vehicles?.map((v) => v.vehicle.id) :
            data?.groundCrews.map((c) => c.crew.id))}
          onClose={() => setEditing(null)}
          onSaved={(label) => {
            setEditing(null);
            onToast({ kind: 'success', text: `${label} saved` });
          }}
        />
      )}
    </section>
  );
}

function CardGrid({ empty, children }: { empty: string; children: ReactNode[] }) {
  return children.length === 0 ? <p className="empty-row muted">{empty}</p> : <div className="crew-grid resource-grid">{children}</div>;
}

/** Someone's hourly rate as shown on their crew card: their own, or the standard one for their role. */
function payOf(m: CrewMember, standard: number | undefined): { rate: number; own: boolean } | null {
  if (m.payRate != null) return { rate: m.payRate, own: true };
  return standard == null ? null : { rate: standard, own: false };
}

function ResourceCard({ title, subtitle, status, badge, badgeBusy, members, memberDetail, payOf: pay, onEditMember, onMemberReport, memberReportLabel, onReport, reportLabel, footer, onEdit, onRemove }: {
  title: string;
  subtitle: string;
  status: CrewStatus;
  badge?: string;
  badgeBusy?: boolean;
  members: CrewMember[];
  memberDetail?(m: CrewMember): ReactNode;
  /** Each person's hourly rate (signed-in users only). */
  payOf?(m: CrewMember): { rate: number; own: boolean } | null;
  /** Supervisors: edit one employee (details, record, pay). */
  onEditMember?(m: CrewMember): void;
  /** Makes each name open that person's report. */
  onMemberReport?(m: CrewMember): void;
  memberReportLabel?: string;
  /** The report for the whole crew. */
  onReport?(): void;
  reportLabel?: string;
  footer: string;
  onEdit?(): void;
  onRemove?(): void;
}) {
  return (
    <article className={`crew-card ${status === 'OffDuty' ? 'crew-off' : ''}`}>
      <header>
        <div>
          <h3>{title}</h3>
          <p className="muted small">{subtitle}</p>
          {onReport && <button className="link-button small" onClick={onReport}>{reportLabel ?? 'Report'}</button>}
        </div>
        <div className="resource-badges">
          <span className={`pill pill-res-${status.toLowerCase()}`}>{CREW_STATUS_LABEL[status]}</span>
          {badge && <span className={`crew-load ${badgeBusy ?? true ? 'crew-load-busy' : ''}`}>{badge}</span>}
        </div>
      </header>
      <ul className="crew-members">
        {members.map((m) => (
          <li key={m.name}>
            <span className="crew-avatar" aria-hidden="true">{m.name.split(' ').map((p) => p[0]).join('').slice(0, 2)}</span>
            <span>
              {onMemberReport
                ? <button className="link-button member-link" onClick={() => onMemberReport(m)} title={`${memberReportLabel ?? 'Report'} for ${m.name}`}>{m.name}</button>
                : m.name}
              <span className="muted small"> · {m.role}</span>
              {pay?.(m) && (() => {
                const p = pay(m)!;
                return (
                  <span className={`pay small ${p.own ? 'pay-own' : 'muted'}`} title={p.own ? 'Their own rate, set by a supervisor' : 'The standard rate for their role'}>
                    {' · '}{money(p.rate)}/h{p.own ? '' : ' std'}

                  </span>
                );
              })()}
              {onEditMember && <button className="link-button small member-edit" onClick={() => onEditMember(m)} aria-label={`Edit employee ${m.name}`}>Edit</button>}
              {memberDetail?.(m)}
            </span>
          </li>
        ))}
      </ul>
      <p className="muted small">{footer}</p>
      {(onEdit || onRemove) && (
        <div className="resource-actions">
          {onEdit && <button className="btn btn-ghost btn-xs" onClick={onEdit}>Edit</button>}
          {onRemove && <button className="btn btn-danger-ghost btn-xs" onClick={onRemove}>Remove</button>}
        </div>
      )}
    </article>
  );
}

const slug = (s: string) => s.toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40) || 'crew';

function uniqueId(base: string, taken: Set<string | undefined>) {
  let id = base;
  for (let i = 2; taken.has(id); i++) id = `${base.slice(0, 36)}-${i}`;
  return id;
}

function ResourceDialog({ editing, depots, takenIds, onClose, onSaved }: {
  editing: Editing;
  depots: Depot[];
  takenIds: Set<string | undefined>;
  onClose(): void;
  onSaved(label: string): void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const save = useSaveResource();
  const [error, setError] = useState<string | null>(null);
  useEffect(() => ref.current?.showModal(), []);

  const isNew = !editing.record;
  const [members, setMembers] = useState<CrewMember[]>(
    editing.kind === 'drones' || editing.kind === 'vehicles' ? [] : editing.record?.members ?? [{ name: '', role: editing.kind === 'flight-crews' ? PILOT : '' }],
  );
  const [handles, setHandles] = useState<FindingType[]>(editing.kind === 'ground-crews' ? editing.record?.handles ?? [] : []);

  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    const text = (k: string) => String(f.get(k) ?? '').trim();
    const crewMembers = members.map((m) => ({ ...m, name: m.name.trim(), role: m.role.trim() })).filter((m) => m.name);
    setError(null);
    try {
      if (editing.kind === 'drones') {
        const id = editing.record?.id ?? text('id').toUpperCase();
        if (isNew && takenIds.has(id)) throw new Error(`${id} is already in the fleet.`);
        await save.mutateAsync({ kind: 'drones', id, record: {
          id, model: text('model'), homeBase: text('homeBase'), status: text('status') as DroneAssetStatus, notes: text('notes') || undefined,
        } });
        onSaved(id);
      } else if (editing.kind === 'vehicles') {
        const id = editing.record?.id ?? text('id').toUpperCase();
        if (isNew && takenIds.has(id)) throw new Error(`${id} is already in the fleet.`);
        await save.mutateAsync({ kind: 'vehicles', id, record: {
          id, kind: text('kind') as VehicleKind, seats: Number(text('seats')), depotId: text('depotId'), status: text('status') as VehicleStatus,
          notes: text('notes') || undefined,
        } });
        onSaved(id);
      } else if (editing.kind === 'flight-crews') {
        const id = editing.record?.id ?? uniqueId(slug(text('name')), takenIds);
        await save.mutateAsync({ kind: 'flight-crews', id, record: {
          id, name: text('name'), homeBase: text('homeBase'), status: text('status') as CrewStatus, members: crewMembers,
        } });
        onSaved(text('name'));
      } else {
        const id = editing.record?.id ?? uniqueId(slug(text('name')), takenIds);
        await save.mutateAsync({ kind: 'ground-crews', id, record: {
          id, name: text('name'), specialty: text('specialty'), status: text('status') as CrewStatus, members: crewMembers, handles, depotId: text('depotId'),
        } });
        onSaved(text('name'));
      }
    } catch (err) {
      setError(err instanceof ApiError || err instanceof Error ? err.message : 'Failed');
    }
  };

  const title = `${isNew ? 'Add' : 'Edit'} ${{ drones: 'drone', 'flight-crews': 'flight crew', 'ground-crews': 'ground crew', vehicles: 'truck' }[editing.kind]}`;
  const setMember = (i: number, patch: Partial<CrewMember>) => setMembers((ms) => ms.map((m, j) => (j === i ? { ...m, ...patch } : m)));

  return (
    <dialog ref={ref} className="dialog" onClose={onClose} onCancel={onClose}>
      <form onSubmit={submit}>
        <h2>{title}</h2>

        {editing.kind === 'drones' && (
          <>
            <div className="grid-2">
              <label>
                Drone ID
                <input className="input mono" name="id" defaultValue={editing.record?.id} disabled={!isNew} required pattern="[A-Za-z0-9-]{1,40}" placeholder="DRN-08" />
              </label>
              <label>
                Status
                <select className="input" name="status" defaultValue={editing.record?.status ?? 'Active'}>
                  {Object.entries(DRONE_STATUS_LABEL).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                </select>
              </label>
            </div>
            <label>
              Model and payload
              <input className="input" name="model" defaultValue={editing.record?.model} required maxLength={80} placeholder="Inspection quad, zoom + thermal" />
            </label>
            <label>
              Home base
              <input className="input" name="homeBase" defaultValue={editing.record?.homeBase} required maxLength={60} placeholder="Concord yard" />
            </label>
            <label>
              Notes
              <input className="input" name="notes" defaultValue={editing.record?.notes} maxLength={200} placeholder="Optional, e.g. why it's in maintenance" />
            </label>
          </>
        )}

        {editing.kind === 'vehicles' && (
          <>
            <div className="grid-2">
              <label>
                Truck ID
                <input className="input mono" name="id" defaultValue={editing.record?.id} disabled={!isNew} required pattern="[A-Za-z0-9-]{1,20}" placeholder="T-074" />
              </label>
              <label>
                Status
                <select className="input" name="status" defaultValue={editing.record?.status ?? 'InService'}>
                  {Object.entries(VEHICLE_STATUS_LABEL).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                </select>
              </label>
            </div>
            <div className="grid-2">
              <label>
                Kind
                <select className="input" name="kind" defaultValue={editing.record?.kind ?? 'BucketTruck'}>
                  {Object.entries(VEHICLE_KIND_LABEL).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                </select>
              </label>
              <label>
                Seats
                <input className="input" name="seats" type="number" min={1} max={6} defaultValue={editing.record?.seats ?? 2} required />
              </label>
            </div>
            <label>
              Kept at
              <select className="input" name="depotId" defaultValue={editing.record?.depotId ?? depots[0]?.id} required>
                {depots.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
              </select>
            </label>
            <label>
              Notes
              <input className="input" name="notes" defaultValue={editing.record?.notes} maxLength={200} placeholder="Optional, e.g. why it's out of service" />
            </label>
          </>
        )}

        {(editing.kind === 'flight-crews' || editing.kind === 'ground-crews') && (
          <>
            <label>
              Crew name
              <input className="input" name="name" defaultValue={editing.record?.name} required maxLength={80} />
            </label>
            <div className="grid-2">
              {editing.kind === 'flight-crews' ? (
                <label>
                  Home base
                  <input className="input" name="homeBase" defaultValue={editing.record?.homeBase} required maxLength={60} />
                </label>
              ) : (
                <label>
                  Specialty
                  <input className="input" name="specialty" defaultValue={editing.record?.specialty} required maxLength={80} />
                </label>
              )}
              <label>
                Status
                <select className="input" name="status" defaultValue={editing.record?.status ?? 'Available'}>
                  {Object.entries(CREW_STATUS_LABEL).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                </select>
              </label>
            </div>

            {editing.kind === 'ground-crews' && (
              <label>
                Departs from
                <select className="input" name="depotId" defaultValue={editing.record?.depotId ?? depots[0]?.id} required>
                  {depots.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
                </select>
              </label>
            )}

            {editing.kind === 'ground-crews' && (
              <fieldset className="resource-fieldset">
                <legend>Whole-crew work orders</legend>
                <div className="handles-grid">
                  {ALL_TYPES.map((t) => (
                    <label key={t} className="check">
                      <input type="checkbox" checked={handles.includes(t)}
                        onChange={(e) => setHandles((h) => (e.target.checked ? [...h, t] : h.filter((x) => x !== t)))} />
                      {FINDING_TYPE_LABEL[t]}
                    </label>
                  ))}
                </div>
              </fieldset>
            )}

            <fieldset className="resource-fieldset">
              <legend>Members</legend>
              {members.map((m, i) => (
                <div key={i} className="member-row">
                  <input className="input" aria-label={`Member ${i + 1} name`} placeholder="Name" value={m.name} maxLength={60}
                    onChange={(e) => setMember(i, { name: e.target.value })} required={i === 0} />
                  {editing.kind === 'flight-crews' ? (
                    <select className="input" aria-label={`Member ${i + 1} role`} value={m.role} onChange={(e) => setMember(i, { role: e.target.value })}>
                      <option value={PILOT}>{PILOT}</option>
                      <option value={OBSERVER}>{OBSERVER}</option>
                    </select>
                  ) : (
                    <input className="input" aria-label={`Member ${i + 1} role`} placeholder="Role, e.g. Lineman" value={m.role} maxLength={60}
                      onChange={(e) => setMember(i, { role: e.target.value })} required={Boolean(m.name.trim())} />
                  )}
                  <button type="button" className="btn btn-ghost btn-xs" aria-label={`Remove member ${i + 1}`} disabled={members.length === 1}
                    onClick={() => setMembers((ms) => ms.filter((_, j) => j !== i))}>✕</button>
                  {editing.kind === 'ground-crews' && (
                    <div className="member-extra">
                      <label className="member-depot">
                        <span className="muted small">Departs from</span>
                        <select className="input" aria-label={`Member ${i + 1} depot`} value={m.depotId ?? ''}
                          onChange={(e) => setMember(i, { depotId: e.target.value || undefined })}>
                          <option value="">Crew's depot</option>
                          {depots.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
                        </select>
                      </label>
                      <div className="member-skills" role="group" aria-label={`Member ${i + 1} qualified to fix`}>
                        <span className="muted small">Qualified to fix</span>
                        <label className="check">
                          <input type="checkbox" checked={!m.skills} onChange={(e) => setMember(i, { skills: e.target.checked ? undefined : [...handles] })} />
                          Same as crew
                        </label>
                        {m.skills && ALL_TYPES.map((t) => (
                          <label key={t} className="check">
                            <input type="checkbox" checked={m.skills!.includes(t)}
                              onChange={(e) => setMember(i, { skills: e.target.checked ? [...m.skills!, t] : m.skills!.filter((x) => x !== t) })} />
                            {FINDING_TYPE_LABEL[t]}
                          </label>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              ))}
              {members.length < 12 && (
                <button type="button" className="link-button" onClick={() => setMembers((ms) => [...ms, { name: '', role: editing.kind === 'flight-crews' ? OBSERVER : '' }])}>
                  + Add member
                </button>
              )}
            </fieldset>
          </>
        )}

        {error && <div className="banner banner-error">{error}</div>}
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => ref.current?.close()}>Cancel</button>
          <button className="btn btn-primary" disabled={save.isPending}>{save.isPending ? 'Saving…' : 'Save'}</button>
        </div>
      </form>
    </dialog>
  );
}
