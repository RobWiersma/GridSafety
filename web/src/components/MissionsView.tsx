import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { ApiError, useCircuits, useDemoFlight, useMe, useMissions, useMissionStatus, useOpenReport, usePlanMission, useResources } from '../api';
import { MISSION_STATUS_LABEL, circuitsByVoltage, miles, pct, relativeTime } from '../lib/format';
import { useSession } from '../session';
import type { MissionView } from '../types';
import { FilterBar, matchesSearch, SearchFilter, SelectFilter, ToggleFilter } from './FilterBar';
import { SimulateButton } from './SimulateButton';
import type { ToastMessage } from './Toast';

const STATUS_CHIPS = [
  ['all', 'All'], ['active', 'Active'], ['Planned', 'Planned'], ['InFlight', 'In flight'], ['Completed', 'Completed'], ['Aborted', 'Aborted'],
] as const;
type StatusChip = (typeof STATUS_CHIPS)[number][0];
const KIND_OPTIONS = [['any', 'Demo and planned'], ['demo', 'Demo flights'], ['planned', 'Planned missions']] as const;
const WHEN_OPTIONS = [['any', 'Any time'], ['1', 'Last 24 hours'], ['7', 'Last 7 days'], ['30', 'Last 30 days']] as const;
type Kind = (typeof KIND_OPTIONS)[number][0];
type When = (typeof WHEN_OPTIONS)[number][0];

export function MissionsView({ onToast, onShowOnMap, onOpenMission }: { onToast(t: ToastMessage): void; onShowOnMap(): void; onOpenMission(id: string): void }) {
  const missions = useMissions();
  const status = useMissionStatus();
  const openReport = useOpenReport();
  const me = useMe();
  const demo = useDemoFlight();
  const isSupervisor = Boolean(me.data?.isSupervisor);
  const { guest } = useSession();

  const flyDemo = () =>
    demo.mutate(undefined, {
      onSuccess: (m) => {
        onToast({ kind: 'success', text: `${m.mission.name ?? 'Demo drone'} launched over ${m.circuitName}` });
        onShowOnMap();
      },
      onError: (e) => onToast({ kind: 'error', text: e.message }),
    });
  const [statusChip, setStatusChip] = useState<StatusChip>('all');
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState<Kind>('any');
  const [when, setWhen] = useState<When>('any');
  const [circuit, setCircuit] = useState('any');
  const [withFindings, setWithFindings] = useState(false);
  const [withAlerts, setWithAlerts] = useState(false);

  const all = missions.data ?? [];
  const circuitOptions = useMemo(
    () => [['any', 'All circuits'] as const, ...[...new Set(all.map((m) => m.circuitName))].sort().map((c) => [c, c] as const)],
    [all],
  );
  const since = when === 'any' ? 0 : Date.now() - Number(when) * 86_400_000;
  const shown = all.filter(({ mission: m, circuitName }) =>
    (statusChip === 'all' || (statusChip === 'active' ? m.status === 'Planned' || m.status === 'InFlight' : m.status === statusChip)) &&
    (kind === 'any' || (kind === 'demo') === Boolean(m.name)) &&
    (when === 'any' || Date.parse(m.createdAt) >= since) &&
    (circuit === 'any' || circuitName === circuit) &&
    (!withFindings || m.findingCount > 0) &&
    (!withAlerts || m.recentAlerts.length > 0) &&
    matchesSearch(query, m.id, m.name, circuitName, m.pilot, m.droneId));
  const active = statusChip !== 'all' || query !== '' || kind !== 'any' || when !== 'any' || circuit !== 'any' || withFindings || withAlerts;
  const clear = () => {
    setStatusChip('all'); setQuery(''); setKind('any'); setWhen('any'); setCircuit('any'); setWithFindings(false); setWithAlerts(false);
  };

  const [planning, setPlanning] = useState(false);
  const [planned, setPlanned] = useState<MissionView | null>(null);

  const abort = (m: MissionView) => {
    if (!confirm(`Abort mission ${m.mission.id}? The drone should return to launch.`)) return;
    status.mutate({ id: m.mission.id, status: 'Aborted', note: 'Aborted from console' }, {
      onSuccess: () => onToast({ kind: 'success', text: 'Mission aborted' }),
      onError: (e) => onToast({ kind: 'error', text: e.message }),
    });
  };

  return (
    <section className="panel panel-flush">
      <div className="panel-toolbar">
        <h2>Missions</h2>
        {!guest && <div className="toolbar-actions">
          <button className="btn btn-secondary" onClick={flyDemo} disabled={!isSupervisor || demo.isPending}
            title={isSupervisor ? 'Fly a simulated drone over a random line now (about 2–4 minutes)' : 'Only supervisors can launch demo flights'}>
            {isSupervisor ? '▶ ' : '🔒 '}{demo.isPending ? 'Launching…' : 'Demo flight'}
          </button>
          <button className="btn btn-primary" onClick={() => setPlanning(true)}>+ Plan mission</button>
        </div>}
      </div>

      {all.length > 0 && (
        <>
          <div className="panel-toolbar filter-chips">
            <div className="chips" role="tablist" aria-label="Mission status">
              {STATUS_CHIPS.map(([key, label]) => (
                <button key={key} role="tab" aria-selected={statusChip === key} className={`chip ${statusChip === key ? 'chip-active' : ''}`} onClick={() => setStatusChip(key)}>
                  {label}
                </button>
              ))}
            </div>
          </div>
          <FilterBar shown={shown.length} total={all.length} noun="missions" active={active} onClear={clear}>
            <SearchFilter value={query} onChange={setQuery} placeholder="Search flight, mission id, circuit, pilot, drone…" />
            <SelectFilter label="Mission type" value={kind} onChange={setKind} options={KIND_OPTIONS} />
            <SelectFilter label="Started" value={when} onChange={setWhen} options={WHEN_OPTIONS} />
            <SelectFilter label="Circuit" value={circuit} onChange={setCircuit} options={circuitOptions} />
            <ToggleFilter label="Has findings" checked={withFindings} onChange={setWithFindings} />
            <ToggleFilter label="Has alerts" checked={withAlerts} onChange={setWithAlerts} />
          </FilterBar>
        </>
      )}

      {missions.data && missions.data.length === 0 && <p className="empty-row muted">No missions yet. Plan one to get started.</p>}
      {all.length > 0 && shown.length === 0 && <p className="empty-row muted">No missions match these filters.</p>}
      {shown.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Mission</th><th>Circuit</th><th>Flight / PIC</th><th>Status</th><th>Coverage</th><th>Distance</th><th>Findings</th><th>Alerts</th><th />
              </tr>
            </thead>
            <tbody>
              {shown.map((m) => (
                <tr key={m.mission.id}>
                  <td><button className="mission-link mono" onClick={() => onOpenMission(m.mission.id)}>{m.mission.id}</button><div className="muted small">{relativeTime(m.mission.createdAt)}</div></td>
                  <td>{m.circuitName}</td>
                  <td>{m.mission.name ? <strong>{m.mission.name}</strong> : m.mission.droneId}<div className="muted small">{m.mission.name ? `Drone ${m.mission.droneId} · ` : ''}{m.mission.pilot}</div></td>
                  <td><span className={`pill pill-${m.mission.status.toLowerCase()}`}>{MISSION_STATUS_LABEL[m.mission.status]}</span></td>
                  <td>
                    <div className="progress progress-sm"><span style={{ width: pct(m.coverage) }} /></div>
                    <span className="small">{pct(m.coverage)}</span>
                  </td>
                  <td>{miles(m.mission.distanceFlownMeters)} mi</td>
                  <td>{m.mission.findingCount}</td>
                  <td>{m.mission.recentAlerts.filter((a) => a.level === 'Critical').length > 0
                    ? <span className="text-danger strong">{m.mission.recentAlerts.length}</span>
                    : m.mission.recentAlerts.length}</td>
                  <td className="row-actions">
                    <button className="btn btn-ghost" onClick={() => onOpenMission(m.mission.id)}>Overview</button>
                    <SimulateButton mission={m.mission} onToast={onToast} onLaunched={onShowOnMap} />
                    {m.mission.status === 'InFlight' && <button className="btn btn-ghost" onClick={onShowOnMap}>Map</button>}
                    {m.mission.reportKey && (
                      <button className="btn btn-secondary" onClick={() => openReport(m.mission.id).catch((e: Error) => onToast({ kind: 'error', text: e.message }))}>
                        Report
                      </button>
                    )}
                    {m.nextStatuses.includes('Aborted') && <button className="btn btn-danger-ghost" onClick={() => abort(m)}>Abort</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {planning && (
        <PlanDialog
          onClose={() => setPlanning(false)}
          onPlanned={(m) => {
            setPlanning(false);
            setPlanned(m);
          }}
        />
      )}
      {planned && <SimulatorHint mission={planned} onClose={() => setPlanned(null)} onToast={onToast} onLaunched={() => { setPlanned(null); onShowOnMap(); }} />}
    </section>
  );
}

function PlanDialog({ onClose, onPlanned }: { onClose(): void; onPlanned(m: MissionView): void }) {
  const circuits = useCircuits();
  const resources = useResources();
  const plan = usePlanMission();
  const ref = useRef<HTMLDialogElement>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => ref.current?.showModal(), []);

  const firstReadyDrone = resources.data?.drones.find(({ drone, activeMissionId }) => drone.status === 'Active' && !activeMissionId && drone.id !== 'DEMO-01')?.drone.id;
  const pilots = (resources.data?.flightCrews ?? [])
    .filter(({ crew }) => crew.status === 'Available')
    .flatMap(({ crew }) => crew.members.filter((m) => m.role === 'Remote pilot (PIC)').map((m) => ({ crewId: crew.id, value: m.name })));

  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setError(null);
    try {
      onPlanned(await plan.mutateAsync({ circuitId: String(f.get('circuitId')), droneId: String(f.get('droneId')), pilot: String(f.get('pilot')) }));
    } catch (err) {
      setError(err instanceof ApiError || err instanceof Error ? err.message : 'Failed');
    }
  };

  return (
    <dialog ref={ref} className="dialog" onClose={onClose} onCancel={onClose}>
      <form onSubmit={submit}>
        <h2>Plan inspection mission</h2>
        <label>
          Circuit
          <select className="input" name="circuitId" required>
            {circuitsByVoltage(circuits.data ?? []).map((g) => (
              <optgroup key={g.kv} label={`${g.label} (${g.circuits.length})`}>
                {g.circuits.map((c) => (
                  <option key={c.id} value={c.id}>{c.name} · {c.structures.length} structures · {(c.lengthMeters / 1000).toFixed(1)} km</option>
                ))}
              </optgroup>
            ))}
          </select>
        </label>
        {resources.data ? (
          <div className="grid-2">
            <label>
              Drone
              <select className="input" name="droneId" required defaultValue={firstReadyDrone ?? ''}>
                {!firstReadyDrone && <option value="">No drone is free</option>}
                {resources.data.drones.map(({ drone, activeMissionName }) => {
                  const why = drone.id === 'DEMO-01' ? 'demo flights only' : drone.status !== 'Active' ? drone.status.toLowerCase() : activeMissionName ? `on ${activeMissionName}` : null;
                  return (
                    <option key={drone.id} value={drone.id} disabled={Boolean(why)}>
                      {drone.id} · {drone.model}{why ? ` (${why})` : ''}
                    </option>
                  );
                })}
              </select>
            </label>
            <label>
              Pilot in command
              <select className="input" name="pilot" required defaultValue={pilots[0]?.value ?? ''}>
                {pilots.length === 0 && <option value="">No pilot is on duty</option>}
                {resources.data.flightCrews.filter(({ crew }) => crew.status === 'Available').map(({ crew }) => (
                  <optgroup key={crew.id} label={crew.name}>
                    {pilots.filter((p) => p.crewId === crew.id).map((p) => <option key={p.value} value={p.value}>{p.value}</option>)}
                  </optgroup>
                ))}
              </select>
            </label>
          </div>
        ) : (
          <div className="center"><div className="spinner" /></div>
        )}
        <p className="muted small">Drones, pilots and their duty status are managed under Resources.</p>
        {error && <div className="banner banner-error">{error}</div>}
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => ref.current?.close()}>Cancel</button>
          <button className="btn btn-primary" disabled={plan.isPending || !firstReadyDrone || pilots.length === 0}>{plan.isPending ? 'Planning…' : 'Plan mission'}</button>
        </div>
      </form>
    </dialog>
  );
}

function SimulatorHint({ mission, onClose, onToast, onLaunched }: { mission: MissionView; onClose(): void; onToast(t: ToastMessage): void; onLaunched(): void }) {
  const { devMode } = useSession();
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => ref.current?.showModal(), []);
  const m = mission.mission;
  const cmd = `dotnet run --project tools/GridSafety.Simulator -- ${devMode ? '' : '--aws '}--mission ${m.id} --circuit ${m.circuitId} --drone ${m.droneId}`;

  return (
    <dialog ref={ref} className="dialog" onClose={onClose} onCancel={onClose}>
      <div className="dialog-body">
        <h2>Mission planned ✓</h2>
        <p className="muted">
          {m.droneId} is assigned to {mission.circuitName}. The mission goes <strong>In flight</strong> automatically when the first telemetry arrives.
          Supervisors can fly it right now with a simulated drone. Or fly it from your machine with the simulator CLI{devMode ? '' : ' (publishes to AWS IoT Core with your AWS credentials)'}:
        </p>
        <pre className="code">{cmd}</pre>
        <div className="dialog-actions">
          <button className="btn btn-ghost" onClick={() => navigator.clipboard?.writeText(cmd)}>Copy command</button>
          <SimulateButton mission={m} onToast={onToast} onLaunched={onLaunched} />
          <button className="btn btn-primary" onClick={() => ref.current?.close()}>Done</button>
        </div>
      </div>
    </dialog>
  );
}
