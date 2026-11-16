import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ApiError, useCircuits, useDemoFlight, useMe, useMissions, useMissionStatus, useOpenReport, usePlanMission } from '../api';
import { MISSION_STATUS_LABEL, circuitsByVoltage, miles, pct, relativeTime } from '../lib/format';
import { useSession } from '../session';
import type { MissionView } from '../types';
import type { ToastMessage } from './Toast';

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
        onToast({ kind: 'success', text: `Demo drone launched over ${m.circuitName}` });
        onShowOnMap();
      },
      onError: (e) => onToast({ kind: 'error', text: e.message }),
    });
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

      {missions.data && missions.data.length === 0 && <p className="empty-row muted">No missions yet. Plan one to get started.</p>}
      {missions.data && missions.data.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Mission</th><th>Circuit</th><th>Drone / PIC</th><th>Status</th><th>Coverage</th><th>Distance</th><th>Findings</th><th>Alerts</th><th />
              </tr>
            </thead>
            <tbody>
              {missions.data.map((m) => (
                <tr key={m.mission.id}>
                  <td><button className="mission-link mono" onClick={() => onOpenMission(m.mission.id)}>{m.mission.id}</button><div className="muted small">{relativeTime(m.mission.createdAt)}</div></td>
                  <td>{m.circuitName}</td>
                  <td>{m.mission.droneId}<div className="muted small">{m.mission.pilot}</div></td>
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
      {planned && <SimulatorHint mission={planned} onClose={() => setPlanned(null)} />}
    </section>
  );
}

function PlanDialog({ onClose, onPlanned }: { onClose(): void; onPlanned(m: MissionView): void }) {
  const circuits = useCircuits();
  const plan = usePlanMission();
  const ref = useRef<HTMLDialogElement>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => ref.current?.showModal(), []);

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
        <div className="grid-2">
          <label>
            Drone ID
            <input className="input" name="droneId" defaultValue="DRN-07" required pattern="[A-Za-z0-9-]{1,40}" />
          </label>
          <label>
            Pilot in command
            <input className="input" name="pilot" defaultValue="J. Rivera (Part 107)" required maxLength={100} />
          </label>
        </div>
        {error && <div className="banner banner-error">{error}</div>}
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => ref.current?.close()}>Cancel</button>
          <button className="btn btn-primary" disabled={plan.isPending}>{plan.isPending ? 'Planning…' : 'Plan mission'}</button>
        </div>
      </form>
    </dialog>
  );
}

function SimulatorHint({ mission, onClose }: { mission: MissionView; onClose(): void }) {
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
          To fly it with the simulator{devMode ? '' : ' (publishes to AWS IoT Core with your AWS credentials)'}:
        </p>
        <pre className="code">{cmd}</pre>
        <div className="dialog-actions">
          <button className="btn btn-secondary" onClick={() => navigator.clipboard?.writeText(cmd)}>Copy</button>
          <button className="btn btn-primary" onClick={() => ref.current?.close()}>Done</button>
        </div>
      </div>
    </dialog>
  );
}
