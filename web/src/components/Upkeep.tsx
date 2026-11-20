import { useEffect, useRef, useState, type FormEvent } from 'react';
import { useFinishRepair, useLogMaintenance } from '../api';
import { money } from '../lib/format';
import type { DroneUpkeep, MaintenanceEvent, MaintenanceKind, TruckUpkeep, UpkeepState } from '../types';

export const UPKEEP_LABEL: Record<UpkeepState, string> = {
  Ready: 'Ready', Refueling: 'Refueling', Charging: 'Charging', Servicing: 'In service', InShop: 'In the shop', OutOfService: 'Out of service',
};
export const MAINTENANCE_LABEL: Record<MaintenanceKind, string> = {
  Refuel: 'Refuel', Service: 'Service', Inspection: 'Inspection', BatterySwap: 'Battery swap', BatteryReplaced: 'Battery replaced', Repair: 'Repair',
};

const clock = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
const when = (iso: string) => new Date(iso).toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

/** Ready, or what's keeping it at the yard and until when. Paired with a text label, never color alone. */
export function UpkeepPill({ state, until, note }: { state: UpkeepState; until?: string; note?: string }) {
  if (state === 'Ready') return <span className="pill pill-res-active">Ready</span>;
  return (
    <span className={`pill upkeep-pill upkeep-${state.toLowerCase()}`} title={note}>
      {UPKEEP_LABEL[state]}{until ? ` till ${clock(until)}` : ''}
    </span>
  );
}

/** A fuel gauge: the bar is the tank, with the % and range in words beside it. */
export function FuelGauge({ u }: { u: TruckUpkeep }) {
  const low = u.fuelPct < 25;
  return (
    <div className="gauge" title={`${Math.round(u.fuelPct * u.tankGallons / 100)} of ${u.tankGallons} gal`}>
      <span className="gauge-bar" aria-hidden="true"><span className={low ? 'gauge-fill gauge-low' : 'gauge-fill'} style={{ width: `${u.fuelPct}%` }} /></span>
      <span className="small">{Math.round(u.fuelPct)}%<span className="muted"> · {u.rangeMiles} mi</span></span>
    </div>
  );
}

/** Each battery pack as a small bar (charge), in use or charging, with its health in the tooltip. */
export function PackBars({ u }: { u: DroneUpkeep }) {
  return (
    <div className="packs">
      <span className="packs-row" aria-hidden="true">
        {u.packs.map((p) => (
          <span key={p.id} className={`pack ${p.inUse ? 'pack-flying' : p.chargePct < 90 ? 'pack-charging' : ''}`}
            title={`Pack ${p.id}: ${p.inUse ? 'in the drone' : `${p.chargePct}% charged`}, ${p.healthPct}% health (${p.cycles} cycles)`}>
            <span className="pack-fill" style={{ height: `${p.chargePct}%` }} />
          </span>
        ))}
      </span>
      <span className="small">
        {u.readyPacks} of {u.packs.length} charged
        {u.readyPacks < u.packs.length && u.nextReadyAt && <span className="muted"> · next {clock(u.nextReadyAt)}</span>}
      </span>
    </div>
  );
}

/**
 * Log a repair (in the shop for some hours, or until marked back) or a service. Trucks run on demo time, so their hours
 * are simulated; drones fly in real time.
 */
export function MaintenanceDialog({ kind, id, onClose, onDone }: { kind: 'drones' | 'vehicles'; id: string; onClose(): void; onDone(text: string): void }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => ref.current?.showModal(), []);
  const log = useLogMaintenance();
  const [what, setWhat] = useState<'Repair' | 'Service'>('Repair');
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    const hours = Number(f.get('hours') || 0);
    setError(null);
    try {
      const cost = String(f.get('cost') ?? '').trim();
      await log.mutateAsync({ kind, id, body: {
        kind: what, hours: what === 'Repair' && hours > 0 ? hours : undefined, note: String(f.get('note') ?? '').trim() || undefined,
        cost: cost ? Number(cost) : undefined,
      } });
      onDone(what === 'Repair' ? `${id} is in the shop` : `Service logged for ${id}`);
      ref.current?.close();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <dialog ref={ref} className="dialog" onClose={onClose} onCancel={onClose}>
      <form onSubmit={submit}>
        <h2>Maintenance · {id}</h2>
        <div className="chips" role="radiogroup" aria-label="What to log">
          {(['Repair', 'Service'] as const).map((k) => (
            <button key={k} type="button" role="radio" aria-checked={what === k} className={`chip ${what === k ? 'chip-active' : ''}`} onClick={() => setWhat(k)}>
              {k === 'Repair' ? 'Repair: into the shop' : 'Service done'}
            </button>
          ))}
        </div>
        {what === 'Repair' ? (
          <label>
            Hours in the shop
            <input className="input" name="hours" type="number" min={0} max={720} step={0.5} placeholder="Leave empty until it's marked back" />
            <span className="muted small">{kind === 'vehicles' ? 'Simulated hours (1 s = 1 min), like the rest of the field work.' : 'Real hours: drones fly in real time.'} It can't be sent out meanwhile.</span>
          </label>
        ) : (
          <p className="muted small">Resets the service counter ({kind === 'vehicles' ? 'every 300 road miles' : 'an inspection every 2 flight hours'}).</p>
        )}
        <label>
          Note
          <input className="input" name="note" maxLength={200} placeholder={what === 'Repair' ? 'e.g. Hydraulic leak on the boom' : 'e.g. New tires'} />
        </label>
        <label>
          Cost ($)
          <input className="input" name="cost" type="number" min={0} max={1000000} step={1}
            placeholder={`Leave empty for the standard ${money(what === 'Repair' ? (kind === 'vehicles' ? 1200 : 600) : (kind === 'vehicles' ? 650 : 150))}`} />
        </label>
        {error && <div className="banner banner-error">{error}</div>}
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => ref.current?.close()}>Cancel</button>
          <button className="btn btn-primary" disabled={log.isPending}>{log.isPending ? 'Saving…' : 'Log it'}</button>
        </div>
      </form>
    </dialog>
  );
}

/** "Back in service": ends the open repair keeping it in the shop. */
export function BackInServiceButton({ events, onDone }: { events: MaintenanceEvent[]; onDone(text: string): void }) {
  const finish = useFinishRepair();
  const open = events.find((e) => e.kind === 'Repair' && e.recordId && (!e.until || Date.parse(e.until) > Date.now()));
  if (!open?.recordId) return null;
  return (
    <button className="btn btn-ghost" disabled={finish.isPending}
      onClick={() => finish.mutate(open.recordId!, { onSuccess: () => onDone(`${open.assetId} is back in service`), onError: (e) => onDone(e.message) })}>
      Back in service
    </button>
  );
}

/** An asset's maintenance log, newest first: refuels, charging, service and repairs. */
export function MaintenanceLog({ events, max = 60 }: { events: MaintenanceEvent[]; max?: number }) {
  if (events.length === 0) return <p className="muted">No maintenance yet.</p>;
  const shown = events.filter((e) => e.kind !== 'BatterySwap').length >= 8 ? events.filter((e) => e.kind !== 'BatterySwap') : events;
  return (
    <div className="table-wrap">
      <table className="table table-compact">
        <thead><tr><th>When</th><th>What</th><th>Detail</th><th>Until</th><th>Cost</th><th>By</th></tr></thead>
        <tbody>
          {shown.slice(0, max).map((e, i) => (
            <tr key={`${e.at}-${e.kind}-${i}`}>
              <td className="nowrap">{when(e.at)}</td>
              <td>{MAINTENANCE_LABEL[e.kind]}</td>
              <td>{e.detail}</td>
              <td className="nowrap">{e.until && e.until !== e.at ? clock(e.until) : e.kind === 'Repair' && !e.until ? 'Until marked back' : '–'}</td>
              <td className="nowrap">{e.cost != null ? money(e.cost) : '–'}{e.gallons ? <span className="muted small"> · {e.gallons} gal</span> : null}</td>
              <td>{e.by ?? <span className="muted">Simulated</span>}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {shown !== events && <p className="muted small">Battery swaps after every flight are left out here.</p>}
    </div>
  );
}
